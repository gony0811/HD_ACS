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
/// run 의 검사 회차만 params.round(1|2)로 얹는다 [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.1].
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

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task GasTank_SendsWeldActionWithRound(int round)
    {
        var f = await Seed("GAS");

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: round);

        var action = await FirstAction(f.Db, runId);
        Assert.Equal("startWeldInspection", action["actionType"]!.GetValue<string>());   // AMR 계약 — 액션은 용접검사와 같다
        Assert.Equal(round, action["params"]!["round"]!.GetValue<int>());
        Assert.Equal("LINE", action["params"]!["seamType"]!.GetValue<string>());          // 나머지 파라미터는 용접검사 그대로
        Assert.Equal(round, (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).Round);
        Assert.Equal(round, (await new RunQueryService(f.Db).GetRunAsync(runId))!.Round);
    }

    [Fact]
    public async Task GasTank_WithoutRound_OmitsTheField()
    {
        var f = await Seed("GAS");

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");

        Assert.False((await FirstAction(f.Db, runId))["params"]!.AsObject().ContainsKey("round"));   // AMR: 없으면 결과에서 생략
    }

    [Fact]
    public async Task WeldTank_IgnoresRound()
    {
        var f = await Seed("WELD");

        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: 2);

        Assert.False((await FirstAction(f.Db, runId))["params"]!.AsObject().ContainsKey("round"));
        Assert.Null((await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).Round);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public async Task RoundOutsideOneOrTwo_IsRejected_BeforeAnyRun(int round)
    {
        var f = await Seed("GAS");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: round));

        Assert.Empty(await f.Db.ScenarioRuns.ToListAsync());
    }

    /// <summary>액션 카탈로그 스키마도 round 를 1·2 로 묶는다 — 다른 경로로 잘못된 값이 실려도 발행 전 검증에서 걸린다.</summary>
    [Fact]
    public async Task ParamSchema_AllowsOnlyRoundOneOrTwo()
    {
        var f = await Seed("GAS");
        var action = await FirstAction(f.Db, await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01", round: 1));
        JsonObject Payload(int round)
        {
            var prm = action["params"]!.DeepClone().AsObject();
            prm["round"] = round;
            return WeldInspectionPayload.BuildActionParameters(action["jobRef"]!.GetValue<string>(), action["position"]!.DeepClone().AsObject(), prm);
        }

        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, Payload(2)));
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, Payload(3)));
    }
}
