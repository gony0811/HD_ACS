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
/// 리프트 미사용(<c>Acs:Lift:Enabled = false</c>) — 지령의 liftStrokeMm 은 영역 저장값과 무관하게 0,
/// 시뮬레이터·최적화의 리프트 후보는 0 하나, 리프트 기둥은 간섭 판정에서 빠진다.
/// </summary>
public class LiftDisabledTests
{
    private static IConfiguration Config(bool? liftEnabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(liftEnabled is { } e
            ? new Dictionary<string, string?> { ["Acs:Lift:Enabled"] = e ? "true" : "false" }
            : new Dictionary<string, string?>())
        .Build();

    [Fact]
    public void 설정을_읽어_모델의_리프트를_끈다()
    {
        Assert.True(ReachSimService.LiftEnabledFrom(null));
        Assert.True(ReachSimService.LiftEnabledFrom(Config(null)));   // 설정 없음 = 종전(사용)

        var on = ReachSimService.ModelFrom(Config(true));
        Assert.True(on.LiftEnabled);
        Assert.Equal(1000, on.StrokeMaxMm);

        var off = ReachSimService.ModelFrom(Config(false));
        Assert.False(off.LiftEnabled);
        Assert.Equal(0, off.StrokeMaxMm);               // 조사·최적화 후보 = 리프트 0 하나
        Assert.Equal(0, off.EffectiveStrokeMm(600));
    }

    private static async Task<(AcsDbContext Db, MissionService Missions, Guid ScenarioId)> Seed(bool liftEnabled)
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
        var a = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.8, StationLiftMm = 600,
            Tasks =
            {
                new AreaTaskEntity
                {
                    TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.0, StartV = 1.64, EndU = 3.0, EndV = 2.0,
                    SectionDxfId = "DXF-1", ProfileId = "PROF-1",
                },
            },
        };
        db.InspectionAreas.Add(a);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, Config(liftEnabled), NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return (db, missions, sc.ScenarioId);
    }

    private static async Task<double> QueuedLift(bool liftEnabled)
    {
        var (db, missions, scenarioId) = await Seed(liftEnabled);
        var runId = await missions.StartRunAsync(scenarioId, "AMR-01");
        var wi = await db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        return JsonNode.Parse(wi.Actions!)![0]!["params"]!["liftStrokeMm"]!.GetValue<double>();
    }

    [Fact]
    public async Task 리프트를_끄면_지령의_리프트는_영역_저장값과_무관하게_0()
    {
        Assert.Equal(600, await QueuedLift(liftEnabled: true));    // 종전 — 영역 저장값
        Assert.Equal(0, await QueuedLift(liftEnabled: false));
    }
}
