using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 검사 종류별 리치 설정 — 가스검사 AMR 은 툴을 플랜지에 180° 돌려 달아 대기(홈) J6 가 0° 이고 J6 안전 범위가 −140~40 이다
/// (HD_AMR inspection.safeJ6StandbyDeg). 선창의 검사 종류로 설정 묶음(<c>Acs:Reach:Kinds:{종류}</c>)과 순회 프로필을 보낸 AMR 을 고른다.
/// </summary>
public class ReachKindConfigTests
{
    private static IConfiguration Cfg(params (string K, string V)[] kv) => new ConfigurationBuilder()
        .AddInMemoryCollection(kv.ToDictionary(x => x.K, x => (string?)x.V)).Build();

    private static readonly (string, string)[] Site =
    {
        ("Acs:Reach:ToolZMm", "365"), ("Acs:Reach:SafeJ6MinDeg", "-320"), ("Acs:Reach:SafeJ6MaxDeg", "-140"),
        ("Acs:Reach:StartJoints:T:0", "0"), ("Acs:Reach:StartJoints:T:1", "59"), ("Acs:Reach:StartJoints:T:2", "-141"),
        ("Acs:Reach:StartJoints:T:3", "81"), ("Acs:Reach:StartJoints:T:4", "90"), ("Acs:Reach:StartJoints:T:5", "-179.7"),
        ("Acs:Reach:StartJoints:F:0", "0"), ("Acs:Reach:StartJoints:F:1", "-9"), ("Acs:Reach:StartJoints:F:2", "120"),
        ("Acs:Reach:StartJoints:F:3", "-111"), ("Acs:Reach:StartJoints:F:4", "90"), ("Acs:Reach:StartJoints:F:5", "180.4"),
        ("Acs:Reach:ProfileSource", "AMR-01"),
        ("Acs:Reach:Kinds:GAS:SafeJ6StandbyDeg", "0"), ("Acs:Reach:Kinds:GAS:SafeJ6MinDeg", "-140"), ("Acs:Reach:Kinds:GAS:SafeJ6MaxDeg", "40"),
        ("Acs:Reach:Kinds:GAS:ToolMountRollDeg", "180"),
        ("Acs:Reach:Kinds:GAS:StartJoints:T:0", "0"), ("Acs:Reach:Kinds:GAS:StartJoints:T:1", "59"), ("Acs:Reach:Kinds:GAS:StartJoints:T:2", "-141"),
        ("Acs:Reach:Kinds:GAS:StartJoints:T:3", "81"), ("Acs:Reach:Kinds:GAS:StartJoints:T:4", "90"), ("Acs:Reach:Kinds:GAS:StartJoints:T:5", "0.3"),
        ("Acs:Lift:Enabled", "false"),
    };

    [Theory]
    [InlineData(-140.0, 40.0, 0.0, -140.0, 40.0, true)]       // 가스검사 — 대기 0° 를 포함
    [InlineData(-140.0, 40.0, null, -320.0, -140.0, false)]   // 대기 자세를 안 주면 −180° 기준 → 기본값으로
    [InlineData(-320.0, -140.0, null, -320.0, -140.0, true)]  // 용접검사(종전)
    [InlineData(-320.0, -140.0, 0.0, -320.0, -140.0, false)]  // 대기 0° 가 범위 밖
    public void J6_안전_범위는_대기_자세를_포함해야_한다(double min, double max, double? standby, double lo, double hi, bool ok) =>
        Assert.Equal((lo, hi, ok), ReachRobotModel.ResolveSafeJ6Range(min, max, standby));

    [Fact]
    public void 가스검사_선창은_종류별_설정을_쓰고_나머지는_기본값을_물려받는다()
    {
        var cfg = Cfg(Site);
        var weld = ReachSimService.ModelFrom(cfg, InspectionKinds.Weld);
        var gas = ReachSimService.ModelFrom(cfg, InspectionKinds.Gas);

        Assert.Equal((-320.0, -140.0, -180.0, 180.0), (weld.SafeJ6MinDeg, weld.SafeJ6MaxDeg, weld.SafeJ6StandbyDeg, weld.OperationJ6Deg!.Value));
        Assert.Equal((-140.0, 40.0, 0.0, 0.0), (gas.SafeJ6MinDeg, gas.SafeJ6MaxDeg, gas.SafeJ6StandbyDeg, gas.OperationJ6Deg!.Value));   // 운영 J6 = 대기 자세
        Assert.Equal(180, gas.ToolMountRollDeg);
        Assert.Equal(365, gas.ToolZMm);                 // 종류 묶음에 없는 값은 기본 설정 그대로
        Assert.False(gas.LiftEnabled);                  // Acs:Lift 도 그대로
        // StartJoints 는 통째로 바뀐다 — 가스 묶음에 없는 F 면을 용접검사 값(J6 180°)으로 섞지 않는다
        Assert.Equal(0.3, gas.StartJointsByWall["T"][5]);
        Assert.False(gas.StartJointsByWall.ContainsKey("F"));
        Assert.Equal(180.4, weld.StartJointsByWall["F"][5]);

        // 종류 없음·모르는 값·묶음 없는 종류는 기본 설정 그대로(종전 동작)
        Assert.Equal(weld.SafeJ6MaxDeg, ReachSimService.ModelFrom(cfg).SafeJ6MaxDeg);
        Assert.Equal(weld.SafeJ6MaxDeg, ReachSimService.ModelFrom(cfg, "XRAY").SafeJ6MaxDeg);
        Assert.Equal(weld.SafeJ6MaxDeg, ReachSimService.ModelFrom(Cfg(("Acs:Reach:SafeJ6MaxDeg", "-140")), InspectionKinds.Gas).SafeJ6MaxDeg);
    }

    [Fact]
    public void 대기_자세를_빼먹은_가스검사_범위는_기본값으로_돌아간다()
    {
        var m = ReachSimService.ModelFrom(Cfg(("Acs:Reach:Kinds:GAS:SafeJ6MinDeg", "-140"), ("Acs:Reach:Kinds:GAS:SafeJ6MaxDeg", "40")), InspectionKinds.Gas);
        Assert.Equal((-320.0, -140.0, -180.0, 180.0), (m.SafeJ6MinDeg, m.SafeJ6MaxDeg, m.SafeJ6StandbyDeg, m.OperationJ6Deg!.Value));
    }

    [Fact]
    public void 프로필_source_는_종류별_지정을_따르고_없으면_다른_종류의_AMR_을_뺀다()
    {
        var cfg = Cfg(Site);
        Assert.Equal("AMR-01", ReachSimService.ProfileSourceFor(cfg, InspectionKinds.Weld));
        Assert.Equal("AMR-01", ReachSimService.ProfileSourceFor(cfg, null));
        Assert.Null(ReachSimService.ProfileSourceFor(cfg, InspectionKinds.Gas));
        Assert.Equal(new[] { "AMR-01" }, ReachSimService.OtherProfileSources(cfg, InspectionKinds.Gas));
        Assert.Empty(ReachSimService.OtherProfileSources(cfg, InspectionKinds.Weld));

        var both = Cfg(("Acs:Reach:ProfileSource", "AMR-01"), ("Acs:Reach:Kinds:GAS:ProfileSource", "GAS-01"));
        Assert.Equal("GAS-01", ReachSimService.ProfileSourceFor(both, InspectionKinds.Gas));
        Assert.Equal(InspectionKinds.Gas, ReachSimService.KindForSource(both, "gas-01"));
        Assert.Null(ReachSimService.KindForSource(both, "AMR-01"));   // 기본 설정(용접검사)으로 대조
    }

    // ── 시뮬레이터 연결: 두 AMR 이 프로필을 번갈아 보내도 선창의 검사 종류에 맞는 AMR 의 지정표로 판정한다 ──

    private static ProfileWaypoint P(double x, double theta, double z, double y = 0, double rz = 0) => new(x, y, z, theta, rz, 0, 0);

    private static async Task<AcsDbContext> SeedTank(string kind)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]", InspectionKind = kind });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.6,
            Tasks =
            {
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.0, StartV = 1.8, EndU = 3.36, EndV = 1.8, SeamType = "LINE", SectionDxfId = "D", ProfileId = "P" },
            },
        });
        await db.SaveChangesAsync();
        // 용접검사 AMR 은 닿지 않는 경유점(옆으로 3 m)이 든 프로필을, 가스검사 AMR 은 정상 프로필을 지정했다. 가스검사 AMR 이 나중에 보냈다.
        await InspectionProfileService.SyncAsync(db, new ProfileSyncRequest("AMR-01", true,
            new[] { new ProfileUpload(18, "LINE-FAR", "LINE", true, null, new[] { P(0, 0, 33.8), P(360, 0, 33.8), P(205, 0, 29.8, y: 3000, rz: -90) }) },
            new[] { new ProfileAssignment("LINE", "F", "H", 18) }));
        await Task.Delay(5);
        await InspectionProfileService.SyncAsync(db, new ProfileSyncRequest("AMR-07", true,
            new[] { new ProfileUpload(18, "LINE-OK", "LINE", true, null, new[] { P(0, 0, 33.8), P(180, 0, 0), P(360, 0, 33.8) }) },
            new[] { new ProfileAssignment("LINE", "F", "H", 18) }));
        return db;
    }

    private static ReachSimService Sim(AcsDbContext db)
    {
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return new ReachSimService(db, dispatcher, Cfg(("Acs:Reach:MountYawDeg", "180"), ("Acs:Reach:ToolZMm", "365"),
            ("Acs:Reach:ScanStandoffMm", "43.8"), ("Acs:Reach:FocusStandoffMm", "10"), ("Acs:Reach:ProfileSource", "AMR-01")));
    }

    [Fact]
    public async Task 용접검사_선창은_나중에_받은_다른_AMR_의_지정표를_쓰지_않는다()
    {
        var db = await SeedTank(InspectionKinds.Weld);
        var seam = (await Sim(db).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.Single();
        Assert.Equal("Unreachable", seam.Verdict);       // AMR-01 의 LINE-FAR — 경유점 #3 에서 막힌다
        Assert.Contains("경유점 #3", seam.Blocker);
    }

    [Fact]
    public async Task 가스검사_선창은_용접검사_AMR_을_뺀_AMR_의_지정표를_쓴다()
    {
        var db = await SeedTank(InspectionKinds.Gas);
        var seam = (await Sim(db).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.Single();
        Assert.DoesNotContain("경유점 #3", seam.Blocker ?? "");   // AMR-07 의 LINE-OK
    }
}
