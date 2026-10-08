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

/// <summary>
/// 코봇 seam 시작점 이동 시험 [VDA §8 moveToSeamStart] — POST /api/robots/{id}/test/seam-start.
/// 순수 Order 빌더(계약 형태) + 가드(404/409). 실발행(_vda)·happy path는 실MQTT·시뮬레이터 E2E.
/// </summary>
public class SeamStartTestTests
{
    private static AcsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static InspectionDispatcher Dispatcher(AcsDbContext db) =>
        new(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);

    // ── 순수 빌더: 단일 노드 + moveToSeamStart 액션 (계약 형태) ──
    [Fact]
    public void BuildSeamStartTestOrder_SingleNode_WithMoveToSeamStartAction()
    {
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(12.5, 5.98, 1.42),
            ["seamEndW"] = new JsonArray(13.3, 5.98, 1.42),
            ["drawingPos"] = new JsonObject { ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "SM" },
        };
        var order = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L2", 12.482, 5.117, 1.571, position, "TEST-SEAM-x", 0.08, 0.07);

        Assert.Equal(0, order.OrderUpdateId);
        Assert.Empty(order.Edges);
        var node = Assert.Single(order.Nodes);
        Assert.Equal(0, node.SequenceId);
        Assert.True(node.Released);
        Assert.Equal("CT1-L2", node.NodePosition!.MapId);
        Assert.Equal(12.482, node.NodePosition.X);
        Assert.Equal(5.117, node.NodePosition.Y);
        Assert.Equal(1.571, node.NodePosition.Theta);
        Assert.Equal(0.08, node.NodePosition.AllowedDeviationXY);

        var act = Assert.Single(node.Actions);
        Assert.Equal("moveToSeamStart", act.ActionType);
        Assert.Equal("HARD", act.BlockingType);
        Assert.False(string.IsNullOrEmpty(act.ActionId));
        Assert.Contains(act.ActionParameters, p => p.Key == "jobRef");
        Assert.Contains(act.ActionParameters, p => p.Key == "position");
    }

    private static async Task<Guid> SeedAsync(AcsDbContext db, string reportedMapId, bool withRobot = true, bool runningRun = false)
    {
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = -0.0027 });
        if (withRobot)
        {
            db.Robots.Add(new RobotEntity { RobotId = "R1", Manufacturer = "HHI", SerialNumber = "AMR-01" });
            db.RobotContexts.Add(new RobotContextEntity { RobotId = "R1", ReportedMapId = reportedMapId });
        }
        if (runningRun)
            db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = Guid.NewGuid(), ScenarioId = Guid.NewGuid(), RobotId = "R1", State = "RUNNING" });
        var taskId = Guid.NewGuid();
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "SM", Level = 1, Name = "SM-01",
            Corners = "[[4.91,1.54],[5.11,1.54],[5.11,2.1],[4.91,2.1]]",
            Tasks = { new AreaTaskEntity { TaskId = taskId, Seq = 1, StartU = 5.01, StartV = 1.64, EndU = 5.01, EndV = 2.0, SectionDxfId = "DXF-1", ProfileId = "PROF-1" } },
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    [Fact]
    public async Task MissingRobot_ThrowsKeyNotFound()
    {
        using var db = NewDb();
        var taskId = await SeedAsync(db, "CT1-L1", withRobot: false);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Dispatcher(db).MoveToSeamStartAsync("R1", taskId, "t"));
    }

    [Fact]
    public async Task MissingTask_ThrowsKeyNotFound()
    {
        using var db = NewDb();
        await SeedAsync(db, "CT1-L1");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Dispatcher(db).MoveToSeamStartAsync("R1", Guid.NewGuid(), "t"));
    }

    [Fact]
    public async Task ActiveRun_ThrowsRunConflict()
    {
        using var db = NewDb();
        var taskId = await SeedAsync(db, "CT1-L1", runningRun: true);
        var ex = await Assert.ThrowsAsync<RunConflictException>(() => Dispatcher(db).MoveToSeamStartAsync("R1", taskId, "t"));
        Assert.Contains("진행 중", ex.Message);
    }

    [Fact]
    public async Task WrongFloor_ThrowsRunConflict()
    {
        using var db = NewDb();
        var taskId = await SeedAsync(db, "CT1-L2");   // 로봇은 L2, 작업은 L1
        var ex = await Assert.ThrowsAsync<RunConflictException>(() => Dispatcher(db).MoveToSeamStartAsync("R1", taskId, "t"));
        Assert.Contains("다른 층", ex.Message);
    }
}
