using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Integration;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
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
        // 리프트 미지정 → 본검사와 같은 기본 0
        Assert.Equal(0.0, LiftOf(act));
        // taskId 미지정 → params 에 싣지 않는다
        Assert.Null(ParamsOf(act)["taskId"]);
    }

    // 시험 Order 에도 대상 용접선 taskId 를 params.taskId 로 싣는다 — 본검사와 같은 표기(Guid.ToString), 스키마 통과.
    [Fact]
    public void BuildSeamStartTestOrder_CarriesTaskId_AndPassesSchema()
    {
        var taskId = Guid.Parse("8e9b34ae-0625-5779-9c1d-2f0a6b3e4d51");
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(12.5, 5.98, 1.42),
            ["seamEndW"] = new JsonArray(13.3, 5.98, 1.42),
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "SM",
                ["u"] = 5.0, ["v"] = 1.6, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0,
            },
        };
        var order = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L2", 12.482, 5.117, 1.571, position,
            $"TEST-SEAM-{taskId}", 0.08, 0.07, 300, taskId);
        var act = Assert.Single(Assert.Single(order.Nodes).Actions);
        Assert.Equal("8e9b34ae-0625-5779-9c1d-2f0a6b3e4d51", ParamsOf(act)["taskId"]!.GetValue<string>());
        Assert.Equal(300.0, LiftOf(act));

        var asObject = new JsonObject();
        foreach (var p in act.ActionParameters)
            asObject[p.Key] = JsonNode.Parse(JsonSerializer.Serialize(p.Value));
        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));

        // 문자열이 아닌 taskId 는 스키마가 거부(format: uuid 는 검증기 기본값상 주석 — 본검사 taskId 와 같은 조건;
        // uuid 형식 검사는 AMR·시뮬레이터 파서가 한다)
        asObject["params"] = new JsonObject { ["liftStrokeMm"] = 0, ["taskId"] = 123 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));
    }

    private static JsonObject ParamsOf(VdaAction act) =>
        JsonNode.Parse(JsonSerializer.Serialize(act.ActionParameters.Single(p => p.Key == "params").Value))!.AsObject();

    // 시험 Order 에도 영역 리프트 높이를 params.liftStrokeMm 으로 싣고, 카탈로그 param_schema 를 통과한다.
    [Fact]
    public void BuildSeamStartTestOrder_CarriesLiftStroke_AndPassesSchema()
    {
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(12.5, 5.98, 1.42),
            ["seamEndW"] = new JsonArray(13.3, 5.98, 1.42),
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "SM",
                ["u"] = 5.0, ["v"] = 1.6, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0,
            },
        };
        var order = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L2", 12.482, 5.117, 1.571, position, "TEST-SEAM-x", 0.08, 0.07, 600);
        var act = Assert.Single(Assert.Single(order.Nodes).Actions);
        Assert.Equal(600.0, LiftOf(act));

        var asObject = new JsonObject();
        foreach (var p in act.ActionParameters)
            asObject[p.Key] = JsonNode.Parse(JsonSerializer.Serialize(p.Value));
        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));

        // 범위 밖 리프트는 스키마가 거부(0~1000 mm)
        asObject["params"] = new JsonObject { ["liftStrokeMm"] = 1200 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));
    }

    private static double LiftOf(VdaAction act)
    {
        var prm = act.ActionParameters.Single(p => p.Key == "params").Value;
        return JsonNode.Parse(JsonSerializer.Serialize(prm))!["liftStrokeMm"]!.GetValue<double>();
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

    // 시험도 본검사와 같은 taskId 별 누적 attempt 를 받는다 — 앞선 본검사 1회 뒤 시험은 2, 그 뒤 본검사는 3.
    [Fact]
    public async Task SeamStartTest_IssuesCumulativeAttempt_AndNextInspectionGoesHigher()
    {
        using var db = NewDb();
        var taskId = await SeedAsync(db, "CT1-L1");
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        db.OrderActions.Add(new OrderActionEntity { ActionId = Guid.NewGuid(), MissionId = Guid.NewGuid(), TaskId = taskId, ActionType = "startWeldInspection" });
        await db.SaveChangesAsync();

        // MQTT 미연결이라 발행은 실패하지만, 발급 기록은 발행 전에 저장된다(번호는 건너뛸 뿐 겹치지 않는다)
        await Assert.ThrowsAnyAsync<Exception>(() => Dispatcher(db).MoveToSeamStartAsync("R1", taskId, "t"));
        var test = Assert.Single(await db.SeamTests.ToListAsync());
        Assert.Equal((taskId, 2, "R1"), (test.TaskId, test.Attempt, test.RobotId));
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.Action == "TEST_SEAM_START" && a.Target.Contains("attempt 2"));

        var issued = await InspectionDispatcher.IssuedAttemptsAsync(db, new[] { taskId });
        Assert.Equal(3, TaskAttempt.Next(issued[taskId]));   // 다음 본검사 — 시험 촬영보다 큰 번호(세이지에서 최신)
    }

    [Fact]
    public void BuildSeamStartTestOrder_CarriesAttempt_AndSchemaLimits()
    {
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(12.5, 5.98, 1.42), ["seamEndW"] = new JsonArray(13.3, 5.98, 1.42),
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "SM",
                ["u"] = 5.0, ["v"] = 1.6, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0,
            },
        };
        var order = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L2", 1, 2, 0, position, "TEST-SEAM-x", 0.08, 0.07, 0, Guid.NewGuid(), 4);
        var act = Assert.Single(Assert.Single(order.Nodes).Actions);
        Assert.Equal(4, ParamsOf(act)["attempt"]!.GetValue<int>());

        var asObject = new JsonObject();
        foreach (var p in act.ActionParameters) asObject[p.Key] = JsonNode.Parse(JsonSerializer.Serialize(p.Value));
        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));
        asObject["params"] = new JsonObject { ["attempt"] = 0 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));
        asObject["params"] = new JsonObject { ["attempt"] = 256 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema, asObject));
        // attempt 미지정이면 싣지 않는다
        var none = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L2", 1, 2, 0, position, "TEST-SEAM-x", 0.08, 0.07);
        Assert.Null(ParamsOf(Assert.Single(Assert.Single(none.Nodes).Actions))["attempt"]);
    }
}
