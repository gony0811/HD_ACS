using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// Run(시나리오 실행) 단위 TASK 진행률 집계 — 고객사 Enterprise/운영 화면 진행 경과 표시용.
/// [불변식 1] TASK 1개 = InspectionTask 1개 = OrderAction 1개 = inspection_result 1행.
///
/// 분모(전체 TASK 수)는 시나리오에 계획된 <c>ref.inspection_task</c> 총수다. OrderAction은
/// 층별 미션 릴리즈 시점에 생성되므로(부분 발행) 분모로 쓰면 진행 중 값이 튀어 부적절하다.
/// 분자(종결 TASK 수)는 이 Run의 미션들에 발행된 OrderAction 중 FINISHED/FAILED 개수다.
/// </summary>
public sealed class ProgressService
{
    private readonly AcsDbContext _db;

    public ProgressService(AcsDbContext db) => _db = db;

    /// <summary>runId의 TASK 진행률을 계산한다. Run이 없으면 0-집계를 반환.</summary>
    public async Task<RunProgress> ComputeRunProgressAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ScenarioRuns.AsNoTracking()
            .Include(r => r.Missions)
            .FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return RunProgress.Empty(runId);

        // 분모: 시나리오 계획 TASK 총수 (InspectionTask ↔ InspectionPoint.ScenarioId).
        var total = await (
            from t in _db.InspectionTasks.AsNoTracking()
            join p in _db.InspectionPoints.AsNoTracking() on t.PointId equals p.PointId
            where p.ScenarioId == run.ScenarioId
            select t.TaskId).CountAsync(ct);

        var missionIds = run.Missions.Select(m => m.MissionId).ToList();

        // 분자: 이 Run의 미션들에 발행된 OrderAction 상태별 집계.
        var byStatus = missionIds.Count == 0
            ? new List<StatusCount>()
            : await _db.OrderActions.AsNoTracking()
                .Where(a => missionIds.Contains(a.MissionId))
                .GroupBy(a => a.Status)
                .Select(g => new StatusCount(g.Key, g.Count()))
                .ToListAsync(ct);

        var released  = byStatus.Sum(x => x.Count);
        var succeeded = byStatus.Where(x => x.Status == "FINISHED").Sum(x => x.Count);
        var failed    = byStatus.Where(x => x.Status == "FAILED").Sum(x => x.Count);
        var completed = succeeded + failed;

        // 정상적으로 total>0. 계획 정보가 아직 없으면(0) 발행분을 분모로 폴백.
        var denom = total > 0 ? total : released;
        var percent = denom > 0 ? Math.Round(completed * 100.0 / denom, 1) : 0.0;

        return new RunProgress(
            RunId: runId,
            TotalTasks: denom,
            ReleasedTasks: released,
            CompletedTasks: completed,
            SucceededTasks: succeeded,
            FailedTasks: failed,
            PendingTasks: Math.Max(0, denom - completed),
            Percent: percent);
    }

    private sealed record StatusCount(string Status, int Count);
}

/// <summary>
/// Run 단위 TASK 진행률 스냅샷. Percent = CompletedTasks / TotalTasks × 100 (종결 기준, 소수 1자리).
/// Completed = Succeeded(FINISHED) + Failed(FAILED). Fraction 은 0~1 진행바용.
/// </summary>
public sealed record RunProgress(
    Guid RunId,
    int TotalTasks,
    int ReleasedTasks,
    int CompletedTasks,
    int SucceededTasks,
    int FailedTasks,
    int PendingTasks,
    double Percent)
{
    public double Fraction => TotalTasks > 0 ? (double)CompletedTasks / TotalTasks : 0.0;

    public static RunProgress Empty(Guid runId) => new(runId, 0, 0, 0, 0, 0, 0, 0.0);
}
