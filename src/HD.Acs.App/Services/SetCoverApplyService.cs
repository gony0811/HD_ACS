using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>적용 결과 영역 하나 — Inherited = 기존 영역 ID 를 물려받음, Unreachable = 어느 정차로도 못 닿는(또는 임시 제외) 용접선 묶음(X 영역).</summary>
public sealed record SetCoverAppliedArea(Guid AreaId, string Name, int TaskCount, bool Inherited, bool Unreachable,
    double? StationX, double? StationY, double? StationTheta, double? LiftMm);

/// <summary>적용 결과 — 새 영역 목록, 지운 기존 영역 수, 시나리오에 새로 연결한 수.</summary>
public sealed record SetCoverApplyResult(string TankId, int Level, string WallCode, List<SetCoverAppliedArea> Areas,
    int DeletedAreas, int ScenarioLinksAdded, int TasksMoved);

/// <summary>
/// 정차 묶음 최적화(set cover) 결과를 영역에 적용한다 — 면·층 하나의 영역을 제안 정차대로 다시 구성(한 트랜잭션).
/// <list type="bullet">
/// <item>제안 정차 하나 = 영역 하나: 사각형은 맡은 작업의 자동 사각형, 정차 덮어쓰기(x·y·방향)·리프트 = 제안값.</item>
/// <item>어느 정차로도 못 닿는 작업·임시 제외 작업은 X 영역(정차 덮어쓰기 없음)으로 — 영역 크기 상한 창 단위로 묶는다. 시나리오 대상에서 빼는 것은 운영자 몫.</item>
/// <item>작업(taskId·좌표·유형·가지·임시 제외)은 그대로 옮긴다 — SAIGE productId·attempt 누적·검사 기록이 끊기지 않는다. 순번은 라인 순, 도구 형식 이름(H01·V03)만 새 순번으로.</item>
/// <item>영역 ID 물려주기: 새 영역은 그 작업이 가장 많이 속해 있던 기존 영역의 ID 를 받는다(기존 영역 하나당 한 번) — 지난 run 의 work_item·결과 조회가 계속 풀린다.
///   물려주지 못한 기존 영역은 지운다(작업은 이미 옮겨 비어 있다).</item>
/// <item>시나리오: 지운·바뀐 기존 영역이 연결돼 있던 시나리오에는 그 작업을 받은 새 영역을 연결한다(부분 검사 대상이 줄지 않게).</item>
/// <item>거부: 계산 뒤 작업이 바뀌었으면(추가·삭제·임시 제외 변경) 409 — 다시 계산. 이 면·층 영역이 진행 중 run 에 있으면 409.</item>
/// <item>감사로그 <c>SET_COVER_APPLY</c> 에 적용 전 영역·작업 배치를 통째로 남긴다(되돌리기 자료).</item>
/// </list>
/// </summary>
public sealed class SetCoverApplyService(AcsDbContext db)
{
    public async Task<SetCoverApplyResult> ApplyAsync(ReachSetCoverPlan plan, string? userId, CancellationToken ct = default)
    {
        var (tank, level, wallCode) = (plan.TankId, plan.Level, plan.WallCode);
        var areas = await db.InspectionAreas.Include(a => a.Tasks)
            .Where(a => a.TankId == tank && a.Level == level && a.WallCode == wallCode).ToListAsync(ct);
        var tasks = areas.SelectMany(a => a.Tasks).ToList();
        var byId = tasks.ToDictionary(t => t.TaskId);

        // 계산 뒤 바뀌지 않았는가 — 검사 대상 작업 = 제안 정차 + 못 닿음
        var planned = plan.Stations.SelectMany(s => s.TaskIds).Concat(plan.Uncovered).ToList();
        if (planned.Count != planned.Distinct().Count())
            throw new AreaSplitException(400, "제안에 같은 작업이 두 번 들어 있습니다.");
        var enabled = tasks.Where(t => t.Enabled).Select(t => t.TaskId).ToHashSet();
        if (!enabled.SetEquals(planned))
            throw new AreaSplitException(409, "계산 뒤 이 면·층의 작업이 바뀌었습니다(추가·삭제·임시 제외) — 다시 계산한 뒤 적용하세요.");
        var oldIds = areas.Select(a => a.AreaId).ToList();
        var busy = await db.WorkItems.AsNoTracking()
            .Where(w => oldIds.Contains(w.AreaId) && (w.Status == "PENDING" || w.Status == "DISPATCHED"))
            .Join(db.ScenarioRuns.AsNoTracking().Where(r => r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"),
                w => w.RunId, r => r.RunId, (w, r) => r.RunId)
            .AnyAsync(ct);
        if (busy) throw new AreaSplitException(409, "이 면·층 영역이 진행 중인 run 에 남아 있습니다 — run 이 끝난 뒤 적용하세요.");

        var wall = await db.Walls.AsNoTracking().FirstOrDefaultAsync(w => w.TankId == tank && w.WallCode == wallCode, ct)
                   ?? throw new AreaSplitException(404, $"면이 없습니다: {tank}/{wallCode}.");
        var geom = await db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tank, ct)
                   ?? throw new AreaSplitException(400, $"선창 지오메트리가 없습니다: {tank}.");

        // 묶음 — 제안 정차 + X(못 닿음·임시 제외, 크기 상한 창 단위)
        var groups = plan.Stations.Select(s => new Group(s.TaskIds.Select(id => byId[id]).ToList(), false, s.X, s.Y, s.Theta, s.StrokeMm)).ToList();
        var rest = plan.Uncovered.Select(id => byId[id]).Concat(tasks.Where(t => !t.Enabled)).ToList();
        foreach (var chunk in Windows(rest, AreaGeometry.MaxSpanM, AreaGeometry.MaxSpanV(wallCode)))
            groups.Add(new Group(chunk, true, null, null, null, null));

        // 이름 — 층 + S(정차)/X(못 닿음) 번호. 이 면의 다른 층 영역 이름과 겹치지 않게
        var others = await db.InspectionAreas.AsNoTracking()
            .Where(a => a.TankId == tank && a.WallCode == wallCode && a.Level != level).Select(a => a.Name).ToListAsync(ct);
        var taken = others.ToHashSet();
        int s = 0, x = 0;
        foreach (var g in groups.OrderBy(g => g.Unreachable).ThenBy(g => g.Tasks.Min(t => Math.Min(t.StartV, t.EndV))).ThenBy(g => g.Tasks.Min(t => Math.Min(t.StartU, t.EndU))))
        {
            string name;
            do name = g.Unreachable ? $"L{level}-X{++x:000}" : $"L{level}-S{++s:000}"; while (taken.Contains(name));
            taken.Add(name);
            g.Name = name;
        }

        // 사각형·층 검증 — DB 를 건드리기 전에 전부
        foreach (var g in groups)
        {
            var corners = AreaRules.AutoCornersForTasks(g.Tasks, wall, geom, level).ToList();
            if (AreaRules.ValidateCorners(corners, wallCode, wall.ULen, wall.VLen, out var bb) is { } cv)
                throw new AreaSplitException(400, $"{g.Name} 사각형: {cv}");
            var lv = AreaRules.DeriveLevel(geom, wall, bb.MinV, bb.MaxV, out var reason)
                     ?? throw new AreaSplitException(400, $"{g.Name} 층 유도 실패: {reason}");
            if (lv != level) throw new AreaSplitException(400, $"{g.Name} 사각형이 다른 층(L{lv})으로 유도됩니다.");
            g.Corners = JsonSerializer.Serialize(corners);
            g.Box = (bb.MinU, bb.MinV, bb.MaxU, bb.MaxV);
            foreach (var t in g.Tasks)
                if (AreaTaskRules.Validate(t.SeamType, g.Corners, t.StartU, t.StartV, t.EndU, t.EndV) is { } tv)
                    throw new AreaSplitException(400, $"{g.Name}: {tv}");
        }

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
            g.Target = target;
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
            UserId = userId ?? "", Action = "SET_COVER_APPLY", Target = $"{tank}/L{level}/{wallCode}",
            Detail = JsonSerializer.Serialize(new
            {
                stationsBefore = areas.Count, stationsAfter = groups.Count(g => !g.Unreachable), unreachableAreas = groups.Count(g => g.Unreachable),
                deleted = deleted.Select(a => a.AreaId), moved, scenarioLinksAdded = added, before,
                after = result,
            }),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new SetCoverApplyResult(tank, level, wallCode, result, deleted.Count, added, moved);
    }

    /// <summary>작업을 크기 상한 창 단위로 묶는다(가장 많이 담는 창부터) — X 영역용.</summary>
    private static IEnumerable<List<AreaTaskEntity>> Windows(List<AreaTaskEntity> tasks, double maxU, double maxV)
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

    private sealed class Group(List<AreaTaskEntity> tasks, bool unreachable, double? x, double? y, double? theta, double? lift)
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
        public InspectionAreaEntity? Target { get; set; }
    }
}
