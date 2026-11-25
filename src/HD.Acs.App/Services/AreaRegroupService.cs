using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>다시 묶을 영역 하나 — 작업 묶음 + (있으면) 정차 덮어쓰기·리프트. 이름은 호출자가 정한다.</summary>
public sealed class RegroupGroup(List<AreaTaskEntity> tasks, bool unreachable = false,
    double? x = null, double? y = null, double? theta = null, double? lift = null)
{
    public List<AreaTaskEntity> Tasks { get; } = tasks;
    public bool Unreachable { get; } = unreachable;
    public double? X { get; } = x;
    public double? Y { get; } = y;
    public double? Theta { get; } = theta;
    public double? Lift { get; } = lift;
    public string? Name { get; set; }
    public string? Corners { get; set; }
    public (double MinU, double MinV, double MaxU, double MaxV) Box { get; set; }
    public Guid? Heir { get; set; }
}

/// <summary>면·층 하나의 지금 배치 — 영역(작업 포함, 추적 중)·면·지오메트리·이 면 다른 층의 영역 이름.</summary>
public sealed record RegroupScope(string Tank, int Level, string WallCode, List<InspectionAreaEntity> Areas,
    List<AreaTaskEntity> Tasks, WallEntity Wall, TankGeometryEntity Geom, HashSet<string> OtherNames);

/// <summary>
/// 면·층 하나의 영역을 주어진 작업 묶음대로 다시 구성하는 공통 절차(한 트랜잭션) —
/// 정차 묶음 최적화 적용(<see cref="SetCoverApplyService"/>)과 균등 격자 재분할(<see cref="AreaGridService"/>)이 함께 쓴다.
/// <list type="bullet">
/// <item>작업(taskId·좌표·유형·가지·임시 제외)은 그대로 옮긴다. 순번은 라인 순, 도구 형식 이름(H01·V03)만 새 순번으로.</item>
/// <item>영역 ID 물려주기: 새 영역은 그 작업이 가장 많이 속해 있던 기존 영역의 ID 를 받는다(기존 영역 하나당 한 번). 물려주지 못한 기존 영역은 지운다.</item>
/// <item>시나리오: 기존 영역이 연결돼 있던 시나리오에는 그 작업을 받은 새 영역을 연결한다.</item>
/// <item>감사로그에 적용 전 영역·작업 배치를 통째로 남긴다(되돌리기 자료).</item>
/// </list>
/// </summary>
public sealed class AreaRegroupService(AcsDbContext db)
{
    public async Task<RegroupScope> LoadAsync(string tank, int level, string wallCode, CancellationToken ct = default)
    {
        var areas = await db.InspectionAreas.Include(a => a.Tasks)
            .Where(a => a.TankId == tank && a.Level == level && a.WallCode == wallCode).ToListAsync(ct);
        var wall = await db.Walls.AsNoTracking().FirstOrDefaultAsync(w => w.TankId == tank && w.WallCode == wallCode, ct)
                   ?? throw new AreaSplitException(404, $"면이 없습니다: {tank}/{wallCode}.");
        var geom = await db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tank, ct)
                   ?? throw new AreaSplitException(400, $"선창 지오메트리가 없습니다: {tank}.");
        var others = await db.InspectionAreas.AsNoTracking()
            .Where(a => a.TankId == tank && a.WallCode == wallCode && a.Level != level).Select(a => a.Name).ToListAsync(ct);
        return new RegroupScope(tank, level, wallCode, areas, areas.SelectMany(a => a.Tasks).ToList(), wall, geom, others.ToHashSet());
    }

    /// <summary>이 면·층 영역이 진행 중 run 의 남은 정차에 있는가.</summary>
    public async Task<bool> IsBusyAsync(RegroupScope s, CancellationToken ct = default)
    {
        var oldIds = s.Areas.Select(a => a.AreaId).ToList();
        return await db.WorkItems.AsNoTracking()
            .Where(w => oldIds.Contains(w.AreaId) && (w.Status == "PENDING" || w.Status == "DISPATCHED"))
            .Join(db.ScenarioRuns.AsNoTracking().Where(r => r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"),
                w => w.RunId, r => r.RunId, (w, r) => r.RunId)
            .AnyAsync(ct);
    }

    /// <summary>묶음마다 자동 사각형을 만들고 크기·층·작업 포함을 검증한다(DB 무변경) — 위반이면 400.</summary>
    public static void Shape(RegroupScope s, IEnumerable<RegroupGroup> groups)
    {
        foreach (var g in groups)
        {
            var corners = AreaRules.AutoCornersForTasks(g.Tasks, s.Wall, s.Geom, s.Level).ToList();
            if (AreaRules.ValidateCorners(corners, s.WallCode, s.Wall.ULen, s.Wall.VLen, out var bb) is { } cv)
                throw new AreaSplitException(400, $"{g.Name} 사각형: {cv}");
            var lv = AreaRules.DeriveLevel(s.Geom, s.Wall, bb.MinV, bb.MaxV, out var reason)
                     ?? throw new AreaSplitException(400, $"{g.Name} 층 유도 실패: {reason}");
            if (lv != s.Level) throw new AreaSplitException(400, $"{g.Name} 사각형이 다른 층(L{lv})으로 유도됩니다.");
            g.Corners = JsonSerializer.Serialize(corners);
            g.Box = (bb.MinU, bb.MinV, bb.MaxU, bb.MaxV);
            foreach (var t in g.Tasks)
                if (AreaTaskRules.Validate(t.SeamType, g.Corners, t.StartU, t.StartV, t.EndU, t.EndV) is { } tv)
                    throw new AreaSplitException(400, $"{g.Name}: {tv}");
        }
    }

    /// <summary>
    /// 묶음대로 영역을 다시 구성한다. 묶음은 이 면·층의 모든 작업을 정확히 한 번씩 담고 이름이 정해져 있어야 한다.
    /// 진행 중 run 에 있으면 409.
    /// </summary>
    public async Task<SetCoverApplyResult> ApplyAsync(RegroupScope s, List<RegroupGroup> groups, string? userId, string auditAction,
        object? auditExtra = null, CancellationToken ct = default)
    {
        var (tank, level, wallCode, areas, tasks) = (s.Tank, s.Level, s.WallCode, s.Areas, s.Tasks);
        var grouped = groups.SelectMany(g => g.Tasks).Select(t => t.TaskId).ToList();
        if (grouped.Count != tasks.Count || !grouped.ToHashSet().SetEquals(tasks.Select(t => t.TaskId)))
            throw new AreaSplitException(400, "묶음이 이 면·층의 작업을 정확히 한 번씩 담지 않습니다.");
        if (await IsBusyAsync(s, ct))
            throw new AreaSplitException(409, "이 면·층 영역이 진행 중인 run 에 남아 있습니다 — run 이 끝난 뒤 적용하세요.");
        var oldIds = areas.Select(a => a.AreaId).ToList();

        // 사각형·층 검증 — DB 를 건드리기 전에 전부
        Shape(s, groups);

        // 영역 ID 물려주기 — (기존 영역, 묶음) 공통 작업 수가 큰 쌍부터
        var oldOf = tasks.ToDictionary(t => t.TaskId, t => t.AreaId);
        var pairs = groups.SelectMany((g, gi) => g.Tasks.GroupBy(t => oldOf[t.TaskId]).Select(o => (Old: o.Key, Gi: gi, N: o.Count())))
            .OrderByDescending(p => p.N).ThenBy(p => p.Gi).ToList();
        var usedOld = new HashSet<Guid>();
        foreach (var p in pairs)
            if (groups[p.Gi].Heir is null && !usedOld.Contains(p.Old))
            {
                groups[p.Gi].Heir = p.Old;
                usedOld.Add(p.Old);
            }

        // 되돌리기 자료(적용 전 배치)
        var before = areas.OrderBy(a => a.Name).Select(a => new
        {
            a.AreaId, a.Name, a.Corners, a.StationX, a.StationY, a.StationTheta, a.StationLiftMm,
            tasks = a.Tasks.OrderBy(t => t.Seq).Select(t => new { t.TaskId, t.Seq, t.Name }),
        }).ToList();

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        // 1단계 — 물려받는 영역 이름을 비켜 두고, 작업 순번을 음수로(유일 제약을 피해 옮긴다)
        var byOld = areas.ToDictionary(a => a.AreaId);
        foreach (var a in areas) a.Name = $"~sc-{a.AreaId:N}";
        var seq = 0;
        foreach (var t in tasks) t.Seq = -(++seq);
        await db.SaveChangesAsync(ct);

        var maxSort = await db.InspectionAreas.AsNoTracking().Where(a => a.TankId == tank && a.WallCode == wallCode)
            .MaxAsync(a => (int?)a.SortOrder, ct) ?? 0;
        var result = new List<SetCoverAppliedArea>();
        var targetOf = new Dictionary<Guid, Guid>();   // taskId → 새 영역
        var template = areas.FirstOrDefault();
        foreach (var g in groups)
        {
            InspectionAreaEntity target;
            if (g.Heir is { } heir) target = byOld[heir];
            else
            {
                target = new InspectionAreaEntity
                {
                    AreaId = Guid.NewGuid(), TankId = tank, WallCode = wallCode, SortOrder = ++maxSort, CreatedBy = userId,
                    StationStandoffM = template?.StationStandoffM, StationMaxBackwardMm = template?.StationMaxBackwardMm,
                };
                db.InspectionAreas.Add(target);
            }
            target.Name = g.Name!;
            target.Level = level;
            target.Corners = g.Corners!;
            (target.UMin, target.VMin, target.UMax, target.VMax) = (g.Box.MinU, g.Box.MinV, g.Box.MaxU, g.Box.MaxV);
            (target.StationX, target.StationY, target.StationTheta, target.StationLiftMm) = (g.X, g.Y, g.Theta, g.Lift);
            AreaRules.SyncStandoffToStation(target, s.Wall);   // 이격 = 제안 정차의 실제 거리(정차 없으면 그대로)
            var ordered = AreaRules.OrderByLine(g.Tasks);
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].AreaId = target.AreaId;
                ordered[i].Name = AreaRules.RenameForSeq(ordered[i], i + 1);
                ordered[i].Seq = -(seq + i + 1);   // 확정은 아래에서
                targetOf[ordered[i].TaskId] = target.AreaId;
            }
            seq += ordered.Count;
            result.Add(new SetCoverAppliedArea(target.AreaId, target.Name, g.Tasks.Count, g.Heir is not null, g.Unreachable,
                g.X, g.Y, g.Theta, g.Lift));
        }
        await db.SaveChangesAsync(ct);
        foreach (var g in groups)
        {
            var ordered = AreaRules.OrderByLine(g.Tasks);
            for (var i = 0; i < ordered.Count; i++) ordered[i].Seq = i + 1;
        }

        // 시나리오 — 기존 영역이 연결된 시나리오에 그 작업을 받은 새 영역을 연결
        var links = await db.ScenarioAreas.Where(sa => oldIds.Contains(sa.AreaId)).ToListAsync(ct);
        var added = 0;
        foreach (var byScenario in links.GroupBy(l => l.ScenarioId))
        {
            var linkedOld = byScenario.Select(l => l.AreaId).ToHashSet();
            var want = tasks.Where(t => linkedOld.Contains(oldOf[t.TaskId])).Select(t => targetOf[t.TaskId]).ToHashSet();
            var have = await db.ScenarioAreas.Where(sa => sa.ScenarioId == byScenario.Key).Select(sa => sa.AreaId).ToListAsync(ct);
            var max = await db.ScenarioAreas.Where(sa => sa.ScenarioId == byScenario.Key).MaxAsync(sa => (int?)sa.SortOrder, ct) ?? 0;
            foreach (var id in want.Where(id => !have.Contains(id)))
            {
                db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = byScenario.Key, AreaId = id, SortOrder = ++max });
                added++;
            }
        }

        // 물려주지 못한 기존 영역 — 비었으므로 지운다(시나리오 연결도 함께)
        var deleted = areas.Where(a => !usedOld.Contains(a.AreaId)).ToList();
        foreach (var a in deleted)
        {
            db.ScenarioAreas.RemoveRange(db.ScenarioAreas.Where(sa => sa.AreaId == a.AreaId));
            db.InspectionAreas.Remove(a);
        }
        var moved = tasks.Count(t => oldOf[t.TaskId] != targetOf[t.TaskId]);
        db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "", Action = auditAction, Target = $"{tank}/L{level}/{wallCode}",
            Detail = JsonSerializer.Serialize(new
            {
                stationsBefore = areas.Count, stationsAfter = groups.Count(g => !g.Unreachable), unreachableAreas = groups.Count(g => g.Unreachable),
                deleted = deleted.Select(a => a.AreaId), moved, scenarioLinksAdded = added, extra = auditExtra, before,
                after = result,
            }),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new SetCoverApplyResult(tank, level, wallCode, result, deleted.Count, added, moved);
    }

    /// <summary>작업을 크기 상한 창 단위로 묶는다(가장 많이 담는 창부터).</summary>
    public static IEnumerable<List<AreaTaskEntity>> Windows(List<AreaTaskEntity> tasks, double maxU, double maxV)
    {
        var box = tasks.Select(t => (U0: Math.Min(t.StartU, t.EndU), V0: Math.Min(t.StartV, t.EndV),
            U1: Math.Max(t.StartU, t.EndU), V1: Math.Max(t.StartV, t.EndV))).ToList();
        var left = Enumerable.Range(0, tasks.Count).ToList();
        while (left.Count > 0)
        {
            var w = StationSetCover.BestWindow(left, box, maxU, maxV);
            yield return w.Seams.Select(i => tasks[i]).ToList();
            left = left.Except(w.Seams).ToList();
        }
    }
}
