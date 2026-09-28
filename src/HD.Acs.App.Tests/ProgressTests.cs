using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>SAIGE 연동 2단계 [사양서 v2.6 §5.4/§6] — 진행률 분모 고정·고유 TASK 집계·제외·캐시, 큐 전개 시 taskId 탑재.</summary>
public class ProgressTests
{
    private static AcsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProgressService Service(AcsDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));

    private sealed class RunBuilder(AcsDbContext db, Guid runId)
    {
        private readonly Guid _mission = Guid.NewGuid();
        private int _minute;

        /// <summary>정차 1곳 + 그 정차의 TASK들. attempts[i] = i번째 TASK의 시도별 액션 상태.</summary>
        public Guid[] Stop(string wiStatus, params string[][] attempts)
        {
            var wi = Guid.NewGuid();
            var taskIds = attempts.Select(_ => Guid.NewGuid()).ToArray();
            db.WorkItems.Add(new WorkItemEntity
            {
                WorkItemId = wi, RunId = runId, AreaId = Guid.NewGuid(), MapId = "CT1-L1", Status = wiStatus,
                Actions = JsonSerializer.Serialize(taskIds.Select(t => new { taskId = t })),
            });
            for (int i = 0; i < attempts.Length; i++)
                foreach (var status in attempts[i])
                    db.OrderActions.Add(new OrderActionEntity
                    {
                        ActionId = Guid.NewGuid(), MissionId = _mission, WorkItemId = wi, TaskId = taskIds[i],
                        ActionType = "startWeldInspection", Status = status,
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(_minute++),
                    });
            return taskIds;
        }
    }

    private static async Task<(AcsDbContext Db, Guid RunId, RunBuilder B)> NewRunAsync(int? total, int excluded = 0)
    {
        var db = NewDb();
        var runId = Guid.NewGuid();
        db.ScenarioRuns.Add(new ScenarioRunEntity
        { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = "AMR01", State = "RUNNING", TotalTasks = total, ExcludedTasks = excluded });
        await db.SaveChangesAsync();
        return (db, runId, new RunBuilder(db, runId));
    }

    /// <summary>§6.1 — 정차 단위 순차 발행이어도 분모는 계획 총수로 고정: 첫 정차 완료 직후 100%가 아니라 2/6.</summary>
    [Fact]
    public async Task Denominator_IsFixedAtRunStart_NotReleasedCount()
    {
        var (db, runId, b) = await NewRunAsync(total: 6);
        b.Stop("DONE", new[] { "FINISHED" }, new[] { "FINISHED" });   // 정차 1 완료
        b.Stop("PENDING", Array.Empty<string>(), Array.Empty<string>());
        b.Stop("PENDING", Array.Empty<string>(), Array.Empty<string>());
        await db.SaveChangesAsync();

        var p = await Service(db).ComputeRunProgressAsync(runId);

        Assert.Equal((6, 2, 2, 4), (p.TotalTasks, p.ReleasedTasks, p.CompletedTasks, p.PendingTasks));
        Assert.Equal(33.3, p.Percent);
        Assert.Equal(0.3333, p.Fraction);
    }

    /// <summary>§6.3 — 재시도로 액션이 여러 번 발행돼도 고유 TASK 최종 결과 1건만 센다.</summary>
    [Fact]
    public async Task Retries_AreNotDoubleCounted()
    {
        var (db, runId, b) = await NewRunAsync(total: 5);
        // 정차 A: 1차에 T1 성공·T2 실패 → 재큐잉 → 2차에 둘 다 성공 (액션 4건, TASK 2건)
        b.Stop("DONE", new[] { "FINISHED", "FINISHED" }, new[] { "FAILED", "FINISHED" });
        // 정차 B: 재시도 소진 — T3는 1차 성공(이후 재실행 실패), T4는 전부 실패
        b.Stop("SKIPPED", new[] { "FINISHED", "FAILED" }, new[] { "FAILED", "FAILED" });
        // 정차 C: 1차 실패 후 재시도 대기 → 미종결
        b.Stop("PENDING", new[] { "FAILED" });
        await db.SaveChangesAsync();

        var p = await Service(db).ComputeRunProgressAsync(runId);

        Assert.Equal(5, p.ReleasedTasks);                                    // 고유 TASK 5건(액션은 9건)
        Assert.Equal((3, 1, 1), (p.SucceededTasks, p.FailedTasks, p.SkippedTasks));   // skipped ⊂ failed
        Assert.Equal((4, 1, 80.0), (p.CompletedTasks, p.PendingTasks, p.Percent));
    }

    /// <summary>§6.6 — 진행 중 어느 시점에도 percent는 감소하지 않는다(실패→재큐잉→재발행 구간 포함).</summary>
    [Fact]
    public async Task Percent_IsMonotonic_AcrossRetryCycle()
    {
        var (db, runId, b) = await NewRunAsync(total: 2);
        var svc = Service(db);
        var tasks = b.Stop("DISPATCHED", new[] { "FINISHED" }, new[] { "FAILED" });
        await db.SaveChangesAsync();
        var wi = await db.WorkItems.FirstAsync();
        var seen = new List<double> { (await svc.ComputeRunProgressAsync(runId)).Percent };   // T1 성공, T2 실패(여유 있음)

        wi.Status = "PENDING"; await db.SaveChangesAsync();                                    // 재큐잉
        seen.Add((await svc.ComputeRunProgressAsync(runId)).Percent);

        wi.Status = "DISPATCHED";                                                              // 재발행 — 둘 다 새 액션 WAITING
        foreach (var t in tasks)
            db.OrderActions.Add(new OrderActionEntity { ActionId = Guid.NewGuid(), WorkItemId = wi.WorkItemId, TaskId = t, Status = "WAITING", CreatedAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        seen.Add((await svc.ComputeRunProgressAsync(runId)).Percent);

        foreach (var a in await db.OrderActions.Where(a => a.Status == "WAITING").ToListAsync()) a.Status = "FINISHED";
        wi.Status = "DONE"; await db.SaveChangesAsync();
        seen.Add((await svc.ComputeRunProgressAsync(runId)).Percent);

        Assert.Equal(new[] { 50.0, 50.0, 50.0, 100.0 }, seen);
    }

    /// <summary>§6.4 — 제외 TASK는 분모 미포함(100% 도달 보장)이되 별도 보고된다.</summary>
    [Fact]
    public async Task Excluded_NotInDenominator_ButReported()
    {
        var (db, runId, b) = await NewRunAsync(total: 1, excluded: 7);
        b.Stop("DONE", new[] { "FINISHED" });
        await db.SaveChangesAsync();

        var p = await Service(db).ComputeRunProgressAsync(runId);

        Assert.Equal((1, 7, 100.0, 0), (p.TotalTasks, p.ExcludedTasks, p.Percent, p.PendingTasks));
    }

    /// <summary>total_tasks 컬럼 도입 전 run(null) — 큐에 저장된 액션 payload 수로 분모 복원.</summary>
    [Fact]
    public async Task LegacyRun_NullTotal_FallsBackToQueuePayload()
    {
        var (db, runId, b) = await NewRunAsync(total: null);
        b.Stop("DONE", new[] { "FINISHED" }, new[] { "FINISHED" });
        b.Stop("PENDING", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        await db.SaveChangesAsync();

        var p = await Service(db).ComputeRunProgressAsync(runId);

        Assert.Equal((5, 2, 40.0), (p.TotalTasks, p.CompletedTasks, p.Percent));
    }

    [Fact]
    public async Task CachedRead_ServesSnapshotWithinTtl_AndNullForUnknownRun()
    {
        var (db, runId, b) = await NewRunAsync(total: 2);
        b.Stop("DONE", new[] { "FINISHED" });
        await db.SaveChangesAsync();
        var svc = Service(db);

        Assert.Equal(1, (await svc.GetCachedAsync(runId))!.CompletedTasks);
        b.Stop("DONE", new[] { "FINISHED" });
        await db.SaveChangesAsync();
        Assert.Equal(1, (await svc.GetCachedAsync(runId))!.CompletedTasks);              // TTL 내 — DB 재조회 없음
        Assert.Equal(2, (await svc.ComputeRunProgressAsync(runId)).CompletedTasks);       // push 경로는 항상 최신 + 캐시 갱신
        Assert.Equal(2, (await svc.GetCachedAsync(runId))!.CompletedTasks);
        Assert.Null(await svc.GetCachedAsync(Guid.NewGuid()));                            // 없는 run → 404
    }

    [Fact]
    public void Serialized_MatchesSpecFieldNames()
    {
        var json = JsonSerializer.Serialize(RunProgress.Create(Guid.Empty, 40, 24, 11, 1, 0, 0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var key in new[] { "runId", "totalTasks", "releasedTasks", "completedTasks", "succeededTasks", "failedTasks",
                     "skippedTasks", "excludedTasks", "pendingTasks", "percent", "fraction" })
            Assert.Contains($"\"{key}\":", json);
        Assert.Contains("\"percent\":30,", json);      // §5.4 예시: 12/40 = 30.0
        Assert.Contains("\"fraction\":0.3", json);
    }

    // ── 큐 전개: 분모/제외 고정 + params.taskId 탑재 ────────────────────

    [Fact]
    public async Task BuildQueue_FixesTotals_ExcludesUncalibratedFloor_AndCarriesTaskId()
    {
        var db = NewDb();
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.Maps.Add(new MapEntity { MapId = "CT1-L2", TankId = "CT1", Level = 2, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 1, Ty = 2, YawRad = 0.1 });   // L2는 미보정

        InspectionAreaEntity Area(string name, int level, int tasks) => new()
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "PM", Level = level, Name = name,
            Corners = "[[3,0.2],[6,0.2],[6,1.5],[3,1.5]]",
            Tasks = Enumerable.Range(1, tasks).Select(i => new AreaTaskEntity
            { TaskId = Guid.NewGuid(), Seq = i, StartU = 3.2, StartV = 0.5, EndU = 5.8, EndV = 0.5 }).ToList(),
        };
        var l1 = Area("PM-L1-01", 1, 3);
        db.InspectionAreas.AddRange(l1, Area("PM-L2-01", 2, 4));
        await db.SaveChangesAsync();
        // StartRunAsync와 같은 순서: run Add → 큐 전개 → 한 번에 저장
        var run = new ScenarioRunEntity { RunId = Guid.NewGuid(), ScenarioId = Guid.NewGuid(), RobotId = "AMR01" };
        db.ScenarioRuns.Add(run);

        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        await dispatcher.BuildQueueAsync(run, "CT1", default);
        await db.SaveChangesAsync();

        Assert.Equal((3, 4), (run.TotalTasks, run.ExcludedTasks));   // 미보정 L2의 4건은 분모 밖 + 별도 보고
        var wi = Assert.Single(await db.WorkItems.AsNoTracking().ToListAsync());
        var actions = JsonDocument.Parse(wi.Actions!).RootElement.EnumerateArray().ToList();
        Assert.Equal(l1.Tasks.Select(t => t.TaskId.ToString()).OrderBy(x => x),
            actions.Select(a => a.GetProperty("params").GetProperty("taskId").GetString()!).OrderBy(x => x));
        Assert.All(actions, a => Assert.False(a.GetProperty("params").TryGetProperty("attempt", out _)));   // attempt는 발행 시점 발급
    }
}
