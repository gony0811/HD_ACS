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
/// 가스검사 지령 — HD_AMR 은 같은 용접선 시퀀스에서 검사 센서만 가스 감지기로 바꾸므로 액션은 startWeldInspection 그대로이고,
/// 그 구역의 검사 횟수만 params.round 로 얹는다 [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.1].
/// </summary>
public class GasCommandTests
{
    private sealed record Fixture(AcsDbContext Db, MissionService Missions, Guid ScenarioId);

    private static async Task<Fixture> Seed(string kind)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "GT1", LevelZ = "[0,2.4,4.8,7.2]", InspectionKind = kind });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "GT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "GT1-L1", TankId = "GT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "GT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = 0 });
        db.ActionCatalog.Add(new ActionCatalogEntity
        {
            ActionType = "startWeldInspection", Scope = "NODE", BlockingType = "HARD",
            ParamSchema = ActionCatalogSeed.StartWeldInspectionParamSchema,
        });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "GT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.8,
            Tasks =
            {
                new AreaTaskEntity
                {
                    TaskId = Guid.NewGuid(), Seq = 1, SeamType = "LINE",
                    StartU = 3.0, StartV = 1.64, EndU = 3.0, EndV = 2.0, SectionDxfId = "DXF-1", ProfileId = "PROF-1",
                },
            },
        });
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "G", Version = 1, TankId = "GT1" };
        db.Scenarios.Add(sc);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return new Fixture(db, new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance),
            sc.ScenarioId);
    }

    private static async Task<JsonObject> FirstAction(AcsDbContext db, Guid runId) =>
        JsonNode.Parse((await db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId)).Actions!)![0]!.AsObject();

    /// <summary>지난 run 을 끝낸 것으로 만든다 — 그 구역의 정차를 주어진 상태로.</summary>
    private static async Task Finish(AcsDbContext db, Guid runId, string itemStatus)
    {
        var run = await db.ScenarioRuns.SingleAsync(r => r.RunId == runId);
        run.State = "COMPLETED";
        foreach (var w in await db.WorkItems.Where(w => w.RunId == runId).ToListAsync()) w.Status = itemStatus;
        await db.SaveChangesAsync();
    }

    /// <summary>가스검사 지령의 round = 그 구역(영역)을 검사하는 횟수 — 운영자가 고르지 않고 서버가 센다. 1·2 로 묶지 않는다.</summary>
    [Fact]
    public async Task GasTank_SendsAreaInspectionCountAsRound()
    {
        var f = await Seed("GAS");

        var run1 = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        var action = await FirstAction(f.Db, run1);
        Assert.Equal("startWeldInspection", action["actionType"]!.GetValue<string>());   // AMR 계약 — 액션은 용접검사와 같다
        Assert.Equal(1, action["params"]!["round"]!.GetValue<int>());
        Assert.Equal("LINE", action["params"]!["seamType"]!.GetValue<string>());          // 나머지 파라미터는 용접검사 그대로
        Assert.Equal(1, (await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == run1)).Round);
        Assert.Null((await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == run1)).Round);   // run 단위 회차는 쓰지 않는다
        await Finish(f.Db, run1, "DONE");

        // 구 UI 가 보내는 round 인자는 무시한다
        var run2 = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: 1);
        Assert.Equal(2, (await FirstAction(f.Db, run2))["params"]!["round"]!.GetValue<int>());
        await Finish(f.Db, run2, "SKIPPED");   // 실패로 건너뛴 것도 검사한 횟수다

        var run3 = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        Assert.Equal(3, (await FirstAction(f.Db, run3))["params"]!["round"]!.GetValue<int>());
        await Finish(f.Db, run3, "PENDING");   // 그 구역에 가지 못하고 끝난 run 은 세지 않는다

        var run4 = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        Assert.Equal(3, (await FirstAction(f.Db, run4))["params"]!["round"]!.GetValue<int>());
    }

    /// <summary>이어하기로 남은 정차를 다시 계산해도 그 정차의 횟수는 그대로다.</summary>
    [Fact]
    public async Task Refresh_KeepsRoundOfTheStop()
    {
        var f = await Seed("GAS");
        var run1 = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        await Finish(f.Db, run1, "DONE");
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        var run = await f.Db.ScenarioRuns.Include(r => r.Missions).SingleAsync(r => r.RunId == runId);
        var items = await f.Db.WorkItems.Where(w => w.RunId == runId).ToListAsync();
        foreach (var w in items) w.Status = "PENDING";
        var dispatcher = new InspectionDispatcher(f.Db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);

        await dispatcher.RefreshOpenItemsAsync(run, items, default);

        Assert.Equal(2, JsonNode.Parse(items[0].Actions!)![0]!["params"]!["round"]!.GetValue<int>());
    }

    [Fact]
    public async Task WeldTank_HasNoRound()
    {
        var f = await Seed("WELD");

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: 2);

        Assert.False((await FirstAction(f.Db, runId))["params"]!.AsObject().ContainsKey("round"));
        Assert.Null((await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId)).Round);
    }

    /// <summary>액션 카탈로그 스키마 — round 는 1 이상의 정수(검사 횟수라 상한이 없다).</summary>
    [Fact]
    public async Task ParamSchema_AllowsAnyPositiveRound()
    {
        var f = await Seed("GAS");
        var action = await FirstAction(f.Db, await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01"));
        JsonObject Payload(int round)
        {
            var prm = action["params"]!.DeepClone().AsObject();
            prm["round"] = round;
            return WeldInspectionPayload.BuildActionParameters(action["jobRef"]!.GetValue<string>(), action["position"]!.DeepClone().AsObject(), prm);
        }

        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, Payload(7)));
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, Payload(0)));
    }
}
