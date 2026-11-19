using System.Collections.Concurrent;
using HD.Acs.Core.Reach;

namespace HD.Acs.App.Services;

/// <summary>
/// 정차 묶음 최적화(set cover) 작업 — 면 하나에 수 분 걸릴 수 있어 요청 하나로 기다리지 않고 작업으로 돌린다.
/// 시작하면 작업 번호를 돌려주고, 화면은 진행률(판정한 후보 수)과 결과를 조회한다. 끝난 작업은 1시간 뒤 지운다.
/// </summary>
public sealed class SetCoverJobs
{
    public sealed class Job
    {
        public Guid JobId { get; init; }
        public string TankId { get; init; } = "";
        public int Level { get; init; }
        public string WallCode { get; init; } = "";
        public string State { get; set; } = "RUNNING";   // RUNNING | DONE | FAILED | CANCELLED
        public int Done { get; set; }
        public int Total { get; set; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public string? Error { get; set; }
        public ReachSetCoverPlan? Result { get; set; }
        /// <summary>이 결과를 영역에 적용했는가 — 두 번 적용하지 않는다.</summary>
        public bool Applied { get; set; }
        internal CancellationTokenSource Cts { get; } = new();
    }

    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SetCoverJobs> _log;

    public SetCoverJobs(IServiceScopeFactory scopes, ILogger<SetCoverJobs> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public Job? Get(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var j) || j.State != "RUNNING") return false;
        j.Cts.Cancel();
        return true;
    }

    public Job Start(string tankId, int level, string wallCode, SetCoverOptions options)
    {
        foreach (var old in _jobs.Values.Where(j => j.EndedAt is { } e && DateTimeOffset.UtcNow - e > TimeSpan.FromHours(1)).ToList())
            _jobs.TryRemove(old.JobId, out _);
        var job = new Job { JobId = Guid.NewGuid(), TankId = tankId, Level = level, WallCode = wallCode };
        _jobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sim = scope.ServiceProvider.GetRequiredService<ReachSimService>();
                job.Result = await sim.SetCoverAsync(tankId, level, wallCode, options, job.Cts.Token,
                    new SyncProgress(p => { job.Done = p.Done; job.Total = p.Total; }));
                job.State = job.Result is null ? "FAILED" : "DONE";
                if (job.Result is null) job.Error = "선창·면을 찾지 못했습니다";
                else
                    _log.LogInformation("정차 묶음 최적화 {Tank} L{Level} {Wall}: 정차 {Now} (지금 {Cur}), 덮음 {Cov}/{Welds}, 후보 {Cells}, 판정 {Eval}, {Ms} ms",
                        tankId, level, wallCode, job.Result.Stations.Count, job.Result.CurrentStations,
                        job.Result.SafeCovered + job.Result.ClearCovered, job.Result.Welds, job.Result.Cells, job.Result.Evaluations, job.Result.ElapsedMs);
            }
            catch (OperationCanceledException) { job.State = "CANCELLED"; }
            catch (Exception ex)
            {
                job.State = "FAILED";
                job.Error = ex.Message;
                _log.LogWarning(ex, "정차 묶음 최적화 실패 {Tank} L{Level} {Wall}", tankId, level, wallCode);
            }
            finally { job.EndedAt = DateTimeOffset.UtcNow; }
        });
        return job;
    }

    /// <summary>진행 보고를 바로 반영(Progress&lt;T&gt; 는 동기화 컨텍스트로 넘겨 늦게 반영된다).</summary>
    private sealed class SyncProgress(Action<(int Done, int Total)> on) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => on(value);
    }
}
