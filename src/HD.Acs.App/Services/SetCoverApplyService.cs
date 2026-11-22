using HD.Acs.Core.Planning;
using HD.Acs.Data;

namespace HD.Acs.App.Services;

/// <summary>적용 결과 영역 하나 — Inherited = 기존 영역 ID 를 물려받음, Unreachable = 어느 정차로도 못 닿는(또는 임시 제외) 용접선 묶음(X 영역).</summary>
public sealed record SetCoverAppliedArea(Guid AreaId, string Name, int TaskCount, bool Inherited, bool Unreachable,
    double? StationX, double? StationY, double? StationTheta, double? LiftMm);

/// <summary>적용 결과 — 새 영역 목록, 지운 기존 영역 수, 시나리오에 새로 연결한 수.</summary>
public sealed record SetCoverApplyResult(string TankId, int Level, string WallCode, List<SetCoverAppliedArea> Areas,
    int DeletedAreas, int ScenarioLinksAdded, int TasksMoved);

/// <summary>
/// 정차 묶음 최적화(set cover) 결과를 영역에 적용한다 — 면·층 하나의 영역을 제안 정차대로 다시 구성(한 트랜잭션).
/// 옮기기·영역 ID 물려주기·시나리오 연결·감사로그는 <see cref="AreaRegroupService"/> 공통 절차.
/// <list type="bullet">
/// <item>제안 정차 하나 = 영역 하나: 사각형은 맡은 작업의 자동 사각형, 정차 덮어쓰기(x·y·방향)·리프트 = 제안값.</item>
/// <item>어느 정차로도 못 닿는 작업·임시 제외 작업은 X 영역(정차 덮어쓰기 없음)으로 — 영역 크기 상한 창 단위로 묶는다. 시나리오 대상에서 빼는 것은 운영자 몫.</item>
/// <item>거부: 계산 뒤 작업이 바뀌었으면(추가·삭제·임시 제외 변경) 409 — 다시 계산. 이 면·층 영역이 진행 중 run 에 있으면 409.</item>
/// <item>감사로그 <c>SET_COVER_APPLY</c> 에 적용 전 영역·작업 배치를 통째로 남긴다(되돌리기 자료).</item>
/// </list>
/// </summary>
public sealed class SetCoverApplyService(AcsDbContext db)
{
    public async Task<SetCoverApplyResult> ApplyAsync(ReachSetCoverPlan plan, string? userId, CancellationToken ct = default)
    {
        var core = new AreaRegroupService(db);
        var s = await core.LoadAsync(plan.TankId, plan.Level, plan.WallCode, ct);
        var (tasks, level, wallCode) = (s.Tasks, s.Level, s.WallCode);
        var byId = tasks.ToDictionary(t => t.TaskId);

        // 계산 뒤 바뀌지 않았는가 — 검사 대상 작업 = 제안 정차 + 못 닿음
        var planned = plan.Stations.SelectMany(st => st.TaskIds).Concat(plan.Uncovered).ToList();
        if (planned.Count != planned.Distinct().Count())
            throw new AreaSplitException(400, "제안에 같은 작업이 두 번 들어 있습니다.");
        var enabled = tasks.Where(t => t.Enabled).Select(t => t.TaskId).ToHashSet();
        if (!enabled.SetEquals(planned))
            throw new AreaSplitException(409, "계산 뒤 이 면·층의 작업이 바뀌었습니다(추가·삭제·임시 제외) — 다시 계산한 뒤 적용하세요.");

        // 묶음 — 제안 정차 + X(못 닿음·임시 제외, 크기 상한 창 단위)
        var groups = plan.Stations
            .Select(st => new RegroupGroup(st.TaskIds.Select(id => byId[id]).ToList(), false, st.X, st.Y, st.Theta, st.StrokeMm)).ToList();
        var rest = plan.Uncovered.Select(id => byId[id]).Concat(tasks.Where(t => !t.Enabled)).ToList();
        foreach (var chunk in AreaRegroupService.Windows(rest, AreaGeometry.MaxSpanM, AreaGeometry.MaxSpanV(wallCode)))
            groups.Add(new RegroupGroup(chunk, true));

        // 이름 — 층 + S(정차)/X(못 닿음) 번호. 이 면의 다른 층 영역 이름과 겹치지 않게
        var taken = s.OtherNames.ToHashSet();
        int n = 0, x = 0;
        foreach (var g in groups.OrderBy(g => g.Unreachable).ThenBy(g => g.Tasks.Min(t => Math.Min(t.StartV, t.EndV))).ThenBy(g => g.Tasks.Min(t => Math.Min(t.StartU, t.EndU))))
        {
            string name;
            do name = g.Unreachable ? $"L{level}-X{++x:000}" : $"L{level}-S{++n:000}"; while (taken.Contains(name));
            taken.Add(name);
            g.Name = name;
        }
        return await core.ApplyAsync(s, groups, userId, "SET_COVER_APPLY", null, ct);
    }
}
