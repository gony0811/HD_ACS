using System.Text.Json;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HD.Acs.App.Services;

/// <summary>
/// Run(시나리오 실행) 단위 TASK 진행률 집계 [SAIGE 연동 사양서 v2.6 §5.4/§6] — SAIGE·운영 화면 공용.
/// TASK 1개 = 용접선 1구간(ref.area_task) = 검사 결과 1건. 촬영 건수·발행 액션 수는 진행률이 아니다.
///
/// - 분모(totalTasks): run 시작 시 큐에 전개된 TASK 총수로 **고정**(run.scenario_run.total_tasks) [§6.1].
///   정차 단위 순차 발행이라 "발행분"을 분모로 쓰면 1/1=100% → 1/2=50%로 되돌아가기 때문이다.
/// - 분자(completedTasks): **고유 TASK 기준** 종결 수. 재시도마다 액션이 새로 발행되지만 같은 TASK의
///   최종 결과 1건만 센다(<see cref="TaskOutcome.Classify"/>) [§6.3]. 따라서 percent는 단조 증가, 100 이하 [§6.6].
/// - excludedTasks: 층 T_W_D 부재 등으로 큐에서 제외된 TASK. 분모 미포함 — 100% 도달을 보장하되 별도 보고 [§6.4].
/// </summary>
public sealed class ProgressService
{
    /// <summary>조회 API 캐시 TTL [§5.4] — 1~5초 주기 polling에도 DB 부하가 늘지 않게 한다.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    private readonly AcsDbContext _db;
    private readonly IMemoryCache _cache;

    public ProgressService(AcsDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    /// <summary>조회(pull) 경로 — 약 1초 TTL 캐시. Run이 없으면 null.</summary>
    public async Task<RunProgress?> GetCachedAsync(Guid runId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(runId), out RunProgress? cached)) return cached;
        if (!await _db.ScenarioRuns.AsNoTracking().AnyAsync(r => r.RunId == runId, ct)) return null;
        return await ComputeRunProgressAsync(runId, ct);
    }

    /// <summary>runId의 TASK 진행률을 새로 계산하고 캐시를 갱신한다(push 경로는 항상 최신값). Run이 없으면 0-집계.</summary>
    public async Task<RunProgress> ComputeRunProgressAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ScenarioRuns.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => new { r.ScenarioId, r.TotalTasks, r.ExcludedTasks })
            .FirstOrDefaultAsync(ct);
        if (run is null) return RunProgress.Empty(runId);

        var workItems = await _db.WorkItems.AsNoTracking().Where(w => w.RunId == runId)
            .Select(w => new { w.WorkItemId, w.Status, w.Actions }).ToListAsync(ct);

        var progress = workItems.Count > 0
            ? await ComputeFromQueueAsync(runId, run.TotalTasks, run.ExcludedTasks,
                workItems.Select(w => (w.WorkItemId, w.Status, w.Actions)).ToList(), ct)
            : await ComputeLegacyAsync(runId, run.ScenarioId, ct);

        _cache.Set(CacheKey(runId), progress, CacheTtl);
        return progress;
    }

    /// <summary>영역 기반(greedy 큐) run — 고유 TASK 최종 결과 집계.</summary>
    private async Task<RunProgress> ComputeFromQueueAsync(Guid runId, int? fixedTotal, int excluded,
        List<(Guid WorkItemId, string Status, string? Actions)> workItems, CancellationToken ct)
    {
        var wiStatus = workItems.ToDictionary(w => w.WorkItemId, w => w.Status);
        var wiIds = wiStatus.Keys.ToList();

        var actions = await _db.OrderActions.AsNoTracking()
            .Where(a => a.TaskId != null && a.WorkItemId != null && wiIds.Contains(a.WorkItemId.Value))
            .Select(a => new { TaskId = a.TaskId!.Value, WorkItemId = a.WorkItemId!.Value, a.Status })
            .ToListAsync(ct);

        int succeeded = 0, failed = 0, skipped = 0;
        var byTask = actions.GroupBy(a => a.TaskId).ToList();
        foreach (var g in byTask)
        {
            var status = wiStatus[g.First().WorkItemId];   // 한 TASK의 모든 시도는 같은 정차(work_item) 소속
            switch (TaskOutcome.Classify(g.Select(a => a.Status), status))
            {
                case TaskOutcome.Success: succeeded++; break;
                case TaskOutcome.Failed or TaskOutcome.Skipped:
                    failed++;
                    if (status == "SKIPPED") skipped++;   // 재시도 초과로 건너뜀 — failedTasks의 부분집합 [§5.4]
                    break;
            }
        }

        // 분모: 시작 시 고정값. 컬럼 도입 전 run(null)은 큐에 저장된 액션 payload 수로 복원(큐는 시작 후 불변).
        int total = fixedTotal ?? workItems.Sum(w => CountPlannedTasks(w.Actions));
        return RunProgress.Create(runId, total, released: byTask.Count, succeeded, failed, skipped, excluded);
    }

    /// <summary>
    /// 구 경로(seam/inspection_point 기반, 운영 비활성) — work_item 없는 run. TASK 1개 = 액션 1개라 중복 집계 문제가 없다.
    /// </summary>
    private async Task<RunProgress> ComputeLegacyAsync(Guid runId, Guid scenarioId, CancellationToken ct)
    {
        var total = await (
            from t in _db.InspectionTasks.AsNoTracking()
            join p in _db.InspectionPoints.AsNoTracking() on t.PointId equals p.PointId
            where p.ScenarioId == scenarioId
            select t.TaskId).CountAsync(ct);

        var missionIds = await _db.Missions.AsNoTracking().Where(m => m.RunId == runId)
            .Select(m => m.MissionId).ToListAsync(ct);
        var statuses = await _db.OrderActions.AsNoTracking()
            .Where(a => missionIds.Contains(a.MissionId)).Select(a => a.Status).ToListAsync(ct);

        return RunProgress.Create(runId, total, released: statuses.Count,
            succeeded: statuses.Count(s => s == "FINISHED"), failed: statuses.Count(s => s == "FAILED"),
            skipped: 0, excluded: 0);
    }

    private static int CountPlannedTasks(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static string CacheKey(Guid runId) => $"run-progress:{runId}";
}

/// <summary>
/// Run 단위 TASK 진행률 스냅샷 [§5.4]. Completed = Succeeded + Failed(종결 기준, 실패도 "더 이상 진행 안 함"이므로 포함).
/// Skipped ⊂ Failed — 별도로 더하지 않는다. Excluded는 분모 미포함. Percent 소수 1자리, Fraction 0~1.
/// </summary>
public sealed record RunProgress(
    Guid RunId,
    int TotalTasks,
    int ReleasedTasks,
    int CompletedTasks,
    int SucceededTasks,
    int FailedTasks,
    int SkippedTasks,
    int ExcludedTasks,
    int PendingTasks,
    double Percent,
    double Fraction)
{
    public static RunProgress Create(Guid runId, int total, int released, int succeeded, int failed, int skipped, int excluded)
    {
        int completed = Math.Min(succeeded + failed, Math.Max(total, 0));   // 100% 초과 방어 [§6.6]
        double fraction = total > 0 ? (double)completed / total : 0.0;
        return new RunProgress(runId, total, released, completed, succeeded, failed, skipped, excluded,
            PendingTasks: Math.Max(0, total - completed),
            Percent: Math.Round(fraction * 100.0, 1, MidpointRounding.AwayFromZero),
            Fraction: Math.Round(fraction, 4, MidpointRounding.AwayFromZero));
    }

    public static RunProgress Empty(Guid runId) => Create(runId, 0, 0, 0, 0, 0, 0);
}
