using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 리치·간섭 시뮬레이터 — FR5-WML 기구학이 HD_AMR 과 같은 해를 내는지, CT1 정차 배치에서 차체·팔 간섭을
/// 분석(2026-11-01)과 같은 방향으로 판정하는지 본다.
/// </summary>
public class ReachSimulatorTests
{
    private static readonly Fr5Kinematics Kin = Fr5Kinematics.Fr5Wml;

    // CT1 — 31.785 × 바닥 10.1497, 경사 45°(하부 3.9776 · 상부 2.8976), 수직벽 6.9097, 층 z 0/3.63/6.63/10.53.
    private static readonly TankGeometry Ct1 = new(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976,
        new[] { 0.0, 3.63, 6.63, 10.53 });
    private static readonly IReadOnlyList<GeneratedWall> Walls = Ct1.GenerateWalls();
    private static readonly ReachPlane[] WallPlanes = Walls.Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)).ToArray();

    [Fact]
    public void 역기구학은_순기구학_자세를_되찾는다()
    {
        var q = new[] { 25.0, -80, 110, -120, 70, 30 };
        var t = Kin.Frames(q)[6];
        var sols = Kin.InverseFlange(t);

        Assert.Equal(8, sols.Count);
        Assert.Contains(sols, s => Enumerable.Range(0, 6).All(i => Math.Abs(s[i] - q[i]) < 1e-6));
        foreach (var s in sols)
        {
            var back = Kin.Frames(s)[6];
            for (var i = 0; i < 3; i++) Assert.Equal(t[i, 3], back[i, 3], 6);
        }
    }

    [Fact]
    public void HD_AMR_모델과_같은_해를_낸다()
    {
        // TCP [800,0,0, rx 180] · 공구 +Z 375 → 플랜지 (800,0,375), 광축 아래. HD_AMR 오프라인 IK 첫 해.
        var t = Mat4.FromAxes(new[] { 1.0, 0, 0 }, new[] { 0.0, -1, 0 }, new[] { 0.0, 0, -1 }, new[] { 800.0, 0, 375 });
        var sols = Kin.InverseFlange(t);
        var expected = new[] { 168.5, -68.9, 116.6, 42.3, 90.0, 78.5 };
        Assert.Contains(sols, s => Enumerable.Range(0, 6).All(i => Math.Abs(s[i] - expected[i]) < 0.1));
    }

    private static GeneratedWall Wall(string code) => Walls.Single(w => w.WallCode == code);

    /// <summary>영역 중심 (u,v) 에서 ACS 정차 식(수평 법선 × 이격)으로 정차를 만든다.</summary>
    private static ReachStation StationFor(GeneratedWall w, double u, double v, double standoffM, int level)
    {
        var d = AreaGeometry.StationDrawing(w.Pose, u, v, w.Normal, standoffM);
        return new ReachStation(d[0], d[1], w.FacingYaw!.Value, Ct1.LevelZ[level - 1]);
    }

    [Fact]
    public void 하부경사면_2행_정차는_차체가_면에_묻힌다()
    {
        // SL 중심 높이 ≈ 1.45 m → 수평 0.8 m 이격 정차는 경사면 끝단보다 0.65 m 안쪽(강판 밑).
        var sl = Wall("SL");
        var v = 1.45 / Math.Sin(Math.PI / 4);
        var st = StationFor(sl, 10, v, 0.8, 1);
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, 0, 1);

        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, planes, st, sl.To3D(10, v), sl.Normal, 700);

        Assert.True(r.Body.ClearanceM < -0.3, $"차체 여유 {r.Body.ClearanceM:0.000}");
        Assert.Equal("SL", r.Body.NearestPlane);
    }

    [Fact]
    public void L4_데크_위_상부경사면은_차체_윗모서리에_붙는다()
    {
        // L4 데크(10.53)는 SU 시작(10.887) 바로 아래 — 수직벽에서 1.2 m 이격해도 차체 윗모서리(0.887 m)가 SU 밑으로 들어간다.
        var sm = Wall("SM");
        var floor = Ct1.LevelZ[3];
        var v = floor + 0.1 - sm.Pose.Origin[2];
        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 4),
            StationFor(sm, 12, v, 1.2, 4), sm.To3D(12, v), sm.Normal, 0);

        Assert.Equal("SU", r.Body.NearestPlane);
        Assert.InRange(r.Body.ClearanceM, 0.15, 0.25);   // (0.805 − 0.53) × cos45° ≈ 0.195
    }

    // L2(데크 3.63) 수직벽 SM 의 데크 위 1.5 m — 수직벽 한가운데.
    private static (GeneratedWall Wall, double V, double Floor) MidWall()
    {
        var sm = Wall("SM");
        var floor = Ct1.LevelZ[1];
        return (sm, floor + 1.5 - sm.Pose.Origin[2], floor);
    }

    [Fact]
    public void 수직벽_1_2m_정차에서_세_자세로_초점까지_닿는다()
    {
        var (sm, v, floor) = MidWall();
        var st = StationFor(sm, 12, v, 1.2, 2);
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, floor, 2);

        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, planes, st, sm.To3D(12, v), sm.Normal, 0);

        Assert.True(r.Body.ClearanceM > 0);
        Assert.Equal(3, r.Stages.Count);
        Assert.True(r.Reachable, string.Join(" / ", r.Stages.Select(s => s.Chosen?.Limiting ?? "해 없음")));
        // 초점 검사 자세의 TCP(초점)는 용접선 위다.
        var focus = r.Stages[2].Chosen!.Focus;
        Assert.True(Vec3.Length(Vec3.Sub(focus, sm.To3D(12, v))) < 1e-6);
        Assert.Equal(new[] { 400.0, 300, 0 }, r.Stages.Select(s => s.StandoffMm));
    }

    [Fact]
    public void 뒤_자세는_앞_자세와_같은_가지로_잇는다()
    {
        var (sm, v, floor) = MidWall();
        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 0);

        for (var i = 1; i < r.Stages.Count; i++)
        {
            var a = r.Stages[i - 1].Chosen!.JointsDeg;
            var b = r.Stages[i].Chosen!.JointsDeg;
            Assert.True(Enumerable.Range(0, 5).Max(k => Math.Abs(a[k] - b[k])) < 60);
        }
    }

    [Fact]
    public void 조사는_간섭_없이_닿는_리프트를_찾는다()
    {
        // 수직벽 한가운데 — 실측 관절 한계(J2 ≤ +85)에서는 운영 가지(자세 여유 최대)도 팔꿈치를 든 가지라 간섭 없이 닿는다.
        var (sm, v, floor) = MidWall();
        ReachSeamSurvey Run(ReachRobotModel m) => ReachSimulator.Survey(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            new[] { StationFor(sm, 12, v, 1.2, 2) }, new[] { sm.To3D(11.8, v), sm.To3D(12, v), sm.To3D(12.2, v) },
            sm.Normal, new[] { 0.0, 500, 1000 });

        var survey = Run(ReachRobotModel.Default);
        Assert.Equal(ReachVerdict.Clear, survey.Verdict);
        Assert.NotNull(survey.StrokeMm);
        Assert.True(survey.MinClearanceM >= 0);
        // 대칭 ±175 한계(옛 가정)면 여유 최대가 elbow-down 가지라 팔꿈치가 기둥에 닿는다.
        Assert.Equal(ReachVerdict.Collision, Run(Symmetric175).Verdict);
        Assert.Equal(ReachVerdict.Clear, Run(Symmetric175 with { BranchRule = ReachBranchRule.MaxClearance }).Verdict);
    }

    private static readonly ReachRobotModel Symmetric175 = ReachRobotModel.Default with
    {
        JointMinDeg = new[] { -175.0, -175, -175, -175, -175 },
        JointMaxDeg = new[] { 175.0, 175, 175, 175, 175 },
    };

    [Fact]
    public void elbow_down_가지는_팔꿈치가_리프트_기둥에_닿는다()
    {
        var (sm, v, floor) = MidWall();
        ReachPart Worst(ReachRobotModel m) => ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300).Stages[2].Chosen!.Parts.MinBy(p => p.ClearanceM)!;

        var hanging = Worst(Symmetric175);
        Assert.Equal("리프트 기둥", hanging.NearestPlane);
        Assert.True(hanging.ClearanceM < 0);
        Assert.True(Worst(ReachRobotModel.Default).ClearanceM >= 0);
    }

    [Fact]
    public void 관절_한계는_FR5_실측_비대칭이다()
    {
        var m = ReachRobotModel.Default;
        Assert.Equal(new[] { -175.0, -265, -160, -265, -175 }, m.JointMinDeg);
        Assert.Equal(new[] { 175.0, 85, 160, 85, 175 }, m.JointMaxDeg);
    }

    [Fact]
    public void 초점_자세에서_툴_끝은_대상_면에_6mm_앞이다()
    {
        // STEP 실측 툴 끝(조명·레이저) 369 mm, 초점 375 mm — 광축이 면 법선과 나란하므로 원통 반경은 여유를 줄이지 않는다.
        var (sm, v, floor) = MidWall();
        var m = ReachRobotModel.Default with { BranchRule = ReachBranchRule.MaxClearance };
        var r = ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300);

        var tool = r.Stages[2].Chosen!.Parts.Single(p => p.Name == "검사 툴(몸체·조명)");
        Assert.Equal("SM", tool.NearestPlane);
        Assert.Equal(0.006, tool.ClearanceM, 3);
        Assert.Equal(0.234, tool.RadiusM, 3);
    }

    [Fact]
    public void 선분_상자_거리는_가장_가까운_점까지다()
    {
        double[] min = { -100, -100, 887 }, max = { 100, 100, 1635 };
        Assert.Equal(0, ReachSimulator.SegmentBoxDistanceMm(new[] { -300.0, 0, 1000 }, new[] { 300.0, 0, 1000 }, min, max), 6);
        Assert.Equal(50, ReachSimulator.SegmentBoxDistanceMm(new[] { 150.0, -500, 1000 }, new[] { 150.0, 500, 1200 }, min, max), 3);
        Assert.Equal(Math.Sqrt(50 * 50 + 50 * 50 + 65 * 65),
            ReachSimulator.SegmentBoxDistanceMm(new[] { 150.0, 150, 700 }, new[] { 150.0, 150, 822 }, min, max), 3);
    }

    [Fact]
    public void 닿지_않으면_조사는_도달불가다()
    {
        var (sm, v, floor) = MidWall();
        // 벽에서 5 m 떨어진 정차 — 팔이 닿지 않는다.
        var survey = ReachSimulator.Survey(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            new[] { StationFor(sm, 12, v, 5.0, 2) }, new[] { sm.To3D(12, v) }, sm.Normal, new[] { 0.0, 1000 });

        Assert.Equal(ReachVerdict.Unreachable, survey.Verdict);
    }
}
