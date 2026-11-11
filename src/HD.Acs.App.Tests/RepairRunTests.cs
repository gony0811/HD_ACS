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

/// <summary>리페어 run — 지정한 용접선(TASK)만 검사 큐에 싣는다. 영역에서 실패한 용접선을 골라 다시 돌릴 때 쓴다.</summary>
public class RepairRunTests
{
    private sealed record Fixture(AcsDbContext Db, InspectionDispatcher Dispatcher, MissionService Missions,
        Guid ScenarioId, InspectionAreaEntity A, InspectionAreaEntity B);

    /// <summary>F면 L1에 영역 2개(A: 용접선 3개 — 시나리오 대상 / B: 용접선 1개 — 시나리오 대상 아님).</summary>
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
        db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = sc.ScenarioId, AreaId = a.AreaId });
        // 연결된 로봇(층 미보고) — 배차는 보류되고 큐 상태만 본다
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var cfg = new ConfigurationBuilder().Build();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, cfg, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return new Fixture(db, dispatcher, missions, sc.ScenarioId, a, b);
    }

    private static List<Guid> TaskIdsOf(WorkItemEntity wi) =>
        JsonNode.Parse(wi.Actions!)!.AsArray().Select(n => Guid.Parse(n!["taskId"]!.GetValue<string>())).ToList();

    [Fact]
    public async Task Repair_QueuesOnlyTheChosenWeld()
    {
        var f = await Seed();
        var weld = f.A.Tasks.Single(t => t.Seq == 2);

        var runId = await f.Missions.StartRepairRunAsync("AMR-01", new[] { weld.TaskId });

        var run = await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        Assert.Equal(f.ScenarioId, run.ScenarioId);                 // 시나리오 지정 없음 → 선창의 시나리오
        Assert.Equal((1, 0), (run.TotalTasks, run.ExcludedTasks));  // 진행률 분모 = 지정한 용접선 수
        var wi = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        Assert.Equal(f.A.AreaId, wi.AreaId);
        Assert.Equal(new[] { weld.TaskId }, TaskIdsOf(wi));         // 영역의 다른 용접선 2개는 싣지 않음
        // 순번·정렬 그룹은 영역 안에서의 값 그대로(AMR 레시피·jobRef 유지)
        var p = JsonNode.Parse(wi.Actions!)![0]!["params"]!;
        Assert.Equal(2, p["seqInGroup"]!.GetValue<int>());
        Assert.Equal("CT1-L1-F-F-01", p["anchorGroupId"]!.GetValue<string>());

        var detail = await new RunQueryService(f.Db).GetRunAsync(runId);
        Assert.Equal(new[] { weld.TaskId }, detail!.RepairTaskIds);
        // 일반 run은 표시 없음
        Assert.Null((await new RunQueryService(f.Db).GetRunAsync(Guid.NewGuid()))?.RepairTaskIds);
    }

    [Fact]
    public async Task Repair_SeveralWelds_AcrossAreas_IgnoresScenarioTargets()
    {
        var f = await Seed();
        var ids = new[] { f.A.Tasks[0].TaskId, f.A.Tasks[2].TaskId, f.B.Tasks[0].TaskId };   // B는 시나리오 대상이 아님

        var runId = await f.Missions.StartRepairRunAsync("AMR-01", ids, f.ScenarioId);

        var items = await f.Db.WorkItems.AsNoTracking().Where(w => w.RunId == runId).ToListAsync();
        Assert.Equal(2, items.Count);                               // 영역마다 정차 1곳
        Assert.Equal(new[] { ids[0], ids[1] }, TaskIdsOf(items.Single(w => w.AreaId == f.A.AreaId)));
        Assert.Equal(new[] { ids[2] }, TaskIdsOf(items.Single(w => w.AreaId == f.B.AreaId)));
        Assert.Equal(3, (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).TotalTasks);
    }

    [Fact]
    public async Task NormalRun_StillQueuesWholeArea()
    {
        var f = await Seed();
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        var wi = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        Assert.Equal(3, TaskIdsOf(wi).Count);
        Assert.Null((await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).TaskIds);
    }

    [Fact]
    public async Task Repair_Rejects_BadRequests_WithoutCreatingRun()
    {
        var f = await Seed();
        await Assert.ThrowsAsync<RepairRequestException>(() => f.Missions.StartRepairRunAsync("AMR-01", Array.Empty<Guid>()));
        await Assert.ThrowsAsync<RepairRequestException>(() => f.Missions.StartRepairRunAsync("AMR-01", new[] { Guid.NewGuid() }));
        await Assert.ThrowsAsync<RepairRequestException>(() =>
            f.Missions.StartRepairRunAsync("AMR-01", new[] { f.A.Tasks[0].TaskId }, Guid.NewGuid()));   // 없는 시나리오

        // 층 캘리브레이션이 없으면 갈 곳이 없다 — 빈 run을 남기지 않는다
        f.Db.MapCalibrations.RemoveRange(f.Db.MapCalibrations);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RepairRequestException>(() => f.Missions.StartRepairRunAsync("AMR-01", new[] { f.A.Tasks[0].TaskId }));
        Assert.Empty(await f.Db.ScenarioRuns.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.WorkItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Repair_Rejected_WhileAnotherRunIsActive()
    {
        var f = await Seed();
        await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        await Assert.ThrowsAsync<RunConflictException>(() => f.Missions.StartRepairRunAsync("AMR-01", new[] { f.A.Tasks[0].TaskId }));
    }

    [Fact]
    public async Task Resume_OfRepairRun_KeepsOnlyTheChosenWeld()
    {
        var f = await Seed();
        var weld = f.A.Tasks[1];
        var runId = await f.Missions.StartRepairRunAsync("AMR-01", new[] { weld.TaskId });
        await f.Missions.AbortRunAsync(runId);

        await f.Missions.ResumeRunAsync(runId);   // 남은 정차를 현재 계획으로 다시 계산

        var wi = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        Assert.Equal(new[] { weld.TaskId }, TaskIdsOf(wi));
        Assert.Equal(1, (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).TotalTasks);
    }
}
