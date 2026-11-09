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
/// 리치·간섭 시뮬레이션 조회 — 배차와 같은 정차점에서 CT1 경사면·수직벽 용접선을 판정한다(분석 2026-11-01 과 같은 방향).
/// </summary>
public class ReachSimServiceTests
{
    private static readonly Guid SlArea = Guid.Parse("5e000000-0000-4000-8000-000000000001");
    private static readonly Guid SlTask = Guid.Parse("5e000000-0000-4000-8000-000000000002");
    private static readonly Guid SmArea = Guid.Parse("5e000000-0000-4000-8000-000000000003");
    private static readonly Guid SmTask = Guid.Parse("5e000000-0000-4000-8000-000000000004");
    private static readonly Guid TArea = Guid.Parse("5e000000-0000-4000-8000-000000000005");
    private static readonly Guid TTask1 = Guid.Parse("5e000000-0000-4000-8000-000000000006");
    private static readonly Guid TTask2 = Guid.Parse("5e000000-0000-4000-8000-000000000007");

    /// <param name="traverse">도면 순회 판정을 켤지 — 기존 테스트는 세 자세·진입·리프트 규칙만 보므로 끈다(순회는 Core ReachTraversalTests).</param>
    private static async Task<ReachSimService> SeedAsync(Dictionary<string, double[]>? startJoints = null, bool traverse = false)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976, new[] { 0.0, 3.63, 6.63, 10.53 });
        db.TankGeometries.Add(new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 31.785, WFloor = 10.1497, ThetaLow = Math.PI / 4, HLow = 3.9776, HWall = 6.9097,
            ThetaUp = Math.PI / 4, HUp = 2.8976, LevelZ = "[0,3.63,6.63,10.53]",
        });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        // SL 2행(중심 높이 ≈ 1.45 m) — 기본 0.8 m 이격 정차는 경사면 끝단 안쪽이다.
        var v = 1.45 / Math.Sin(Math.PI / 4);
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = SlArea, TankId = "CT1", WallCode = "SL", Level = 1, Name = "L1-SL-R02",
            Corners = JsonSerializer.Serialize(new[] { new[] { 9.3, v - 0.6 }, new[] { 10.7, v - 0.6 }, new[] { 10.7, v + 0.6 }, new[] { 9.3, v + 0.6 } }),
            Tasks = { new AreaTaskEntity { TaskId = SlTask, Seq = 1, StartU = 9.5, StartV = v, EndU = 10.5, EndV = v } },
        });
        // L2 수직벽 SM — 데크(3.63) 위 1.5 m. SM 면 v 는 3.9776 부터.
        var vm = 3.63 + 1.5 - 3.9776;
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = SmArea, TankId = "CT1", WallCode = "SM", Level = 2, Name = "L2-SM-R01",
            Corners = JsonSerializer.Serialize(new[] { new[] { 11.3, vm - 0.6 }, new[] { 12.7, vm - 0.6 }, new[] { 12.7, vm + 0.6 }, new[] { 11.3, vm + 0.6 } }),
            Tasks = { new AreaTaskEntity { TaskId = SmTask, Seq = 1, StartU = 11.8, StartV = vm, EndU = 12.2, EndV = vm } },
        });
        // L4 천장 T — 정면 방향(FacingYaw)이 없는 수평면.
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = TArea, TankId = "CT1", WallCode = "T", Level = 4, Name = "L4-T-R01",
            Corners = JsonSerializer.Serialize(new[] { new[] { 14.4, 1.4 }, new[] { 15.6, 1.4 }, new[] { 15.6, 2.6 }, new[] { 14.4, 2.6 } }),
            Tasks =
            {
                new AreaTaskEntity { TaskId = TTask1, Seq = 1, StartU = 14.7, StartV = 1.8, EndU = 15.3, EndV = 1.8 },
                new AreaTaskEntity { TaskId = TTask2, Seq = 2, StartU = 14.7, StartV = 2.2, EndU = 15.3, EndV = 2.2 },
            },
        });
        await db.SaveChangesAsync();
        // 조회는 정차 기본 이격만 쓴다 — 허브(SignalR)는 닿지 않는다.
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var entries = (startJoints ?? new Dictionary<string, double[]>()).SelectMany(kv =>
                kv.Value.Select((j, i) => new KeyValuePair<string, string?>($"Acs:Reach:StartJoints:{kv.Key}:{i}", j.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Append(new KeyValuePair<string, string?>("Acs:Reach:CheckTraversal", traverse ? "true" : "false"));
        return new ReachSimService(db, dispatcher, new ConfigurationBuilder().AddInMemoryCollection(entries).Build());
    }

    [Fact]
    public async Task 조사는_경사면_정차의_차체_간섭과_수직벽_도달을_가른다()
    {
        var sim = await SeedAsync();
        var areas = (await sim.SurveyAsync("CT1", null, null, default))!;
        var alt = (await sim.SurveyAsync("CT1", null, null, default, ReachBranchRule.MaxClearance))!;

        var sl = areas.Single(a => a.AreaId == SlArea).Seams.Single();
        Assert.NotEqual("Clear", sl.Verdict);
        // 수직벽 한가운데 — 실측 관절 한계에서는 운영 가지도, 간섭 여유 최대 가지도 간섭 없이 닿는다.
        Assert.Equal("Clear", areas.Single(a => a.AreaId == SmArea).Seams.Single().Verdict);
        var sm = alt.Single(a => a.AreaId == SmArea).Seams.Single();
        Assert.Equal("Clear", sm.Verdict);
        Assert.NotNull(sm.StrokeMm);
        Assert.InRange(sm.ZMin, 1.49, 1.51);   // 층 바닥 기준 높이
    }

    [Fact]
    public async Task 조사는_층과_면으로_거른다()
    {
        var sim = await SeedAsync();
        var areas = (await sim.SurveyAsync("CT1", 2, "SM", default))!;
        Assert.Equal(SmArea, Assert.Single(areas).AreaId);
        Assert.Null(await sim.SurveyAsync("NOPE", null, null, default));
    }

    [Fact]
    public async Task 리프트_영역_고정_조사는_고정_높이와_용접선별_최적을_싣는다()
    {
        var sim = await SeedAsync();
        var plain = Assert.Single((await sim.SurveyAsync("CT1", 2, "SM", default))!);
        var fixedArea = Assert.Single((await sim.SurveyAsync("CT1", 2, "SM", default, areaLift: true))!);

        Assert.Null(plain.FixedStrokeMm);
        Assert.NotNull(fixedArea.FixedStrokeMm);
        // 용접선별 최적은 종전 조사와 같다.
        Assert.Equal(plain.Seams.Select(s => s.Verdict), fixedArea.Seams.Select(s => s.BestVerdict));
        Assert.Equal(plain.Seams.Select(s => s.StrokeMm), fixedArea.Seams.Select(s => s.BestStrokeMm));
        Assert.Equal(plain.Seams.Count(s => s.Verdict == "Clear"), fixedArea.BestClear);
        Assert.Equal(plain.Seams.Count(s => s.Verdict != "Unreachable"), fixedArea.BestReachable);
        // 닿는 용접선은 모두 고정 높이 하나로 판정했다.
        Assert.All(fixedArea.Seams.Where(s => s.Verdict != "Unreachable"), s => Assert.Equal(fixedArea.FixedStrokeMm, s.StrokeMm));
    }

    [Fact]
    public async Task 상세는_세_자세와_차체_간섭을_돌려주고_JSON_으로_나간다()
    {
        var sim = await SeedAsync();
        var d = (await sim.DetailAsync(SlArea, SlTask, 1, 700, 0, 0, 0, default))!;

        Assert.True(d.Body.ClearanceM < -0.3);
        Assert.Equal("SL", d.Body.NearestPlane);
        Assert.Equal(700, d.StrokeMm);
        Assert.Equal(new[] { 400.0, 300, 0 }, d.Stages.Select(s => s.StandoffMm));
        var json = JsonSerializer.Serialize(d, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"stages\"", json);
    }

    [Fact]
    public async Task 상세는_리프트를_주지_않으면_조사로_고른다()
    {
        var sim = await SeedAsync();
        var d = (await sim.DetailAsync(SmArea, SmTask, 1, null, 0, 0, 0, default))!;
        var survey = (await sim.SurveyAsync("CT1", 2, "SM", default))!.Single().Seams.Single();

        Assert.Equal(survey.StrokeMm, d.StrokeMm);
        Assert.All(d.Stages, s => Assert.True(s.ChosenIndex >= 0));
        Assert.Null(await sim.DetailAsync(SmArea, SlTask, 1, null, 0, 0, 0, default));
    }

    [Fact]
    public async Task 최적화는_경사면_정차를_옮기고_수직벽은_닿는_수를_줄이지_않는다()
    {
        var sim = await SeedAsync();
        var list = (await sim.OptimizeAsync("CT1", null, null, null, default, ReachBranchRule.MaxClearance))!;

        var sl = list.Single(o => o.WallCode == "SL");
        Assert.Equal(0, sl.Current.Clear);
        Assert.False(sl.Current.Feasible);
        Assert.Equal(1, sl.Best.Clear);
        Assert.True(sl.Best.DistM > sl.Current.DistM);
        Assert.False(sl.Overridden);
        var seam = Assert.Single(sl.Seams);
        Assert.NotEqual("Clear", seam.Current);   // 차체가 경사면에 묻힌 정차 — 간섭이거나(점마다 가지를 따로 고르던 종전) 이어 갈 가지 없음
        Assert.Equal("Clear", seam.Best);
        Assert.NotNull(seam.BestStrokeMm);

        var sm = list.Single(o => o.WallCode == "SM");
        Assert.Equal(1, sm.Current.Clear);
        Assert.Equal(1, sm.Best.Clear);
        Assert.InRange(sm.Current.DistM, 1.19, 1.21);   // 수직벽 기본 1.2 m

        var t = list.Single(o => o.WallCode == "T");
        Assert.Equal(2, t.Best.Clear);
        Assert.NotNull(t.Best.StrokeMm);
        Assert.All(t.Seams, s => Assert.Equal(t.Best.StrokeMm, s.BestStrokeMm));

        Assert.Single((await sim.OptimizeAsync("CT1", 1, "SL", null, default))!);
        Assert.Single((await sim.OptimizeAsync("CT1", 4, "T", null, default))!);
        Assert.Null(await sim.OptimizeAsync("NOPE", null, null, null, default));
    }
    [Fact]
    public void 설정에서_장착_yaw와_면별_출발_관절각을_읽는다()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:MountYawDeg"] = "180", ["Acs:Reach:ToolZMm"] = "365", ["Acs:Reach:ApproachStandoffMm"] = "200",
            ["Acs:Reach:StartJoints:PU:0"] = "1", ["Acs:Reach:StartJoints:PU:1"] = "2", ["Acs:Reach:StartJoints:PU:2"] = "-3",
            ["Acs:Reach:StartJoints:PU:3"] = "4", ["Acs:Reach:StartJoints:PU:4"] = "5", ["Acs:Reach:StartJoints:PU:5"] = "6",
            ["Acs:Reach:StartJoints:T:0"] = "1",   // 6개가 아니면 버린다
        }).Build();

        var m = ReachSimService.ModelFrom(config);

        Assert.Equal(180, m.MountYawDeg);
        Assert.Equal(365, m.ToolZMm);
        Assert.Equal(new[] { 200.0, 300, 0 }, m.StageStandoffsMm);
        Assert.Equal(new[] { 1.0, 2, -3, 4, 5, 6 }, ReachSimulator.StartJoints(m, "PU"));
        Assert.Equal(new[] { 1.0, 2, -3, 4, 5, 6 }, ReachSimulator.StartJoints(m, "pu"));
        Assert.Null(ReachSimulator.StartJoints(m, "T"));
        Assert.Equal(0, ReachSimService.ModelFrom(null).MountYawDeg);
        Assert.Equal(180, m.OperationJ6Deg);   // 없으면 운영 기본 180°
        Assert.Equal(0, m.ToolMountRollDeg);
        // 도면 순회 — 없으면 켜짐·이격 = ② 접근 이격(점1 Top = ② 접근점)·±40° 포괄 근사·여유 목표 10°
        Assert.True(m.CheckTraversal);
        Assert.Equal(200, m.ScanStandoffMm);
        Assert.Equal(10, m.TraverseMarginTargetDeg);
        Assert.Equal(40, m.ScanTiltDeg);
        Assert.Null(m.ScanTiltProfile);
    }

    [Fact]
    public void 도면_순회_설정을_읽는다()
    {
        var m = ReachSimService.ModelFrom(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:CheckTraversal"] = "false", ["Acs:Reach:ScanStandoffMm"] = "417", ["Acs:Reach:ScanTiltDeg"] = "30",
            ["Acs:Reach:TraverseMarginTargetDeg"] = "12",
            ["Acs:Reach:ScanTiltProfile:3:0"] = "200", ["Acs:Reach:ScanTiltProfile:3:1"] = "-45", ["Acs:Reach:ScanTiltProfile:3:2"] = "2.9",
            ["Acs:Reach:ScanTiltProfile:0:0"] = "0", ["Acs:Reach:ScanTiltProfile:0:1"] = "0",
            ["Acs:Reach:ScanTiltProfile:1:0"] = "180", ["Acs:Reach:ScanTiltProfile:1:1"] = "-39",
            ["Acs:Reach:ScanTiltProfile:2:0"] = "1",   // 2개가 아니면 버린다
        }).Build());

        Assert.False(m.CheckTraversal);
        Assert.Equal(417, m.ScanStandoffMm);
        Assert.Equal(30, m.ScanTiltDeg);
        Assert.Equal(3, m.ScanTiltProfile!.Count);   // [거리, 기울기] 또는 [거리, 기울기, Z]
        Assert.Equal(new[] { 180.0, -39 }, m.ScanTiltProfile[1]);
        Assert.Equal(new[] { 200.0, -45, 2.9 }, m.ScanTiltProfile[2]);
        Assert.Equal(12, m.TraverseMarginTargetDeg);
    }

    [Fact]
    public async Task 상세는_도면_순회_판정을_싣는다()
    {
        var off = (await (await SeedAsync()).DetailAsync(SmArea, SmTask, 1, null, 0, 0, 0, default))!;
        Assert.Null(off.Traversal);

        var sim = await SeedAsync(traverse: true);
        var d = (await sim.DetailAsync(SmArea, SmTask, 1, 500, 0, 0, 0, default))!;
        Assert.NotNull(d.Traversal);
        Assert.Equal(400, d.Traversal!.StandoffMm);
        Assert.Equal(40, d.Traversal.TiltDeg);
        Assert.True(d.Traversal.States > 1);
        Assert.Equal(d.Traversal.Ok, d.Traversal.Blocker is null);
        if (!d.Traversal.Ok) Assert.StartsWith("도면 순회", d.Traversal.Blocker);
        // 대상 점(중간)과 무관하게 시작점부터 같은 판정
        var d0 = (await sim.DetailAsync(SmArea, SmTask, 0, 500, 0, 0, 0, default))!;
        Assert.Equal(d.Traversal.Blocker, d0.Traversal!.Blocker);
        Assert.Equal(d.Traversal.MinMarginDeg, d0.Traversal.MinMarginDeg);
    }

    [Fact]
    public void 운영_J6_와_툴_장착각을_설정에서_읽고_none_이면_IK_값을_쓴다()
    {
        ReachRobotModel From(params (string K, string V)[] kv) => ReachSimService.ModelFrom(new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(x => x.K, x => (string?)x.V)).Build());

        var m = From(("Acs:Reach:OperationJ6Deg", "90"), ("Acs:Reach:ToolMountRollDeg", "-45"));
        Assert.Equal(90, m.OperationJ6Deg);
        Assert.Equal(-45, m.ToolMountRollDeg);
        Assert.Null(From(("Acs:Reach:OperationJ6Deg", "none")).OperationJ6Deg);
    }

    [Fact]
    public async Task 출발_자세가_있는_면은_상세에_진입_경로를_싣는다()
    {
        // 출발 자세 = 출발 자세 없이 푼 이 점의 ② 해(조사로 고른 리프트) — 진입 경로는 세 자세가 닿고 가지 전환이 없을 때만 본다.
        var plain = (await (await SeedAsync()).DetailAsync(SmArea, SmTask, 0, null, 0, 0, 0, default))!;
        var approach = plain.Stages[0].Solutions[plain.Stages[0].ChosenIndex].JointsDeg;
        var sim = await SeedAsync(new Dictionary<string, double[]> { ["SM"] = approach });
        var d = (await sim.DetailAsync(SmArea, SmTask, 0, plain.StrokeMm, 0, 0, 0, default))!;

        Assert.Null(d.BranchChange);
        Assert.NotNull(d.Entry);
        Assert.True(d.Entry!.InPlaneMm < 1, "출발 TCP = ② 접근점");
        Assert.NotNull(d.StartJointsDeg);
        Assert.False(string.IsNullOrEmpty(d.Entry!.Summary));
        var other = (await sim.DetailAsync(SlArea, SlTask, 1, 0, 0, 0, 0, default))!;
        Assert.Null(other.Entry);   // SL 은 출발 자세 없음 — 종전 판정
    }
}
