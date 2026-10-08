using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Planning;

/// <summary>
/// 계획 변경안(ChangeSet) 전개·검증·적용 [계획 어시스턴트 — ADR-013].
/// - 미리보기와 적용이 **같은 실행 코드**를 쓴다: 선창 계획 데이터를 추적(tracked) 엔티티로 읽어 연산을 차례로 실행하고,
///   미리보기는 저장하지 않고 버리며(ChangeTracker.Clear), 적용은 전 연산 통과 시에만 SaveChanges 1회(= 단일 트랜잭션)로 반영한다.
/// - 검증 규칙은 REST 등록/수정과 같은 정본(<see cref="AreaRules"/>·<see cref="AreaTaskRules"/>)을 쓴다.
/// - 매크로는 앞선 연산이 반영된 상태 위에서 전개된다(같은 변경안에서 영역 생성 → 그 영역에 작업 추가 가능).
/// - 범위는 계획 데이터(영역·작업·시나리오 대상)뿐 — run·배차·VDA 5050·선창 지오메트리는 건드리지 않는다.
/// </summary>
public sealed class PlanChangeSetEngine
{
    /// <summary>작업 등록 기본 sectionDxfId/profileId — 계획 화면(AreaPlanningViewModel)과 같은 임시값.</summary>
    public const string DefaultSectionDxfId = "DXF-1";
    public const string DefaultProfileId = "PROF-1";
    private const double MinCell = 0.05;   // 격자 끝 조각이 이보다 작으면 만들지 않는다 [m]

    private readonly AcsDbContext _db;
    public PlanChangeSetEngine(AcsDbContext db) => _db = db;

    public sealed class PlanConflictException(string message) : Exception(message);

    private sealed class Ws
    {
        public required string TankId;
        public required TankGeometryEntity G;
        public required TankGeometry Geom;
        public required Dictionary<string, WallEntity> Walls;
        public required List<InspectionAreaEntity> Areas;
        public required List<ScenarioEntity> Scenarios;
        public required List<ScenarioAreaEntity> ScenarioAreas;
    }

    // ══════════════════════════════════════════════════════════════
    // 공개 API
    // ══════════════════════════════════════════════════════════════

    /// <summary>변경안 미리보기 — DB 무변경. 매크로를 원자 연산으로 전개하고 연산별 통과/사유를 돌려준다.</summary>
    public async Task<PlanPreview> PreviewAsync(string tankId, IReadOnlyList<PlanOp> ops, int maxOps, CancellationToken ct = default)
    {
        var ws = await LoadAsync(tankId, ct);
        var fingerprint = Fingerprint(ws);
        var results = new List<OpResult>();
        var messages = new List<string>();
        try
        {
            for (int i = 0; i < ops.Count; i++)
            {
                foreach (var a in Expand(ws, ops[i], i, messages))
                {
                    if (results.Count >= maxOps)
                    {
                        messages.Add($"원자 연산이 상한({maxOps}건)을 넘어 나머지는 전개하지 않았습니다 — 범위를 나눠 요청하세요.");
                        return new PlanPreview(results, messages, fingerprint, AllOk: false);
                    }
                    results.Add(Execute(ws, a));
                }
            }
            return new PlanPreview(results, messages, fingerprint, results.All(r => r.Ok));
        }
        finally { _db.ChangeTracker.Clear(); }   // 미리보기는 저장하지 않는다
    }

    /// <summary>
    /// 미리보기에서 확정한 원자 연산을 적용한다. 미리보기 이후 계획 데이터가 바뀌었으면 <see cref="PlanConflictException"/>,
    /// 하나라도 실패하면 아무것도 저장하지 않고 실패 결과를 돌려준다.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<OpResult> Results)> ApplyAsync(
        string tankId, IReadOnlyList<AtomicOp> ops, string expectedFingerprint, AuditLogEntity? audit = null, CancellationToken ct = default)
    {
        var ws = await LoadAsync(tankId, ct);
        if (Fingerprint(ws) != expectedFingerprint)
        {
            _db.ChangeTracker.Clear();
            throw new PlanConflictException("미리보기 이후 계획 데이터가 바뀌었습니다 — 다시 제안받아 확인하세요.");
        }
        var results = ops.Select(a => Execute(ws, a)).ToList();
        if (results.Any(r => !r.Ok))
        {
            _db.ChangeTracker.Clear();
            return (false, results);
        }
        if (audit is not null) _db.AuditLogs.Add(audit);
        await _db.SaveChangesAsync(ct);   // 단일 SaveChanges = 단일 트랜잭션(전부 또는 전무)
        return (true, results);
    }

    // ══════════════════════════════════════════════════════════════
    // 작업 공간 로드·지문
    // ══════════════════════════════════════════════════════════════

    private async Task<Ws> LoadAsync(string tankId, CancellationToken ct)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct)
                ?? throw new InvalidOperationException($"선창 지오메트리가 없습니다: {tankId} (파라미터를 먼저 등록하세요).");
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).ToDictionaryAsync(w => w.WallCode, ct);
        var areas = await _db.InspectionAreas.Include(a => a.Tasks).Where(a => a.TankId == tankId).ToListAsync(ct);
        var scenarios = await _db.Scenarios.Where(s => s.TankId == tankId).ToListAsync(ct);
        var sids = scenarios.Select(s => s.ScenarioId).ToList();
        var sas = await _db.ScenarioAreas.Where(sa => sids.Contains(sa.ScenarioId)).ToListAsync(ct);
        return new Ws
        {
            TankId = tankId, G = g, Walls = walls, Areas = areas, Scenarios = scenarios, ScenarioAreas = sas,
            Geom = new TankGeometry(g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
                JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? Array.Empty<double>(),
                g.OriginOx, g.OriginOy, g.ReachZMin, g.ReachZMax),
        };
    }

    /// <summary>선창 계획 데이터(영역·작업·시나리오 대상) 지문 — 미리보기와 적용 사이의 변경 감지용.</summary>
    private static string Fingerprint(Ws ws)
    {
        var sb = new StringBuilder();
        foreach (var a in ws.Areas.OrderBy(a => a.AreaId))
        {
            sb.Append($"A|{a.AreaId}|{a.WallCode}|{a.Name}|{a.Corners}|{a.Level}|{a.SortOrder}|{a.StationStandoffM}\n");
            foreach (var t in a.Tasks.OrderBy(t => t.TaskId))
                sb.Append($"T|{t.TaskId}|{t.Seq}|{t.Name}|{t.SeamType}|{t.StartU:R}|{t.StartV:R}|{t.EndU:R}|{t.EndV:R}|{t.SectionDxfId}|{t.ProfileId}\n");
        }
        foreach (var s in ws.Scenarios.OrderBy(s => s.ScenarioId)) sb.Append($"S|{s.ScenarioId}|{s.Name}\n");
        foreach (var sa in ws.ScenarioAreas.OrderBy(x => x.ScenarioId).ThenBy(x => x.AreaId)) sb.Append($"SA|{sa.ScenarioId}|{sa.AreaId}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ══════════════════════════════════════════════════════════════
    // 매크로 전개 (PlanOp → AtomicOp*) — 좌표는 여기서 층-로컬 v → 면-전체 v
    // ══════════════════════════════════════════════════════════════

    private IEnumerable<AtomicOp> Expand(Ws ws, PlanOp op, int src, List<string> messages)
    {
        AtomicOp Invalid(string why) => new() { Kind = "invalid:" + why, Source = src };
        var list = new List<AtomicOp>();
        switch (op.Op)
        {
            case "createArea":
            {
                if (op.WallCode is null || !ws.Walls.ContainsKey(op.WallCode)) return [Invalid($"면 '{op.WallCode}'이(가) 없습니다.")];
                if (op.Level is not int lv) return [Invalid("createArea 에는 level 이 필요합니다(코너 v 는 층-로컬).")];
                if (VOff(ws, op.WallCode, lv) is not double off) return [Invalid($"면 {op.WallCode}는 L{lv}에서 도달할 수 없습니다.")];
                if (op.Corners is not { Length: >= 3 }) return [Invalid("createArea 에는 코너 3~4점이 필요합니다.")];
                list.Add(new AtomicOp
                {
                    Kind = "createArea", Source = src, AreaId = Guid.NewGuid(), WallCode = op.WallCode,
                    Name = op.Name ?? op.AreaName, StandoffM = op.StandoffM,
                    Corners = op.Corners.Where(p => p is { Length: >= 2 }).Select(p => new[] { p[0], p[1] + off }).ToArray(),
                });
                break;
            }
            case "createTask":
            {
                if (ResolveOneArea(ws, op, out var why) is not { } area) return [Invalid(why!)];
                double off = VOff(ws, area.WallCode, area.Level) ?? 0;
                if (op.StartU is null || op.StartV is null || op.EndU is null || op.EndV is null)
                    return [Invalid("createTask 에는 startU·startV·endU·endV 가 모두 필요합니다.")];
                list.Add(new AtomicOp
                {
                    Kind = "createTask", Source = src, AreaId = area.AreaId, TaskId = Guid.NewGuid(),
                    Seq = op.Seq, TaskName = op.TaskName, SeamType = op.SeamType ?? "LINE",
                    StartU = op.StartU, StartV = op.StartV + off, EndU = op.EndU, EndV = op.EndV + off,
                });
                break;
            }
            case "updateTask":
            case "deleteTask":
            {
                if (ResolveOneArea(ws, op, out var why) is not { } area) return [Invalid(why!)];
                var seqs = op.Seqs is { Length: > 0 } ? op.Seqs : op.Seq is int s ? [s] : null;
                if (seqs is null) return [Invalid($"{op.Op} 에는 seq(또는 seqs)가 필요합니다.")];
                double off = VOff(ws, area.WallCode, area.Level) ?? 0;
                foreach (var seq in seqs)
                {
                    var t = area.Tasks.FirstOrDefault(x => x.Seq == seq);
                    if (t is null) { list.Add(Invalid($"영역 '{area.Name}'에 seq {seq} 작업이 없습니다.")); continue; }
                    list.Add(op.Op == "deleteTask"
                        ? new AtomicOp { Kind = "deleteTask", Source = src, AreaId = area.AreaId, TaskId = t.TaskId }
                        : new AtomicOp
                        {
                            Kind = "updateTask", Source = src, AreaId = area.AreaId, TaskId = t.TaskId,
                            TaskName = op.TaskName, SeamType = op.SeamType,
                            StartU = op.StartU, StartV = op.StartV + off, EndU = op.EndU, EndV = op.EndV + off,
                        });
                }
                break;
            }
            case "copyArea":
            {
                // 원본 좌표는 서버가 안다 — LLM 은 "어느 영역을, 어느 쪽으로, 무슨 이름으로"만 정한다.
                if (ResolveOneArea(ws, op, out var why) is not { } srcArea) return [Invalid(why!)];
                if (string.IsNullOrWhiteSpace(op.Name)) return [Invalid("copyArea 에는 새 영역 이름(name)이 필요합니다.")];
                double w = srcArea.UMax - srcArea.UMin, h = srcArea.VMax - srcArea.VMin, gap = op.Gap ?? 0;
                double du, dv;
                if (op.Du is not null || op.Dv is not null) { du = op.Du ?? 0; dv = op.Dv ?? 0; }
                else
                {
                    (du, dv) = (op.Placement ?? "").ToLowerInvariant() switch
                    {
                        "left" => (-(w + gap), 0.0),
                        "right" => (w + gap, 0.0),
                        "above" => (0.0, h + gap),
                        "below" => (0.0, -(h + gap)),
                        _ => (double.NaN, double.NaN),
                    };
                    if (double.IsNaN(du)) return [Invalid("copyArea 에는 placement(left·right·above·below) 또는 du/dv 가 필요합니다.")];
                }
                var newId = Guid.NewGuid();
                var srcCorners = JsonSerializer.Deserialize<double[][]>(srcArea.Corners)!;
                list.Add(new AtomicOp
                {
                    Kind = "createArea", Source = src, AreaId = newId, WallCode = srcArea.WallCode, Name = op.Name.Trim(),
                    StandoffM = op.StandoffM ?? srcArea.StationStandoffM,
                    Corners = srcCorners.Select(p => new[] { p[0] + du, p[1] + dv }).ToArray(),
                });
                if (op.CopyTasks ?? true)
                    foreach (var t in srcArea.Tasks.OrderBy(t => t.Seq))
                        list.Add(new AtomicOp
                        {
                            Kind = "createTask", Source = src, AreaId = newId, TaskId = Guid.NewGuid(),
                            Seq = t.Seq, TaskName = t.Name, SeamType = t.SeamType,
                            StartU = t.StartU + du, StartV = t.StartV + dv, EndU = t.EndU + du, EndV = t.EndV + dv,
                            SectionDxfId = t.SectionDxfId, ProfileId = t.ProfileId,
                        });
                break;
            }
            case "gridAreas":
                return ExpandGrid(ws, op, src, messages);
            case "renameAreas":
            case "moveAreas":
            case "deleteAreas":
            {
                if (!HasFilter(op)) return [Invalid($"{op.Op} 에는 대상 필터(wallCode·level·areaName·namePattern 중 하나 이상)가 필요합니다 — 전체 대상이면 namePattern \"*\".")];
                var areas = SelectAreas(ws, op);
                if (areas.Count == 0) { messages.Add($"#{src + 1} {op.Op}: 조건에 맞는 영역이 없습니다."); break; }
                // 새 이름 지정(name) — "A 를 B 로 이름 변경"의 자연스러운 표현. 같은 이름 여러 개가 생기지 않게 대상 1개만 허용.
                if (op.Op == "renameAreas" && !string.IsNullOrWhiteSpace(op.Name) && areas.Count > 1)
                    return [Invalid($"새 이름(name) 지정은 영역 1개에만 쓸 수 있습니다 — 대상 {areas.Count}개. areaName(+wallCode)로 하나만 고르거나 find/replace 를 쓰세요.")];
                foreach (var a in areas)
                {
                    if (op.Op == "deleteAreas")
                        list.Add(new AtomicOp { Kind = "deleteArea", Source = src, AreaId = a.AreaId });
                    else if (op.Op == "moveAreas")
                    {
                        if (MoveOffset(op) is not var (du, dv)) return [Invalid($"방향 '{op.Placement}' — left·right·above·below 중 하나여야 합니다.")];
                        if (du == 0 && dv == 0) return [Invalid("moveAreas 에는 placement+distance(또는 du/dv)가 필요합니다.")];
                        var corners = JsonSerializer.Deserialize<double[][]>(a.Corners)!.Select(p => new[] { p[0] + du, p[1] + dv }).ToArray();
                        list.Add(new AtomicOp { Kind = "updateArea", Source = src, AreaId = a.AreaId, Corners = corners, MoveTasksDu = du, MoveTasksDv = dv });
                    }
                    else
                    {
                        var n = a.Name;
                        if (!string.IsNullOrWhiteSpace(op.Name)) n = op.Name.Trim();   // name 우선
                        else
                        {
                            if (!string.IsNullOrEmpty(op.Find)) n = n.Replace(op.Find, op.Replace ?? "");
                            n = (op.Prefix ?? "") + n + (op.Suffix ?? "");
                        }
                        if (n != a.Name) list.Add(new AtomicOp { Kind = "updateArea", Source = src, AreaId = a.AreaId, Name = n });
                    }
                }
                // 대상은 찾았는데 바뀌는 이름이 없으면 조용히 끝내지 않고 이유를 알린다(LLM이 지원하지 않는 필드로 답한 경우 등).
                if (op.Op == "renameAreas" && list.Count == 0)
                    messages.Add($"#{src + 1} renameAreas: 대상 영역 {areas.Count}개를 찾았지만 바뀌는 이름이 없습니다 (name=새 이름, find/replace, prefix/suffix 중 하나를 지정).");
                break;
            }
            case "setSeamType":
            case "shiftTasks":
            case "deleteTasks":
            {
                if (!HasFilter(op)) return [Invalid($"{op.Op} 에는 대상 필터(wallCode·level·areaName·namePattern 중 하나 이상)가 필요합니다 — 전체 대상이면 namePattern \"*\".")];
                if (op.Op == "setSeamType" && op.SeamType is null) return [Invalid("setSeamType 에는 seamType 이 필요합니다.")];
                var (sdu, sdv) = op.Op == "shiftTasks" ? MoveOffset(op) ?? (double.NaN, double.NaN) : (0, 0);
                if (double.IsNaN(sdu)) return [Invalid($"방향 '{op.Placement}' — left·right·above·below 중 하나여야 합니다.")];
                if (op.Op == "shiftTasks" && sdu == 0 && sdv == 0) return [Invalid("shiftTasks 에는 placement+distance(또는 du/dv)가 필요합니다.")];
                foreach (var a in SelectAreas(ws, op))
                    foreach (var t in a.Tasks.OrderBy(t => t.Seq))
                    {
                        if (op.Seqs is { Length: > 0 } && !op.Seqs.Contains(t.Seq)) continue;
                        if (op.MatchSeamType is not null && !string.Equals(t.SeamType, op.MatchSeamType, StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(op.Op switch
                        {
                            "deleteTasks" => new AtomicOp { Kind = "deleteTask", Source = src, AreaId = a.AreaId, TaskId = t.TaskId },
                            "setSeamType" => new AtomicOp { Kind = "updateTask", Source = src, AreaId = a.AreaId, TaskId = t.TaskId, SeamType = op.SeamType },
                            _ => new AtomicOp
                            {
                                Kind = "updateTask", Source = src, AreaId = a.AreaId, TaskId = t.TaskId,
                                StartU = t.StartU + sdu, StartV = t.StartV + sdv,
                                EndU = t.EndU + sdu, EndV = t.EndV + sdv,
                            },
                        });
                    }
                if (list.Count == 0) messages.Add($"#{src + 1} {op.Op}: 조건에 맞는 작업이 없습니다.");
                break;
            }
            case "setScenarioAreas":
            {
                if (string.IsNullOrWhiteSpace(op.ScenarioName)) return [Invalid("setScenarioAreas 에는 scenarioName 이 필요합니다.")];
                var mode = (op.Mode ?? "add").ToLowerInvariant();
                if (mode is not ("add" or "remove" or "replace")) return [Invalid($"mode '{op.Mode}' — add·remove·replace 중 하나여야 합니다.")];
                if (mode != "replace" && !HasFilter(op)) return [Invalid("add/remove 에는 대상 필터가 필요합니다.")];
                var sc = ws.Scenarios.FirstOrDefault(s => s.Name == op.ScenarioName);
                Guid sid;
                if (sc is null)
                {
                    if (op.CreateIfMissing != true) return [Invalid($"시나리오 '{op.ScenarioName}'이(가) 없습니다 (새로 만들려면 createIfMissing=true).")];
                    sid = Guid.NewGuid();
                    list.Add(new AtomicOp { Kind = "createScenario", Source = src, ScenarioId = sid, Name = op.ScenarioName });
                }
                else sid = sc.ScenarioId;
                var current = ws.ScenarioAreas.Where(x => x.ScenarioId == sid).OrderBy(x => x.SortOrder).Select(x => x.AreaId).ToList();
                var picked = HasFilter(op) ? SelectAreas(ws, op).Select(a => a.AreaId).ToList() : new List<Guid>();
                var final = mode switch
                {
                    "add" => current.Concat(picked.Except(current)).ToList(),
                    "remove" => current.Except(picked).ToList(),
                    _ => picked,
                };
                list.Add(new AtomicOp { Kind = "setScenarioAreas", Source = src, ScenarioId = sid, Name = op.ScenarioName, AreaIds = final.ToArray() });
                break;
            }
            case "query":
                messages.Add(Query(ws, op));
                break;
            default:
                return [Invalid($"알 수 없는 연산 '{op.Op}'.")];
        }
        return list;
    }

    private List<AtomicOp> ExpandGrid(Ws ws, PlanOp op, int src, List<string> messages)
    {
        AtomicOp Invalid(string why) => new() { Kind = "invalid:" + why, Source = src };
        if (op.WallCode is null || !ws.Walls.TryGetValue(op.WallCode, out var wall)) return [Invalid($"면 '{op.WallCode}'이(가) 없습니다.")];
        if (op.Level is not int lv) return [Invalid("gridAreas 에는 level 이 필요합니다.")];
        if (Band(ws, wall, lv) is not var (vLo, vHi)) return [Invalid($"면 {op.WallCode}는 L{lv}에서 도달할 수 없습니다.")];
        double cu = op.CellU ?? 1.4, cv = op.CellV ?? cu, gap = op.Gap ?? 0;
        if (cu <= MinCell || cv <= MinCell || !AreaGeometry.WithinMaxSize(0, 0, cu, cv))
            return [Invalid($"격자 칸 크기 {cu:0.###}×{cv:0.###} m — 0.05 초과, 1.44 m 이하여야 합니다.")];
        if (gap < 0) return [Invalid("gap 은 0 이상이어야 합니다.")];

        double u0 = Math.Max(0, op.UFrom ?? 0), u1 = Math.Min(wall.ULen, op.UTo ?? wall.ULen);
        double v0 = Math.Max(vLo, vLo + (op.VFrom ?? 0)), v1 = Math.Min(vHi, vLo + (op.VTo ?? (vHi - vLo)));
        if (u1 - u0 < MinCell || v1 - v0 < MinCell) return [Invalid("격자 범위가 비었습니다.")];

        var prefix = op.NamePrefix ?? $"{op.WallCode}-L{lv}-";
        var usedNames = ws.Areas.Where(a => a.WallCode == op.WallCode).Select(a => a.Name).ToHashSet();
        var existing = ws.Areas.Where(a => a.WallCode == op.WallCode).Select(a => (a.UMin, a.VMin, a.UMax, a.VMax)).ToList();
        bool bulkhead = TankGeometry.IsBulkhead(op.WallCode);
        double b2 = ws.Geom.Derived().B / 2;
        int n = 0, made = 0, overlap = 0, outside = 0;
        var list = new List<AtomicOp>();
        for (double v = v0; v1 - v >= MinCell - 1e-9; v += cv + gap)
        {
            double vt = Math.Min(v + cv, v1);
            for (double u = u0; u1 - u >= MinCell - 1e-9; u += cu + gap)
            {
                double ut = Math.Min(u + cu, u1);
                if (existing.Any(e => Math.Min(ut, e.UMax) - Math.Max(u, e.UMin) > 1e-6 && Math.Min(vt, e.VMax) - Math.Max(v, e.VMin) > 1e-6))
                { overlap++; continue; }
                if (bulkhead && new[] { (u, v), (ut, v), (ut, vt), (u, vt) }.Any(p => Math.Abs(p.Item1 - b2) > ws.Geom.BulkheadHalfWidth(p.Item2) + 1e-6))
                { outside++; continue; }
                string name;
                do name = $"{prefix}{++n:000}"; while (usedNames.Contains(name));
                list.Add(new AtomicOp
                {
                    Kind = "createArea", Source = src, AreaId = Guid.NewGuid(), WallCode = op.WallCode, Name = name, StandoffM = op.StandoffM,
                    Corners = [[u, v], [ut, v], [ut, vt], [u, vt]],
                });
                made++;
            }
        }
        messages.Add($"#{src + 1} gridAreas {op.WallCode} L{lv}: 영역 {made}개" +
            (overlap > 0 ? $" · 기존 영역과 겹쳐 {overlap}칸 제외" : "") +
            (outside > 0 ? $" · 격벽 윤곽 밖 {outside}칸 제외" : ""));
        return list;
    }

    /// <summary>
    /// 이동량(du,dv). placement 가 있으면 **부호는 방향이 정한다**(LLM이 "아래로 0.3"을 dv=+0.3으로 내는 부호 실수 방지),
    /// 크기는 distance → |du| → |dv| 순. placement 가 없으면 du/dv 그대로. 알 수 없는 방향이면 null.
    /// 방향은 전개도 화면 기준: 위=v 증가, 아래=v 감소, 오른쪽=u 증가, 왼쪽=u 감소.
    /// </summary>
    internal static (double Du, double Dv)? MoveOffset(PlanOp op)
    {
        if (string.IsNullOrWhiteSpace(op.Placement)) return (op.Du ?? 0, op.Dv ?? 0);
        double mag = op.Distance ?? (op.Du is double d && d != 0 ? Math.Abs(d) : Math.Abs(op.Dv ?? 0));
        return op.Placement.ToLowerInvariant() switch
        {
            "left" => (-mag, 0),
            "right" => (mag, 0),
            "above" => (0, mag),
            "below" => (0, -mag),
            _ => null,
        };
    }

    private static bool HasFilter(PlanOp op) =>
        op.WallCode is not null || op.Level is not null || op.AreaName is not null || op.NamePattern is not null;

    private static List<InspectionAreaEntity> SelectAreas(Ws ws, PlanOp op)
    {
        Regex? rx = op.NamePattern is null ? null
            : new Regex("^" + Regex.Escape(op.NamePattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        return ws.Areas.Where(a =>
                (op.WallCode is null || a.WallCode == op.WallCode) &&
                (op.Level is null || a.Level == op.Level) &&
                (op.AreaName is null || a.Name == op.AreaName) &&
                (rx is null || rx.IsMatch(a.Name)))
            .OrderBy(a => a.WallCode).ThenBy(a => a.Level).ThenBy(a => a.Name).ToList();
    }

    private static InspectionAreaEntity? ResolveOneArea(Ws ws, PlanOp op, out string? why)
    {
        why = null;
        if (op.AreaName is null) { why = $"{op.Op} 에는 areaName 이 필요합니다."; return null; }
        var hits = ws.Areas.Where(a => a.Name == op.AreaName && (op.WallCode is null || a.WallCode == op.WallCode)).ToList();
        if (hits.Count == 1) return hits[0];
        why = hits.Count == 0
            ? $"영역 '{op.AreaName}'{(op.WallCode is null ? "" : $"(면 {op.WallCode})")}이(가) 없습니다."
            : $"영역 '{op.AreaName}'이(가) 여러 면에 있습니다({string.Join(",", hits.Select(h => h.WallCode))}) — wallCode 를 지정하세요.";
        return null;
    }

    private static (double VLo, double VHi)? Band(Ws ws, WallEntity wall, int level)
    {
        var band = ws.Geom.LevelBandList().FirstOrDefault(b => b.Level == level);
        if (band is null) return null;
        var origin = JsonSerializer.Deserialize<double[]>(wall.Origin)!;
        var vAxis = JsonSerializer.Deserialize<double[]>(wall.VAxis)!;
        return LevelBands.ReachableVBand(origin[2], vAxis[2], wall.VLen, band);
    }

    /// <summary>층-로컬 v 원점(= 그 층 도달 구간 아래 끝, 면-전체 v). 계획 화면 VOff 와 같은 값.</summary>
    private static double? VOff(Ws ws, string wallCode, int level) =>
        ws.Walls.TryGetValue(wallCode, out var w) ? Band(ws, w, level)?.VLo : null;

    private static string Query(Ws ws, PlanOp op)
    {
        var areas = SelectAreas(ws, op);
        var tasks = areas.SelectMany(a => a.Tasks.Where(t =>
            (op.MatchSeamType is null || string.Equals(t.SeamType, op.MatchSeamType, StringComparison.OrdinalIgnoreCase)) &&
            (op.Seqs is not { Length: > 0 } || op.Seqs.Contains(t.Seq)))).ToList();
        var sb = new StringBuilder($"조회 결과: 영역 {areas.Count}개 · 작업 {tasks.Count}개");
        if (tasks.Count > 0)
            sb.Append(" (" + string.Join(", ", tasks.GroupBy(t => t.SeamType).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")) + ")");
        var byWall = areas.GroupBy(a => (a.WallCode, a.Level)).OrderBy(g => g.Key.WallCode).ThenBy(g => g.Key.Level).ToList();
        if (byWall.Count > 1)
            sb.Append("\n면·층별: " + string.Join(" / ", byWall.Select(g => $"{g.Key.WallCode} L{g.Key.Level} 영역 {g.Count()}·작업 {g.Sum(a => a.Tasks.Count)}")));
        if ((op.Aggregate ?? "count").Equals("list", StringComparison.OrdinalIgnoreCase) && areas.Count > 0)
            sb.Append("\n목록: " + string.Join(", ", areas.Take(30).Select(a => $"{a.Name}({a.WallCode} L{a.Level}, 작업 {a.Tasks.Count})"))
                      + (areas.Count > 30 ? $" … 외 {areas.Count - 30}개" : ""));
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════
    // 원자 연산 실행 — 통과 시에만 작업 공간(추적 엔티티)을 바꾼다
    // ══════════════════════════════════════════════════════════════

    private OpResult Execute(Ws ws, AtomicOp a)
    {
        if (a.Kind.StartsWith("invalid:"))
            return new OpResult(a with { Kind = "invalid" }, false, a.Kind["invalid:".Length..], null, null, null, null, null, "전개 실패");
        try
        {
            return a.Kind switch
            {
                "createArea" => CreateArea(ws, a),
                "updateArea" => UpdateArea(ws, a),
                "deleteArea" => DeleteArea(ws, a),
                "createTask" => CreateTask(ws, a),
                "updateTask" => UpdateTask(ws, a),
                "deleteTask" => DeleteTask(ws, a),
                "createScenario" => CreateScenario(ws, a),
                "setScenarioAreas" => SetScenarioAreas(ws, a),
                _ => Fail(a, $"알 수 없는 원자 연산 '{a.Kind}'."),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return Fail(a, ex.Message); }
    }

    private static OpResult Fail(AtomicOp a, string error, InspectionAreaEntity? area = null, string? summary = null) =>
        new(a, false, error, area?.WallCode ?? a.WallCode, area?.Level, area?.Name ?? a.Name, a.Corners, null, summary ?? a.Kind);

    private static OpResult Ok(AtomicOp a, InspectionAreaEntity area, string summary, double[][]? corners = null, double[]? seg = null) =>
        new(a, true, null, area.WallCode, area.Level, area.Name, corners, seg, summary);

    private OpResult CreateArea(Ws ws, AtomicOp a)
    {
        if (string.IsNullOrWhiteSpace(a.Name)) return Fail(a, "영역 이름이 비었습니다.");
        if (!ws.Walls.TryGetValue(a.WallCode ?? "", out var wall)) return Fail(a, $"면 '{a.WallCode}'이(가) 없습니다.");
        var corners = (a.Corners ?? []).ToList();
        if (AreaRules.ValidateCorners(corners, wall.WallCode, wall.ULen, wall.VLen, out var bb) is { } v) return Fail(a, v);
        if (AreaRules.DeriveLevel(ws.G, wall, bb.MinV, bb.MaxV, out var reason) is not int level)
            return Fail(a, $"층 유도 실패 (면 {wall.WallCode}): {reason}");
        if (a.StandoffM is < 0) return Fail(a, "정차 이격은 0 이상이어야 합니다.");
        if (ws.Areas.Any(x => x.WallCode == wall.WallCode && x.Name == a.Name))
            return Fail(a, $"면 {wall.WallCode} 내에 영역 '{a.Name}'이(가) 이미 있습니다.");
        if (ws.Areas.Any(x => x.AreaId == a.AreaId)) return Fail(a, $"areaId '{a.AreaId}' 가 이미 있습니다.");
        var e = new InspectionAreaEntity
        {
            AreaId = a.AreaId ?? Guid.NewGuid(), TankId = ws.TankId, WallCode = wall.WallCode, Level = level, Name = a.Name!,
            Corners = JsonSerializer.Serialize(corners), UMin = bb.MinU, VMin = bb.MinV, UMax = bb.MaxU, VMax = bb.MaxV,
            StationStandoffM = a.StandoffM, CreatedBy = "planning-assistant",
        };
        _db.InspectionAreas.Add(e);
        ws.Areas.Add(e);
        return Ok(a, e, $"영역 생성 {e.Name} ({e.WallCode} L{e.Level})", corners.ToArray());
    }

    private OpResult UpdateArea(Ws ws, AtomicOp a)
    {
        var area = ws.Areas.FirstOrDefault(x => x.AreaId == a.AreaId);
        if (area is null) return Fail(a, $"영역 '{a.AreaId}'이(가) 없습니다.");
        var wall = ws.Walls[area.WallCode];
        var name = a.Name ?? area.Name;
        if (string.IsNullOrWhiteSpace(name)) return Fail(a, "영역 이름이 비었습니다.", area);
        var corners = (a.Corners ?? JsonSerializer.Deserialize<double[][]>(area.Corners)!).ToList();
        if (AreaRules.ValidateCorners(corners, wall.WallCode, wall.ULen, wall.VLen, out var bb) is { } v) return Fail(a, v, area);
        if (AreaRules.DeriveLevel(ws.G, wall, bb.MinV, bb.MaxV, out var reason) is not int level)
            return Fail(a, $"층 유도 실패 (면 {wall.WallCode}): {reason}", area);
        double du = a.MoveTasksDu ?? 0, dv = a.MoveTasksDv ?? 0;
        var moved = area.Tasks.Select(t => new AreaTaskEntity
            { Seq = t.Seq, StartU = t.StartU + du, StartV = t.StartV + dv, EndU = t.EndU + du, EndV = t.EndV + dv }).ToList();
        var outside = AreaRules.TasksOutside(corners, moved);
        if (outside.Count > 0)
            return Fail(a, $"작업이 새 영역 밖으로 나갑니다 (seq {string.Join(", ", outside)}).", area);
        if (ws.Areas.Any(x => x.AreaId != area.AreaId && x.WallCode == area.WallCode && x.Name == name))
            return Fail(a, $"면 {area.WallCode} 내에 영역 '{name}'이(가) 이미 있습니다.", area);

        var oldName = area.Name;
        area.Name = name;
        area.Corners = JsonSerializer.Serialize(corners);
        (area.UMin, area.VMin, area.UMax, area.VMax) = bb;
        area.Level = level;
        foreach (var t in area.Tasks) { t.StartU += du; t.StartV += dv; t.EndU += du; t.EndV += dv; }
        var what = a.Name is not null && oldName != name ? $"이름 {oldName} → {name}" : $"이동 Δu {du:+0.###;-0.###;0} Δv {dv:+0.###;-0.###;0}";
        return Ok(a, area, $"영역 수정 {oldName}: {what}", corners.ToArray());
    }

    private OpResult DeleteArea(Ws ws, AtomicOp a)
    {
        var area = ws.Areas.FirstOrDefault(x => x.AreaId == a.AreaId);
        if (area is null) return Fail(a, $"영역 '{a.AreaId}'이(가) 없습니다.");
        var corners = JsonSerializer.Deserialize<double[][]>(area.Corners);
        int nTasks = area.Tasks.Count;
        _db.InspectionAreas.Remove(area);   // 작업 CASCADE
        ws.Areas.Remove(area);
        foreach (var sa in ws.ScenarioAreas.Where(x => x.AreaId == area.AreaId).ToList())
        { _db.ScenarioAreas.Remove(sa); ws.ScenarioAreas.Remove(sa); }
        return Ok(a, area, $"영역 삭제 {area.Name} ({area.WallCode} L{area.Level}, 작업 {nTasks}개 포함)", corners);
    }

    private OpResult CreateTask(Ws ws, AtomicOp a)
    {
        var area = ws.Areas.FirstOrDefault(x => x.AreaId == a.AreaId);
        if (area is null) return Fail(a, $"영역 '{a.AreaId}'이(가) 없습니다.");
        if (AreaTaskRules.Validate(a.SeamType, area.Corners, a.StartU!.Value, a.StartV!.Value, a.EndU!.Value, a.EndV!.Value) is { } v)
            return Fail(a, v, area);
        if (a.Seq is < 1) return Fail(a, "seq 는 1 이상이어야 합니다.", area);
        if (a.Seq is int s && area.Tasks.Any(t => t.Seq == s)) return Fail(a, $"영역 '{area.Name}'에 seq {s} 가 이미 있습니다.", area);
        int seq = a.Seq ?? (area.Tasks.Select(t => t.Seq).DefaultIfEmpty(0).Max() + 1);
        var t = new AreaTaskEntity
        {
            TaskId = a.TaskId ?? Guid.NewGuid(), AreaId = area.AreaId, Seq = seq, Name = a.TaskName,
            SeamType = AreaTaskRules.Normalize(a.SeamType),   // canonical 저장(legacy 별칭 매핑 — §8.5.1)
            StartU = a.StartU.Value, StartV = a.StartV.Value, EndU = a.EndU.Value, EndV = a.EndV.Value,
            SectionDxfId = string.IsNullOrWhiteSpace(a.SectionDxfId) ? DefaultSectionDxfId : a.SectionDxfId,
            ProfileId = string.IsNullOrWhiteSpace(a.ProfileId) ? DefaultProfileId : a.ProfileId, CreatedBy = "planning-assistant",
        };
        _db.AreaTasks.Add(t);
        area.Tasks.Add(t);
        return Ok(a, area, $"작업 추가 {area.Name} #{seq} {t.SeamType}", seg: [t.StartU, t.StartV, t.EndU, t.EndV]);
    }

    private OpResult UpdateTask(Ws ws, AtomicOp a)
    {
        var area = ws.Areas.FirstOrDefault(x => x.AreaId == a.AreaId);
        var t = area?.Tasks.FirstOrDefault(x => x.TaskId == a.TaskId);
        if (area is null || t is null) return Fail(a, $"작업 '{a.TaskId}'이(가) 없습니다.", area);
        double su = a.StartU ?? t.StartU, sv = a.StartV ?? t.StartV, eu = a.EndU ?? t.EndU, ev = a.EndV ?? t.EndV;
        if (AreaTaskRules.Validate(a.SeamType, area.Corners, su, sv, eu, ev) is { } v) return Fail(a, v, area);
        var changes = new List<string>();
        if (a.SeamType is not null && AreaTaskRules.Normalize(a.SeamType) is var norm && !norm.Equals(t.SeamType, StringComparison.Ordinal))
        { changes.Add($"seamType {t.SeamType} → {norm}"); t.SeamType = norm; }
        if (su != t.StartU || sv != t.StartV || eu != t.EndU || ev != t.EndV)
        { changes.Add("좌표"); (t.StartU, t.StartV, t.EndU, t.EndV) = (su, sv, eu, ev); }
        if (a.TaskName is not null && a.TaskName != t.Name) { changes.Add("이름"); t.Name = a.TaskName.Length == 0 ? null : a.TaskName; }
        return Ok(a, area, $"작업 수정 {area.Name} #{t.Seq}: {(changes.Count == 0 ? "변경 없음" : string.Join(", ", changes))}",
            seg: [t.StartU, t.StartV, t.EndU, t.EndV]);
    }

    private OpResult DeleteTask(Ws ws, AtomicOp a)
    {
        var area = ws.Areas.FirstOrDefault(x => x.AreaId == a.AreaId);
        var t = area?.Tasks.FirstOrDefault(x => x.TaskId == a.TaskId);
        if (area is null || t is null) return Fail(a, $"작업 '{a.TaskId}'이(가) 없습니다.", area);
        _db.AreaTasks.Remove(t);
        area.Tasks.Remove(t);
        return Ok(a, area, $"작업 삭제 {area.Name} #{t.Seq}", seg: [t.StartU, t.StartV, t.EndU, t.EndV]);
    }

    private OpResult CreateScenario(Ws ws, AtomicOp a)
    {
        if (string.IsNullOrWhiteSpace(a.Name)) return Fail(a, "시나리오 이름이 비었습니다.");
        if (ws.Scenarios.Any(s => s.Name == a.Name)) return Fail(a, $"시나리오 '{a.Name}'이(가) 이미 있습니다.");
        var s = new ScenarioEntity { ScenarioId = a.ScenarioId ?? Guid.NewGuid(), Name = a.Name!, Version = 1, TankId = ws.TankId, Policy = "{}", Status = "DRAFT" };
        _db.Scenarios.Add(s);
        ws.Scenarios.Add(s);
        return new OpResult(a, true, null, null, null, null, null, null, $"시나리오 생성 {s.Name}");
    }

    private OpResult SetScenarioAreas(Ws ws, AtomicOp a)
    {
        if (!ws.Scenarios.Any(s => s.ScenarioId == a.ScenarioId)) return Fail(a, $"시나리오 '{a.Name}'이(가) 없습니다.");
        var ids = (a.AreaIds ?? []).Distinct().ToArray();
        var missing = ids.Where(id => ws.Areas.All(x => x.AreaId != id)).ToList();
        if (missing.Count > 0) return Fail(a, $"존재하지 않는 영역 {missing.Count}건.");
        var existing = ws.ScenarioAreas.Where(x => x.ScenarioId == a.ScenarioId).ToList();
        int before = existing.Count;
        // 차집합으로 갱신 — 같은 키 행을 삭제 후 재추가하면 EF 추적 충돌이 나므로 유지 행은 SortOrder만 고친다.
        foreach (var sa in existing.Where(x => !ids.Contains(x.AreaId)))
        { _db.ScenarioAreas.Remove(sa); ws.ScenarioAreas.Remove(sa); }
        for (int i = 0; i < ids.Length; i++)
        {
            if (existing.FirstOrDefault(x => x.AreaId == ids[i]) is { } kept) { kept.SortOrder = i; continue; }
            var sa = new ScenarioAreaEntity { ScenarioId = a.ScenarioId!.Value, AreaId = ids[i], SortOrder = i };
            _db.ScenarioAreas.Add(sa);
            ws.ScenarioAreas.Add(sa);
        }
        return new OpResult(a, true, null, null, null, null, null, null,
            $"시나리오 '{a.Name}' 검사 대상 {before}개 → {ids.Length}개{(ids.Length == 0 ? " (선창 전체)" : "")}");
    }
}
