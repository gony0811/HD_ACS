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

    [Fact]
    public void 장착_높이와_나무판_두께를_설정에서_읽는다()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:MountZMm"] = "927", ["Acs:Reach:AmrFloorOffsetMm"] = "110",
        }).Build();
        var m = ReachSimService.ModelFrom(cfg);
        Assert.Equal(927, m.MountZMm);
        Assert.Equal(110, m.AmrFloorOffsetMm);

        var support = ReachSimService.ModelFrom(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:AmrHeightMm"] = "765", ["Acs:Reach:SupportColumn"] = "true",
        }).Build());
        Assert.Equal(765, support.AmrHeightMm);
        Assert.True(support.SupportColumn);
        Assert.Equal(HD.Acs.Core.Reach.ReachRobotModel.Default.TowerTopMm - 122, support.TowerTopMm, 9);   // 신호탑은 차체 윗면과 함께 내려간다

        var byLevel = ReachSimService.ModelFrom(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:AmrFloorOffsetMm:1"] = "25", ["Acs:Reach:AmrFloorOffsetMm:3"] = "900", ["Acs:Reach:AmrFloorOffsetMm:x"] = "10",
        }).Build());
        Assert.Equal(0, byLevel.AmrFloorOffsetMm);                       // 층별이면 공통 값은 0
        Assert.Equal(25, byLevel.AmrFloorOffsetByLevelMm![1]);
        Assert.Equal(0, byLevel.AmrFloorOffsetByLevelMm[3]);             // 범위 밖(0~500) = 0
        Assert.Equal(2, byLevel.AmrFloorOffsetByLevelMm.Count);          // 층 번호가 아닌 키는 버림
        Assert.Null(m.AmrFloorOffsetByLevelMm);                          // 숫자 하나 = 모든 층 공통

        var none = ReachSimService.ModelFrom(Config(null));      // 없으면 종전 1635·0
        Assert.Equal(1635, none.MountZMm);
        Assert.Equal(0, none.AmrFloorOffsetMm);
        Assert.Equal(1635, ReachSimService.MountZFrom(50, 1635));     // 범위 밖 = 기본값
        Assert.Equal(887, ReachSimService.AmrHeightFrom(50, 887));
        Assert.Equal(160, ReachSimService.ModelFrom(Config(null)).MaxEntryJointTravelDeg);        // 설정 없음 = 기본 160
        Assert.Equal(150, ReachSimService.ModelFrom(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Acs:Reach:MaxEntryJointTravelDeg"] = "150" }).Build()).MaxEntryJointTravelDeg);
        Assert.Equal(160, ReachSimService.EntryTravelFrom(10, 160));                              // 범위 밖 = 기본값
        Assert.Equal(765, ReachSimService.AmrHeightFrom(765, 887));
        Assert.Equal(0, ReachSimService.FloorOffsetFrom(-10));
        Assert.Equal(0, ReachSimService.FloorOffsetFrom(900));
    }

    private static async Task<(AcsDbContext Db, MissionService Missions, Guid ScenarioId)> Seed(IConfiguration cfg)
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
            hub: null!, cfg, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return (db, missions, sc.ScenarioId);
    }

    private static Task<double> QueuedLift(bool liftEnabled) => QueuedLift(Config(liftEnabled));

    private static async Task<double> QueuedLift(IConfiguration cfg)
    {
        var (db, missions, scenarioId) = await Seed(cfg);
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

    private static IConfiguration Lift(params (string Key, string Value)[] kv) => new ConfigurationBuilder()
        .AddInMemoryCollection(kv.ToDictionary(p => "Acs:Lift:" + p.Key, p => (string?)p.Value)).Build();

    [Fact]
    public void 리프트_모드를_읽는다_Mode_가_없으면_종전_Enabled()
    {
        Assert.Equal(LiftMode.Auto, LiftSettings.From(null).Mode);
        Assert.Equal(LiftMode.Auto, LiftSettings.From(Config(null)).Mode);
        Assert.Equal(LiftMode.Auto, LiftSettings.From(Config(true)).Mode);
        Assert.Equal(LiftMode.Off, LiftSettings.From(Config(false)).Mode);

        Assert.Equal(LiftMode.Off, LiftSettings.From(Lift(("Mode", "off"), ("Enabled", "true"))).Mode);   // Mode 가 우선, 대소문자 무시
        var f = LiftSettings.From(Lift(("Mode", "Fixed"), ("FixedStrokeMm", "350")));
        Assert.Equal((LiftMode.Fixed, 350.0, (string?)null), (f.Mode, f.FixedStrokeMm!.Value, f.Warning));
        Assert.True(f.Enabled);

        foreach (var bad in new[] { "1200", "-5", "abc" })   // 범위 밖·숫자 아님 → 0 mm 고정 + 경고
        {
            var b = LiftSettings.From(Lift(("Mode", "Fixed"), ("FixedStrokeMm", bad)));
            Assert.Equal(0, b.FixedStrokeMm);
            Assert.NotNull(b.Warning);
        }
        var missing = LiftSettings.From(Lift(("Mode", "Fixed")));
        Assert.Equal(0, missing.FixedStrokeMm);
        var unknown = LiftSettings.From(Lift(("Mode", "Manual")));
        Assert.Equal(LiftMode.Auto, unknown.Mode);
        Assert.NotNull(unknown.Warning);
    }

    [Fact]
    public void 고정_모드면_모델_후보와_판정_높이가_고정값_하나()
    {
        var m = ReachSimService.ModelFrom(Lift(("Mode", "Fixed"), ("FixedStrokeMm", "350")));
        Assert.True(m.LiftEnabled);
        Assert.Equal(350, m.FixedStrokeMm);
        Assert.Equal(new[] { 350.0 }, m.StrokeCandidates(100));
        Assert.Equal(350, m.EffectiveStrokeMm(0));       // 호출부가 다른 높이를 넘겨도 고정값
        Assert.Equal(350, m.LiftStrokeMm(800));         // 영역 저장값 무시
        Assert.Equal(350, m.LiftStrokeMm(null));

        var auto = ReachSimService.ModelFrom(Lift(("Mode", "Auto")));
        Assert.Equal(11, auto.StrokeCandidates(100).Count);
        Assert.Equal(800, auto.LiftStrokeMm(800));
        Assert.Equal(1000, auto.LiftStrokeMm(1500));
        var off = ReachSimService.ModelFrom(Lift(("Mode", "Off")));
        Assert.Equal(new[] { 0.0 }, off.StrokeCandidates(100));
        Assert.Equal(0, off.LiftStrokeMm(800));
    }

    [Fact]
    public async Task 고정_모드면_지령의_리프트는_영역_저장값과_무관하게_고정값()
    {
        Assert.Equal(350, await QueuedLift(Lift(("Mode", "Fixed"), ("FixedStrokeMm", "350"))));
        Assert.Equal(600, await QueuedLift(Lift(("Mode", "Auto"))));
        Assert.Equal(0, await QueuedLift(Lift(("Mode", "Off"))));
    }
}
