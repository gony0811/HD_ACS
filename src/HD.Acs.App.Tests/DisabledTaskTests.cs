using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>용접선 임시 제외(enabled = false) — 큐 전개·재개·재검사에서 그 용접선을 싣지 않는다. 계획·taskId 는 그대로.</summary>
public class DisabledTaskTests
{
    private sealed record Fixture(AcsDbContext Db, InspectionDispatcher Dispatcher, MissionService Missions,
        Guid ScenarioId, InspectionAreaEntity A, InspectionAreaEntity B);

    /// <summary>F면 L1에 영역 2개(A: 용접선 3개 / B: 용접선 1개) — 시나리오 대상 지정 없음(선창 전체).</summary>
    private static async Task<Fixture> Seed()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = 0 });

        static AreaTaskEntity Task(int seq, double u) => new()
        {
            TaskId = Guid.NewGuid(), Seq = seq, StartU = u, StartV = 1.64, EndU = u, EndV = 2.0,
            SectionDxfId = "DXF-1", ProfileId = "PROF-1",
        };
        var a = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.8,
            Tasks = { Task(1, 3.0), Task(2, 3.2), Task(3, 3.4) },
        };
        var b = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-02", SortOrder = 1,
            Corners = "[[4.9,1.5],[5.6,1.5],[5.6,2.1],[4.9,2.1]]", StationStandoffM = 1.8,
            Tasks = { Task(1, 5.0) },
        };
        db.InspectionAreas.AddRange(a, b);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        // 연결된 로봇(층 미보고) — 배차는 보류되고 큐 상태만 본다
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var cfg = new ConfigurationBuilder().Build();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, cfg, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return new Fixture(db, dispatcher, missions, sc.ScenarioId, a, b);
    }

    private static async Task Disable(AcsDbContext db, params Guid[] taskIds)
    {
        foreach (var t in await db.AreaTasks.Where(t => taskIds.Contains(t.TaskId)).ToListAsync()) t.Enabled = false;
        await db.SaveChangesAsync();
    }

    private static List<Guid> TaskIdsOf(WorkItemEntity wi) =>
        JsonNode.Parse(wi.Actions!)!.AsArray().Select(n => Guid.Parse(n!["taskId"]!.GetValue<string>())).ToList();

    [Fact]
    public async Task NewTask_IsEnabledByDefault()
    {
        var f = await Seed();
        Assert.All(await f.Db.AreaTasks.AsNoTracking().ToListAsync(), t => Assert.True(t.Enabled));
    }

    [Fact]
    public async Task DisabledWeld_IsNotQueued_OthersKeepTheirOrder()
    {
        var f = await Seed();
        var off = f.A.Tasks.Single(t => t.Seq == 2);
        await Disable(f.Db, off.TaskId);

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");

        var wiA = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId && w.AreaId == f.A.AreaId);
        Assert.Equal(new[] { f.A.Tasks[0].TaskId, f.A.Tasks[2].TaskId }, TaskIdsOf(wiA));
        // 남은 용접선의 순번은 영역 안에서의 값 그대로(1, 3) — AMR 레시피·jobRef 가 바뀌지 않는다
        var seqs = JsonNode.Parse(wiA.Actions!)!.AsArray().Select(n => n!["params"]!["seqInGroup"]!.GetValue<int>()).ToArray();
        Assert.Equal(new[] { 1, 3 }, seqs);
        var run = await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        Assert.Equal((3, 1), (run.TotalTasks, run.ExcludedTasks));   // 분모 = 싣는 용접선(A 2 + B 1), 제외 1
        // 계획은 그대로 — 용접선은 지워지지 않는다
        Assert.Equal(4, await f.Db.AreaTasks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AreaWithAllWeldsDisabled_GetsNoStop()
    {
        var f = await Seed();
        await Disable(f.Db, f.B.Tasks[0].TaskId);

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");

        var items = await f.Db.WorkItems.AsNoTracking().Where(w => w.RunId == runId).ToListAsync();
        Assert.Equal(f.A.AreaId, Assert.Single(items).AreaId);       // B 는 정차 자체가 없다
        var run = await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        Assert.Equal((3, 1), (run.TotalTasks, run.ExcludedTasks));
    }

    [Fact]
    public async Task ReEnabledWeld_IsQueuedAgain()
    {
        var f = await Seed();
        var weld = f.A.Tasks[1];
        await Disable(f.Db, weld.TaskId);
        (await f.Db.AreaTasks.SingleAsync(t => t.TaskId == weld.TaskId)).Enabled = true;
        await f.Db.SaveChangesAsync();

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");

        var wiA = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId && w.AreaId == f.A.AreaId);
        Assert.Equal(3, TaskIdsOf(wiA).Count);
    }

    [Fact]
    public async Task Repair_SkipsDisabledWeld_AndRejectsWhenNothingIsLeft()
    {
        var f = await Seed();
        var off = f.A.Tasks[0];
        var on = f.A.Tasks[2];
        await Disable(f.Db, off.TaskId);

        var runId = (await f.Missions.StartRepairRunAsync("AMR-01", new[] { off.TaskId, on.TaskId }, optimize: false)).RunId;
        var wi = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        Assert.Equal(new[] { on.TaskId }, TaskIdsOf(wi));

        // 끝내고, 제외된 용접선만 지정하면 실을 것이 없어 거부(빈 run 을 남기지 않는다)
        (await f.Db.ScenarioRuns.SingleAsync(r => r.RunId == runId)).State = "ABORTED";
        await f.Db.SaveChangesAsync();
        var before = await f.Db.ScenarioRuns.CountAsync();
        await Assert.ThrowsAsync<RepairRequestException>(() =>
            f.Missions.StartRepairRunAsync("AMR-01", new[] { off.TaskId }, optimize: false));
        Assert.Equal(before, await f.Db.ScenarioRuns.CountAsync());
    }

    [Fact]
    public async Task Resume_RecalculatesOpenStop_WithoutDisabledWeld()
    {
        var f = await Seed();
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        var off = f.A.Tasks[1];
        await Disable(f.Db, off.TaskId);

        var run = await f.Db.ScenarioRuns.SingleAsync(r => r.RunId == runId);
        var items = await f.Db.WorkItems.Where(w => w.RunId == runId && w.Status == "PENDING").ToListAsync();
        await f.Dispatcher.RefreshOpenItemsAsync(run, items, CancellationToken.None);
        await f.Db.SaveChangesAsync();

        var wiA = items.Single(w => w.AreaId == f.A.AreaId);
        Assert.DoesNotContain(off.TaskId, TaskIdsOf(wiA));
        Assert.Equal(2, TaskIdsOf(wiA).Count);
        Assert.Equal(3, run.TotalTasks);                             // 4 → 3 (제외한 만큼 분모 보정)
    }
}
