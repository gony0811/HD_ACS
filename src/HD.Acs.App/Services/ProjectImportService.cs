using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>프로젝트(.hdacs) 일괄 가져오기 요청의 영역 1개 — 단건 <c>CreateAreaRequest</c>와 같은 필드 + 소속 작업.</summary>
public sealed record ProjectImportArea(string WallCode, string Name, double[][]? Corners,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta, double? StationStandoffM,
    int? SortOrder, Guid? AreaId, CreateAreaTaskRequest[]? Tasks);

/// <summary>프로젝트 일괄 가져오기 요청 — 지오메트리(각도 deg) + 영역·작업 전체.</summary>
public sealed record ProjectImportRequest(CreateTankGeometryRequest Geometry, ProjectImportArea[] Areas, string? UserId);

/// <summary>가져오기 결과. AreaIds = 요청 순서(구파일처럼 areaId 미지정이면 새로 발급된 값).</summary>
public sealed record ProjectImportResult(string TankId, int Areas, int Tasks, Guid[] AreaIds);

/// <summary>
/// 프로젝트 파일 일괄 가져오기 — 선창 재등록 + 영역·작업 전부를 **요청 1건·커밋 1회**로 적재한다.
/// 종전 열기는 영역·작업마다 POST(대형 선창 약 8,800건, 건마다 조회 4~5회+커밋)라 수 분이 걸렸고,
/// 중간 실패 시 이미 지워진 DB가 반쯤 적재된 채 남았다.
/// 여기서는 **DB를 건드리기 전에 메모리에서 전부 검증**(단건 엔드포인트와 같은 정본 규칙 AreaRules/AreaTaskRules)하고,
/// 위반이 1건이라도 있으면 아무것도 바꾸지 않는다(전부 또는 전무).
/// </summary>
public sealed class ProjectImportService
{
    /// <summary>거부 사유 수집 상한 — 파일이 통째로 틀렸을 때 응답이 수천 줄이 되지 않게.</summary>
    public const int MaxReasons = 50;

    private readonly AcsDbContext _db;
    private readonly TankGeometryService _geometry;
    private readonly ILogger<ProjectImportService> _log;

    public ProjectImportService(AcsDbContext db, TankGeometryService geometry, ILogger<ProjectImportService> log)
    {
        _db = db; _geometry = geometry; _log = log;
    }

    /// <summary>검증 실패 — 400. DB는 변경되지 않았다.</summary>
    public sealed class ImportRejectedException(IReadOnlyList<string> reasons)
        : Exception("프로젝트 가져오기 거부: " + string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? $" 외 {reasons.Count - 3}건" : ""))
    {
        public IReadOnlyList<string> Reasons { get; } = reasons;
    }

    public async Task<ProjectImportResult> ImportAsync(string tankId, ProjectImportRequest req, CancellationToken ct = default)
    {
        var gr = req.Geometry;
        var geom = new TankGeometry(
            gr.LengthL, gr.WFloor, gr.ThetaLowDeg * Math.PI / 180.0, gr.HLow,
            gr.HWall, gr.ThetaUpDeg * Math.PI / 180.0, gr.HUp,
            gr.LevelZ ?? Array.Empty<double>(), gr.OriginOx ?? 0, gr.OriginOy ?? 0,
            gr.ReachZMin, gr.ReachZMax);
        var geomReasons = geom.Validate(gr.CheckHTotal, gr.CheckBeam, gr.CheckWCeil);
        if (geomReasons.Count > 0)
            throw new ImportRejectedException(geomReasons.Select(r => "선창 파라미터: " + r).ToList());

        // ── 1) 메모리 검증 — 면은 등록 전이라 GenerateWalls로 만든 것을 그대로 쓴다(등록 결과와 동일) ──
        var gEntity = new TankGeometryEntity { TankId = tankId };
        TankGeometryService.ApplyTo(gEntity, geom, req.UserId);
        var walls = geom.GenerateWalls().ToDictionary(w => w.WallCode, w => TankGeometryService.ToWallEntity(tankId, w));

        var reasons = new List<string>();
        void Reject(string r) { if (reasons.Count < MaxReasons) reasons.Add(r); }

        var areas = new List<InspectionAreaEntity>(req.Areas.Length);
        var tasks = new List<AreaTaskEntity>();
        var areaIds = new HashSet<Guid>();
        var taskIds = new HashSet<Guid>();
        var names = new HashSet<(string, string)>();
        foreach (var a in req.Areas)
        {
            string at = $"영역 '{a.Name}' (면 {a.WallCode})";
            if (!walls.TryGetValue(a.WallCode, out var wall)) { Reject($"{at}: 면이 없습니다."); continue; }
            if (string.IsNullOrWhiteSpace(a.Name)) { Reject($"{at}: 영역 이름은 빈 값일 수 없습니다."); continue; }
            var corners = (a.Corners is { Length: >= 3 }
                    ? a.Corners
                    : new[] { new[] { a.UMin, a.VMin }, new[] { a.UMax, a.VMin }, new[] { a.UMax, a.VMax }, new[] { a.UMin, a.VMax } })
                .Where(p => p is { Length: >= 2 }).Select(p => new[] { p[0], p[1] }).ToList();
            if (AreaRules.ValidateCorners(corners, a.WallCode, wall.ULen, wall.VLen, out var bb) is { } cv) { Reject($"{at}: {cv}"); continue; }
            if (AreaRules.DeriveLevel(gEntity, wall, bb.MinV, bb.MaxV, out var why) is not int level) { Reject($"{at}: 층 유도 실패 — {why}"); continue; }
            if (a.StationStandoffM is < 0) Reject($"{at}: 정차 이격(stationStandoffM)은 0 이상이어야 합니다.");
            if (!names.Add((a.WallCode, a.Name))) Reject($"{at}: 같은 면에 같은 이름의 영역이 둘 이상입니다.");
            if (a.AreaId == Guid.Empty) Reject($"{at}: areaId 는 빈 GUID일 수 없습니다.");
            var areaId = a.AreaId ?? Guid.NewGuid();
            if (!areaIds.Add(areaId)) Reject($"{at}: areaId '{areaId}' 가 파일 안에서 중복됩니다.");

            var cornersJson = JsonSerializer.Serialize(corners);
            areas.Add(new InspectionAreaEntity
            {
                AreaId = areaId, TankId = tankId, WallCode = a.WallCode, Level = level, Name = a.Name,
                Corners = cornersJson, UMin = bb.MinU, VMin = bb.MinV, UMax = bb.MaxU, VMax = bb.MaxV,
                StationX = a.StationX, StationY = a.StationY, StationTheta = a.StationTheta,
                StationStandoffM = a.StationStandoffM, SortOrder = a.SortOrder ?? 0, CreatedBy = req.UserId,
            });

            var seqs = new HashSet<int>();
            int nextSeq = 1;
            foreach (var t in a.Tasks ?? Array.Empty<CreateAreaTaskRequest>())
            {
                int seq = t.Seq ?? nextSeq;
                nextSeq = Math.Max(nextSeq, seq + 1);
                string tt = $"{at} 작업 seq {seq}";
                if (AreaTaskRules.Validate(t.SeamType, cornersJson, t.StartU, t.StartV, t.EndU, t.EndV) is { } v) Reject($"{tt}: {v}");
                if (AreaTaskRules.ValidatePoints(t.SeamType, cornersJson, t.Points) is { } pv) Reject($"{tt}: {pv}");
                if (string.IsNullOrWhiteSpace(t.SectionDxfId) || string.IsNullOrWhiteSpace(t.ProfileId))
                    Reject($"{tt}: sectionDxfId와 profileId는 빈 값일 수 없습니다.");
                if (seq < 1 || !seqs.Add(seq)) Reject($"{tt}: seq 는 1 이상이고 영역 안에서 유일해야 합니다.");
                if (t.TaskId == Guid.Empty) Reject($"{tt}: taskId 는 빈 GUID일 수 없습니다.");
                var taskId = t.TaskId ?? Guid.NewGuid();
                if (!taskIds.Add(taskId)) Reject($"{tt}: taskId '{taskId}' 가 파일 안에서 중복됩니다.");
                tasks.Add(new AreaTaskEntity
                {
                    TaskId = taskId, AreaId = areaId, Seq = seq, Name = t.Name,
                    SeamType = AreaTaskRules.Normalize(t.SeamType),
                    StartU = t.StartU, StartV = t.StartV, EndU = t.EndU, EndV = t.EndV,
                    Points = t.Points is { Length: > 0 } ? JsonSerializer.Serialize(t.Points) : null,
                    SectionDxfId = t.SectionDxfId ?? "", ProfileId = t.ProfileId ?? "", CreatedBy = req.UserId,
                });
            }
        }

        // ── 2) 영구 식별자 충돌 — 다른 선창에 이미 쓰이는 ID만 거부(이 선창 것은 재등록으로 지워진다) ──
        if (reasons.Count == 0)
        {
            var aIds = areaIds.ToList();
            foreach (var id in await _db.InspectionAreas.AsNoTracking()
                         .Where(x => aIds.Contains(x.AreaId) && x.TankId != tankId).Select(x => x.AreaId).Take(MaxReasons).ToListAsync(ct))
                Reject($"areaId '{id}' 가 다른 선창에 이미 등록되어 있습니다 — 덮어쓰지 않습니다.");
            var tIds = taskIds.ToList();
            foreach (var id in await (from t in _db.AreaTasks.AsNoTracking()
                                      join x in _db.InspectionAreas.AsNoTracking() on t.AreaId equals x.AreaId
                                      where tIds.Contains(t.TaskId) && x.TankId != tankId
                                      select t.TaskId).Take(MaxReasons).ToListAsync(ct))
                Reject($"taskId '{id}' 가 다른 선창에 이미 등록되어 있습니다 — 영구 식별자는 재사용할 수 없습니다.");
        }
        if (reasons.Count > 0) throw new ImportRejectedException(reasons);

        // ── 3) 적재 — 트랜잭션 1개(관계형 DB). 선창 재등록이 면을 지우면 이 선창의 기존 영역·작업·시나리오 연결은 FK CASCADE로 정리된다 ──
        bool relational = _db.Database.IsRelational();
        await using var tx = relational ? await _db.Database.BeginTransactionAsync(ct) : null;
        if (!relational)
        {
            // InMemory(테스트)에는 FK CASCADE가 없다 — 관계형과 같은 결과가 되도록 명시 삭제
            _db.InspectionAreas.RemoveRange(await _db.InspectionAreas.Where(x => x.TankId == tankId).Include(x => x.Tasks).ToListAsync(ct));
            await _db.SaveChangesAsync(ct);
        }
        await _geometry.RegisterAsync(tankId, geom, gr.CheckHTotal, gr.CheckBeam, gr.CheckWCeil, req.UserId, ct);
        _db.InspectionAreas.AddRange(areas);
        _db.AreaTasks.AddRange(tasks);
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "PROJECT_IMPORT", Target = tankId,
            Detail = JsonSerializer.Serialize(new { areas = areas.Count, tasks = tasks.Count }),
        });
        await _db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        _log.LogInformation("프로젝트 일괄 가져오기: {Tank} 영역 {Areas} · 작업 {Tasks}", tankId, areas.Count, tasks.Count);
        return new ProjectImportResult(tankId, areas.Count, tasks.Count, areas.Select(x => x.AreaId).ToArray());
    }
}
