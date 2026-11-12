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

        var survey = Run(NoTraverse);
        Assert.Equal(ReachVerdict.Clear, survey.Verdict);
        Assert.NotNull(survey.StrokeMm);
        Assert.True(survey.MinClearanceM >= 0);
        // 대칭 ±175 한계(옛 가정)면 여유 최대가 elbow-down 가지라 팔꿈치가 기둥에 닿는다.
        Assert.Equal(ReachVerdict.Collision, Run(Symmetric175).Verdict);
        Assert.Equal(ReachVerdict.Clear, Run(Symmetric175 with { BranchRule = ReachBranchRule.MaxClearance }).Verdict);
    }

    private static readonly ReachRobotModel Symmetric175 = ReachRobotModel.Default with
    {
        CheckTraversal = false,
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
    public void 관절_한계는_FR5_WML_사양_비대칭이다()
    {
        var m = ReachRobotModel.Default;
        Assert.Equal(new[] { -175.0, -265, -170, -265, -175 }, m.JointMinDeg);
        Assert.Equal(new[] { 175.0, 85, 170, 85, 175 }, m.JointMaxDeg);
    }

    [Fact]
    public void 초점_자세에서_카메라_브래킷_끝은_대상_면에_98_5mm_앞이다()
    {
        // STEP 실측 툴 끝(카메라 브래킷) 플랜지 276.5 mm, 초점 375 mm — 광축이 면 법선과 나란해 상자 앞면 네 꼭짓점이 같은 거리다.
        var (sm, v, floor) = MidWall();
        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300);

        var tool = r.Stages[2].Chosen!.Parts.Single(p => p.Name == "검사 툴(카메라 브래킷)");
        Assert.Equal("SM", tool.NearestPlane);
        Assert.Equal(0.0985, tool.ClearanceM, 4);
        Assert.Equal(8, tool.Corners!.Length);
        Assert.Equal(0, tool.RadiusM);
    }

    [Fact]
    public void 세_자세의_J6_는_운영값_180도이고_J1_J5_는_그대로다()
    {
        var (sm, v, floor) = MidWall();
        ReachPointResult Run(ReachRobotModel m) => ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300);

        var op = Run(ReachRobotModel.Default);
        var ik = Run(ReachRobotModel.Default with { OperationJ6Deg = null });
        for (var s = 0; s < 3; s++)
        {
            var a = op.Stages[s].Chosen!.JointsDeg;
            var b = ik.Stages[s].Chosen!.JointsDeg;
            Assert.Equal(0, Math.IEEERemainder(a[5] - 180, 360), 9);
            for (var i = 0; i < 5; i++) Assert.Equal(b[i], a[i], 9);
            // J6 는 광축 둘레 회전 — 초점(TCP)은 같다.
            for (var i = 0; i < 3; i++) Assert.Equal(ik.Stages[s].Chosen!.Focus[i], op.Stages[s].Chosen!.Focus[i], 9);
        }
    }

    [Fact]
    public void J6_를_돌리는_것과_툴_장착각을_돌리는_것은_같은_툴_위치다()
    {
        var (sm, v, floor) = MidWall();
        double[][] Corners(ReachRobotModel m, string name) => ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300).Stages[2].Chosen!.Parts.Single(p => p.Name == name).Corners!;

        var a = Corners(ReachRobotModel.Default with { OperationJ6Deg = 90 }, "검사 툴(D435i)");
        var b = Corners(ReachRobotModel.Default with { OperationJ6Deg = 0, ToolMountRollDeg = 90 }, "검사 툴(D435i)");
        for (var k = 0; k < 8; k++)
            for (var i = 0; i < 3; i++) Assert.Equal(a[k][i], b[k][i], 6);

        // 장착각 180° 면 센서 헤드가 광축 반대편으로 — 상자 중심이 광축을 사이에 두고 마주 본다.
        static double[] Center(double[][] c) => Enumerable.Range(0, 3).Select(i => c.Average(p => p[i])).ToArray();
        var focusAxis = ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300).Stages[2].Chosen!.Focus;
        var h0 = Vec3.Sub(Center(Corners(ReachRobotModel.Default, "검사 툴(D435i)")), focusAxis);
        var h180 = Vec3.Sub(Center(Corners(ReachRobotModel.Default with { ToolMountRollDeg = 180 }, "검사 툴(D435i)")), focusAxis);
        var n = sm.Normal;
        double[] Lateral(double[] p) => Vec3.Sub(p, Vec3.Scale(n, Vec3.Dot(p, n)));
        var l0 = Lateral(h0);
        var l180 = Lateral(h180);
        Assert.Equal(-Vec3.Length(l0) * Vec3.Length(l180), Vec3.Dot(l0, l180), 6);
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

    // ── 운영 ② 출발 자세·진입 경로(HD_AMR 평면 경유 접근) ─────────────────────────

    /// <summary>MidWall 한 점의 ② 운영 선택 해 — 출발 자세 시험용.</summary>
    private static double[] ApproachJoints(ReachRobotModel m, GeneratedWall sm, double u, double v, double floor, double strokeMm = 0) =>
        ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(u, v), sm.Normal, strokeMm).Stages[0].Chosen!.JointsDeg;

    /// <summary>MidWall 에서 간섭 없이 닿는 리프트(조사).</summary>
    /// <summary>세 자세·진입만 보는 모델 — 리프트·진입·가지 규칙 테스트용(도면 순회 판정은 ReachTraversalTests).</summary>
    private static readonly ReachRobotModel NoTraverse = ReachRobotModel.Default with { CheckTraversal = false };

    private static double ClearStroke(GeneratedWall sm, double v, double floor) =>
        ReachSimulator.Survey(NoTraverse, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            new[] { StationFor(sm, 12, v, 1.2, 2) }, new[] { sm.To3D(12, v) }, sm.Normal,
            Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray()).StrokeMm!.Value;

    [Fact]
    public void 출발_자세가_없는_면은_종전_판정이다()
    {
        var (sm, v, floor) = MidWall();
        var r = ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 0, wallCode: "SM");

        Assert.Null(r.Entry);
        Assert.Null(r.BranchChange);
        Assert.True(r.Reachable);
    }

    [Fact]
    public void 출발_자세가_접근_자세와_같으면_들어갈_점으로_바로_진입한다()
    {
        // 후퇴 0 — 들어갈 점 = 접근점. (후퇴 100 mm 면 이 정차에서는 들어갈 점에서 전완이 기둥에 닿는다.)
        var (sm, v, floor) = MidWall();
        var h = ClearStroke(sm, v, floor);
        var start = ApproachJoints(ReachRobotModel.Default, sm, 12, v, floor, h);
        var m = ReachRobotModel.Default with { StartJointsByWall = new Dictionary<string, double[]> { ["SM"] = start }, PlanarRetreatMm = 0 };

        var r = ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, h, wallCode: "SM");

        Assert.Null(r.BranchChange);
        Assert.NotNull(r.Entry);
        Assert.True(r.Entry!.Ok, r.Entry.Summary);
        Assert.True(r.Entry.InPlaneMm < 1);
        Assert.Equal(1, r.Entry.Candidates);
        Assert.True(r.Reachable, r.Blocker);
        // 출발 TCP = ② 접근점(면에서 400 mm).
        Assert.True(Vec3.Length(Vec3.Sub(r.Entry.StartTcp, r.Stages[0].TcpTarget)) < 1e-6);
    }

    [Fact]
    public void 진입점_후보는_투영점에서_500mm_까지만_본다()
    {
        // 출발 TCP 가 ② 접근점에서 면을 따라 600 mm 옆 — HD_AMR 처럼 들어갈 점 직접(600 > 500)은 빼고
        // 투영점 + 100 mm 간격 5곳 = 후보 6개(실기 2026-11-02 PU H03: 면 방향 584 mm → 후보 6개).
        var (sm, v, floor) = MidWall();
        var h = ClearStroke(sm, v, floor);   // 세 자세가 닿는 리프트 — 진입 경로는 그때만 본다
        // 출발 자세 = 600 mm 옆 점의 ② 해 중 접근점 해와 같은 가지(여유 최대 해는 어깨 가지가 달라 가지 전환이 된다).
        // 장착 yaw 180(실기) — 0° 면 벽 쪽 J1 이 ±180° 경계에 걸려 옆 점 해가 J1 을 한 바퀴 돌린다.
        var mount = ReachRobotModel.Default with { MountYawDeg = 180 };
        var same = mount with
        {
            StartJointsByWall = new Dictionary<string, double[]> { ["SM"] = ApproachJoints(mount, sm, 12, v, floor, h) },
        };
        var start = ReachSimulator.Evaluate(same, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2), StationFor(sm, 12, v, 1.2, 2),
            sm.To3D(12.6, v), sm.Normal, h, wallCode: "SM", checkEntry: false).Stages[0].Chosen!.JointsDeg;
        var m = mount with { StartJointsByWall = new Dictionary<string, double[]> { ["SM"] = start } };

        var r = ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, h, wallCode: "SM");

        Assert.True(r.Entry is not null, r.Blocker ?? "진입 경로를 보지 않음");
        Assert.Equal(600, r.Entry!.InPlaneMm, 0);
        Assert.Equal(6, r.Entry.Candidates);
        if (r.Entry.Entry is { } entry)
            Assert.True(Vec3.Length(Vec3.Sub(entry, r.Entry.Plunge)) > 0.05, "들어갈 점 직접은 후보가 아니다");
    }

    [Fact]
    public void 진입_경로가_없으면_자세가_닿아도_도달불가다()
    {
        // 출발 자세를 벽 반대쪽으로 팔을 뻗은 자세로 두면 같은 가지·140° 안의 진입점이 없다.
        var (sm, v, floor) = MidWall();
        var away = ApproachJoints(ReachRobotModel.Default, sm, 12, v, floor).ToArray();
        away[0] += away[0] > 0 ? -170 : 170;
        var m = ReachRobotModel.Default with { StartJointsByWall = new Dictionary<string, double[]> { ["SM"] = away } };

        var r = ReachSimulator.Evaluate(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 0, wallCode: "SM");

        Assert.False(r.Reachable);
        Assert.NotNull(r.Blocker);
        Assert.True(r.BranchChange is not null || r.Entry is { Ok: false });

        var survey = ReachSimulator.Survey(m, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            new[] { StationFor(sm, 12, v, 1.2, 2) }, new[] { sm.To3D(12, v) }, sm.Normal, new[] { 0.0 }, "SM");
        Assert.Equal(ReachVerdict.Unreachable, survey.Verdict);
        Assert.NotNull(survey.Blocker);
    }

    [Fact]
    public void 가지_전환_사유는_팔꿈치_손목_반전과_큰_관절_이동이다()
    {
        var m = ReachRobotModel.Default;
        var start = new[] { 0.0, -90, 100, -90, 90, 0 };

        Assert.Null(ReachSimulator.BranchChangeReason(m, start, new[] { 30.0, -80, 90, -70, 80, 170 }));   // J6 는 보지 않는다
        Assert.Contains("팔꿈치 반전", ReachSimulator.BranchChangeReason(m, start, new[] { 0.0, -90, -100, -90, 90, 0 }));
        Assert.Contains("손목 반전", ReachSimulator.BranchChangeReason(m, start, new[] { 0.0, -90, 100, -90, -90, 0 }));
        Assert.Null(ReachSimulator.BranchChangeReason(m, start, new[] { 0.0, -90, 100, 40, 90, 0 }));          // 130° — 140° 한계 안
        Assert.Contains("J4 150°", ReachSimulator.BranchChangeReason(m, start, new[] { 0.0, -90, 100, 60, 90, 0 }));
    }

    [Fact]
    public void 장착_yaw는_J1_이름표만_바꾸고_간섭은_그대로다()
    {
        var (sm, v, floor) = MidWall();
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, floor, 2);
        var st = StationFor(sm, 12, v, 1.2, 2);
        var a = ReachSimulator.Evaluate(ReachRobotModel.Default, planes, st, sm.To3D(12, v), sm.Normal, 0);
        var b = ReachSimulator.Evaluate(ReachRobotModel.Default with { MountYawDeg = 180 }, planes, st, sm.To3D(12, v), sm.Normal, 0);

        foreach (var (sa, sb) in a.Stages.Zip(b.Stages))
            foreach (var x in sa.Solutions)
            {
                var y = sb.Solutions.Single(s => Enumerable.Range(1, 4).All(k => Math.Abs(s.JointsDeg[k] - x.JointsDeg[k]) < 1e-6));
                var dJ1 = ((y.JointsDeg[0] - x.JointsDeg[0]) % 360 + 360) % 360;
                Assert.Equal(180, dJ1, 6);
                Assert.Equal(x.MinClearanceM, y.MinClearanceM, 9);
            }
    }

    [Fact]
    public void 자기_간섭_판정은_장착_yaw를_따른다()
    {
        // BASE 가 180° 돌면 같은 팔 자세는 J1 + 180° 다 — 차체·기둥·신호탑까지 여유가 같아야 한다.
        var j = new[] { 20.0, -150, 120, -60, -90, 0 };
        var flipped = new[] { -160.0, -150, 120, -60, -90, 0 };
        var m0 = ReachSimulator.SelfClearance(ReachRobotModel.Default, 1635, j);
        var m180 = ReachSimulator.SelfClearance(ReachRobotModel.Default with { MountYawDeg = 180 }, 1635, flipped);
        Assert.Equal(m0.ClearanceMm, m180.ClearanceMm, 6);
    }
    [Fact]
    public void 실기_L4_R02C09_PU_H03_진입_경로_없음을_재현한다()
    {
        // 2026-11-02 15:50 현장 — AMR: "접근점은 현재 TCP 정면에서 면을 따라 584mm … 후보 6개 모두 거부:
        // 후퇴 평면 [-1120, -154, 80] / [-1188, -144, 11] / [-1256, -134, -58] / [-1324, -123, -127]: 자기 간섭".
        // 실기 값: 홈 위치_2 출발, T_A_B rz 180, 공구 #1 TCP 365(초점 자세는 10 mm 띄워 카메라 초점 375 를 용접선에), ② 이격 200, 실제 정차 (−3.037, 5.582, 94.1°), CT1 멤브레인 PU·L4 데크 10.1175.
        var home2 = new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
        var m = ReachRobotModel.Default with
        {
            MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 200.0, 300, 10 },
            StartJointsByWall = new Dictionary<string, double[]> { ["PU"] = home2 },
        };
        var pu = new WallPose(new[] { -15.48, 8.64, 10.304 }, new[] { 1.0, 0, 0 }, new[] { 0, -Math.Sqrt(0.5), Math.Sqrt(0.5) });
        var n = new[] { 0, -Math.Sqrt(0.5), -Math.Sqrt(0.5) };
        const double floor = 10.1175;
        var planes = ReachSimulator.PlanesForLevel(new[]
        {
            new ReachPlane("PU", pu.Origin, n),
            new ReachPlane("T", new[] { -15.48, -5.984, 12.96 }, new[] { 0.0, 0, -1 }),
        }, floor, 4);

        var r = ReachSimulator.Evaluate(m, planes, new ReachStation(-3.037, 5.582, 1.6429, floor),
            pu.LocalToDrawing(12.24, 1.8781), n, 0, wallCode: "PU");

        Assert.False(r.Reachable);
        Assert.NotNull(r.Entry);
        Assert.False(r.Entry!.Ok);
        Assert.Equal(584, r.Entry.InPlaneMm, 5.0);
        Assert.Equal(6, r.Entry.Candidates);
        Assert.All(r.Entry.Rejects, x => Assert.Contains("자기 간섭", x));
        // 후보 위치(BASE mm)가 AMR 보고와 5 mm 안.
        var amr = new[] { new[] { -1120.0, -154, 80 }, new[] { -1188.0, -144, 11 }, new[] { -1256.0, -134, -58 }, new[] { -1324.0, -123, -127 } };
        for (var i = 0; i < amr.Length; i++)
        {
            var got = System.Text.RegularExpressions.Regex.Match(r.Entry.Rejects[i], @"\[(-?\d+), (-?\d+), (-?\d+)\]").Groups;
            for (var k = 0; k < 3; k++) Assert.InRange(double.Parse(got[k + 1].Value) - amr[i][k], -6, 6);
        }
    }
    [Fact]
    public void 실기_20261112_PU_진입_거부를_J6_포함_판정으로_재현한다()
    {
        // 2026-11-12 21:30~21:42 현장 PU L4 — AMR 은 정차에 정확히 섰는데(오차 1~4 cm) "안전한 진입 경로 없음"으로 7건을 거부했다.
        //  · R02C24 #1(정차 10.76, 5.353, 90°, 리프트 100): "면을 따라 768mm … 후퇴 평면: 진입 관절 이동 221° > 140° / 224° / 226° / 229°"
        //  · R03C24 #1(정차 10.96, 4.982, 85°, 리프트 0):   "면을 따라 828mm … J6 안전 범위 밖 — J6 −179.7° → −155.2° / 자기 간섭"
        // ACS 는 진입점의 J6 을 운영 값(180°)으로 덮어써 이 두 사유를 보지 못했다. AMR 처럼 ② 접근 자세를 유지한 채 6축을 풀면 같은 사유가 나온다.
        var home2 = new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
        var m = ReachRobotModel.Default with
        {
            MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, CheckTraversal = false,
            StartJointsByWall = new Dictionary<string, double[]> { ["PU"] = home2 },
        };
        var pu = new WallPose(new[] { -15.48, 8.64, 10.304 }, new[] { 1.0, 0, 0 }, new[] { 0, -Math.Sqrt(0.5), Math.Sqrt(0.5) });
        var n = new[] { 0, -Math.Sqrt(0.5), -Math.Sqrt(0.5) };
        const double floor = 10.1175;
        var planes = ReachSimulator.PlanesForLevel(new[]
        {
            new ReachPlane("PU", pu.Origin, n),
            new ReachPlane("T", new[] { -15.48, -5.984, 12.96 }, new[] { 0.0, 0, -1 }),
        }, floor, 4);

        var c24 = ReachSimulator.Evaluate(m, planes, new ReachStation(10.76, 5.353, 1.5708, floor),
            pu.LocalToDrawing(25.74, 2.0054), n, 100, wallCode: "PU");
        Assert.NotNull(c24.Entry);
        Assert.False(c24.Entry!.Ok, c24.Entry.Summary);
        Assert.False(c24.Reachable);
        Assert.InRange(c24.Entry.InPlaneMm, 730, 790);                       // AMR 768 mm
        var travel = System.Text.RegularExpressions.Regex.Match(c24.Entry.Rejects[0], @"진입 관절 이동 (\d+)°");
        Assert.True(travel.Success, c24.Entry.Rejects[0]);
        Assert.InRange(int.Parse(travel.Groups[1].Value), 214, 228);         // AMR 221°

        var c03 = ReachSimulator.Evaluate(m, planes, new ReachStation(10.96, 4.982, 1.4835, floor),
            pu.LocalToDrawing(25.74, 2.5146), n, 0, wallCode: "PU");
        Assert.False(c03.Entry!.Ok, c03.Entry.Summary);
        Assert.Contains("J6 안전 범위", c03.Entry.Rejects[0]);                // AMR 첫 후보와 같은 사유
        Assert.Contains(c03.Entry.Rejects, x => x.Contains("자기 간섭"));

        // 종전 판정(J6 미포함)은 두 건 모두 진입 경로가 있다고 봤다 — 그래서 이 정차가 골라졌다.
        var old = m with { CheckEntryJ6 = false };
        Assert.True(ReachSimulator.Evaluate(old, planes, new ReachStation(10.76, 5.353, 1.5708, floor),
            pu.LocalToDrawing(25.74, 2.0054), n, 100, wallCode: "PU").Entry!.Ok);

        // 여유 기준을 0 으로 두면 마지막 후보(자기 간섭 여유 16 mm)가 통과한다 — 실기는 이 경계에서 거부했다.
        var noMargin = ReachSimulator.Evaluate(m with { EntrySelfMarginMm = 0 }, planes, new ReachStation(10.76, 5.353, 1.5708, floor),
            pu.LocalToDrawing(25.74, 2.0054), n, 100, wallCode: "PU");
        Assert.True(noMargin.Entry!.Ok);
        Assert.Equal(5, noMargin.Entry.Rejects.Count);
    }

    [Fact]
    public void 조사의_진입_점검을_상위_후보로_줄여도_전수_판정과_같다()
    {
        // 실기 설정(장착 180°·홈 위치_2 출발·② 200)으로 SM L2 여러 높이·위치 용접선을 조사 — 시작점만 진입 점검·좋은 후보부터 보는
        // 조사와, 모든 리프트에서 진입까지 다 본 뒤 고르는 전수 판정이 판정·리프트·여유가 같아야 한다.
        var home2 = new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
        var m = ReachRobotModel.Default with
        {
            MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 200.0, 300, 10 },
            StartJointsByWall = new Dictionary<string, double[]> { ["SM"] = home2 },
            CheckTraversal = false,   // 기준 구현이 세 자세·진입만 본다
        };
        var (sm, _, floor) = MidWall();
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, floor, 2);
        var strokes = Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray();
        var verdicts = new HashSet<ReachVerdict>();
        foreach (var dz in new[] { 0.4, 1.0, 1.6, 2.2 })
            foreach (var du in new[] { -0.6, 0.0, 0.5 })
            {
                var v = floor + dz - sm.Pose.Origin[2];
                var st = StationFor(sm, 12, v, 1.2, 2);
                var pts = new[] { sm.To3D(12 + du, v), sm.To3D(12 + du + 0.18, v), sm.To3D(12 + du + 0.36, v) };

                var fast = ReachSimulator.Survey(m, planes, new[] { st }, pts, sm.Normal, strokes, "SM");

                (double H, double Cl)? clear = null, hit = null;
                foreach (var h in strokes)
                {
                    // 기준 구현 — 점마다 진입까지 다 풀되, 중간·끝점은 앞 점 ② 해에서 이어 간다.
                    var rs = new List<ReachPointResult>();
                    double[]? seed = null;
                    foreach (var (p, i) in pts.Select((p, i) => (p, i)))
                    {
                        var r = ReachSimulator.Evaluate(m, planes, st, p, sm.Normal, h, withGeometry: false,
                            wallCode: i == 0 ? "SM" : null, seedJoints: seed);
                        rs.Add(r);
                        seed = ReachSimulator.ScanSeed(r);
                    }
                    if (!rs.All(r => r.Reachable)) continue;
                    var min = rs.Min(r => r.MinClearanceM);
                    if (min >= 0) { if (clear is null || min > clear.Value.Cl) clear = (h, min); }
                    else if (hit is null || min > hit.Value.Cl) hit = (h, min);
                }
                var (verdict, best) = clear is { } c ? (ReachVerdict.Clear, c) : hit is { } x ? (ReachVerdict.Collision, x) : (ReachVerdict.Unreachable, default((double H, double Cl)));

                Assert.Equal(verdict, fast.Verdict);
                if (verdict != ReachVerdict.Unreachable)
                {
                    Assert.Equal(best.Item1, fast.StrokeMm);
                    Assert.Equal(best.Item2, fast.MinClearanceM!.Value, 9);
                }
                else Assert.NotNull(fast.Blocker);
                verdicts.Add(verdict);
            }
        Assert.True(verdicts.Count >= 2, $"판정이 한 가지뿐이라 비교가 약하다: {string.Join(",", verdicts)}");
    }

    [Fact]
    public void 중간_끝점은_앞_점_가지를_따라가고_반전_점프면_도달불가다()
    {
        // 2026-11-02 PU L4-R03C04 #2: 끝점을 따로 풀면 다른 가지(J1 −158°)가 골라져 상완이 천장에 −59 mm — 운영은 그 가지로 가지 않는다.
        var (sm, v, floor) = MidWall();
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, floor, 2);
        var st = StationFor(sm, 12, v, 1.2, 2);
        var pts = new[] { sm.To3D(11.8, v), sm.To3D(12, v), sm.To3D(12.2, v) };
        var m = Symmetric175 with { BranchRule = ReachBranchRule.MaxClearance };

        ReachPointResult[] Chain(double h)
        {
            var rs = new ReachPointResult[3];
            double[]? seed = null;
            for (var i = 0; i < 3; i++)
            {
                rs[i] = ReachSimulator.Evaluate(m, planes, st, pts[i], sm.Normal, h, checkEntry: false, seedJoints: seed);
                seed = ReachSimulator.ScanSeed(rs[i]);
            }
            return rs;
        }

        // 리프트 0: 시작점 초점 자세에서 중간·끝점까지 같은 가지로 이어진다. 중간·끝점은 초점 자세로만 판정한다.
        var h0 = Chain(0);
        Assert.All(h0, r => Assert.True(r.Reachable, r.Blocker));
        Assert.False(h0[0].ScanOnly);
        Assert.True(h0[1].ScanOnly && h0[2].ScanOnly);
        Assert.Equal(new[] { ReachStage.Focus }, h0[1].JudgedStages.Select(s => s.Stage));
        for (var i = 1; i < 3; i++)
        {
            var a = ReachSimulator.ScanSeed(h0[i - 1])!;
            var b = ReachSimulator.ScanSeed(h0[i])!;
            Assert.Equal(Math.Sign(a[2]), Math.Sign(b[2]));   // 팔꿈치
            Assert.Equal(Math.Sign(a[4]), Math.Sign(b[4]));   // 손목
        }
        // 리프트 500: 중간 → 끝점에서 J1 이 한계(±175°) 반대편으로 감겨 139° 돈다 — 0.2 m 스캔으로는 갈 수 없다.
        var h500 = Chain(500);
        Assert.True(h500[1].Reachable);
        Assert.False(h500[2].Reachable);
        Assert.Matches(@"앞 점에서 J1 \d+° 이동 > 90°", h500[2].Blocker);
    }

    [Fact]
    public void 같은_점의_자세_사이_관절이_크게_튀면_도달불가다()
    {
        var (sm, v, floor) = MidWall();
        var r = ReachSimulator.Evaluate(ReachRobotModel.Default with { MaxStageJumpDeg = 1 },
            ReachSimulator.PlanesForLevel(WallPlanes, floor, 2), StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300);
        Assert.False(r.Reachable);
        Assert.StartsWith("자세 사이 가지 전환", r.Blocker);
        Assert.True(ReachSimulator.Evaluate(ReachRobotModel.Default, ReachSimulator.PlanesForLevel(WallPlanes, floor, 2),
            StationFor(sm, 12, v, 1.2, 2), sm.To3D(12, v), sm.Normal, 300).Reachable);
    }

    private static ReachSeamSurvey V(ReachVerdict v, double h, double? cl = null) =>
        new(v, v == ReachVerdict.Unreachable ? null : h, v == ReachVerdict.Unreachable ? null : cl ?? 0.01, 0,
            v == ReachVerdict.Unreachable ? "관절" : null);

    [Fact]
    public void 영역_리프트는_간섭_없음_최대_다음_도달_최대_다음_낮은_높이로_고른다()
    {
        var strokes = new[] { 0.0, 100, 200 };
        var table = new[]
        {
            new[] { V(ReachVerdict.Clear, 0, 0.02), V(ReachVerdict.Clear, 100, 0.05), V(ReachVerdict.Collision, 200) },
            new[] { V(ReachVerdict.Unreachable, 0), V(ReachVerdict.Collision, 100, -0.01), V(ReachVerdict.Clear, 200, 0.03) },
            new[] { V(ReachVerdict.Clear, 0), V(ReachVerdict.Clear, 100), V(ReachVerdict.Unreachable, 200) },
        };

        var r = AreaLift.Choose(strokes, table);

        // 0: 간섭 없음 2·도달 2 / 100: 간섭 없음 2·도달 3 / 200: 간섭 없음 1 → 100.
        Assert.Equal(100, r.StrokeMm);
        Assert.Equal(new[] { ReachVerdict.Clear, ReachVerdict.Collision, ReachVerdict.Clear }, r.Fixed.Select(s => s.Verdict));
        Assert.Equal((2, 3, 3, 3), (r.FixedClear, r.FixedReachable, r.BestClear, r.BestReachable));
        // 용접선별 최적 — 간섭 없음 중 여유가 큰 높이.
        Assert.Equal(new double?[] { 100, 200, 0 }, r.Best.Select(s => s.StrokeMm));

        // 점수가 같으면 낮은 높이.
        var tie = AreaLift.Choose(strokes, new[] { new[] { V(ReachVerdict.Clear, 0), V(ReachVerdict.Clear, 100), V(ReachVerdict.Clear, 200) } });
        Assert.Equal(0, tie.StrokeMm);
    }

    [Fact]
    public void 영역_리프트_조사의_용접선별_최적은_전_높이_조사와_같다()
    {
        var (sm, v, floor) = MidWall();
        var planes = ReachSimulator.PlanesForLevel(WallPlanes, floor, 2);
        var st = new[] { StationFor(sm, 12, v, 1.2, 2) };
        var strokes = new[] { 0.0, 300, 600, 900 };
        var seams = new IReadOnlyList<double[]>[]
        {
            new[] { sm.To3D(11.8, v), sm.To3D(12, v), sm.To3D(12.2, v) },
            new[] { sm.To3D(12, v - 1.2), sm.To3D(12, v - 1.1), sm.To3D(12, v - 1.0) },
            new[] { sm.To3D(12, v + 1.0), sm.To3D(12, v + 1.1), sm.To3D(12, v + 1.2) },
        };

        var r = AreaLift.Evaluate(ReachRobotModel.Default, planes, st, seams, sm.Normal, strokes, "SM");

        for (var i = 0; i < seams.Length; i++)
        {
            var all = ReachSimulator.Survey(ReachRobotModel.Default, planes, st, seams[i], sm.Normal, strokes, "SM");
            Assert.Equal(all.Verdict, r.Best[i].Verdict);
            Assert.Equal(all.StrokeMm, r.Best[i].StrokeMm);
            Assert.Equal(all.MinClearanceM, r.Best[i].MinClearanceM);
            // 고정 판정은 고른 높이 하나로 푼 조사와 같다.
            var one = ReachSimulator.Survey(ReachRobotModel.Default, planes, st, seams[i], sm.Normal, new[] { r.StrokeMm }, "SM");
            Assert.Equal(one.Verdict, r.Fixed[i].Verdict);
        }
        Assert.True(r.FixedClear <= r.BestClear && r.FixedReachable <= r.BestReachable);
    }
}
