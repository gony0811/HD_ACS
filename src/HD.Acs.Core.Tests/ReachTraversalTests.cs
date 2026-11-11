using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 도면 순회(⑱ inspectionRun) 판정 — 실기 2026-11-03 CT1 L2-R02C05(F 벽) 재현.
/// 그날 같은 정차(도면 13.7255, 2.4803, θ −0.0302 rad — 운영자가 '현재 AMR 위치를 정차점으로' 지정)·리프트 0(liftStrokeMm 전달 전)에서
/// H01~H03 은 순회를 마쳤고 H04 는 경유점 #5(J5 0.48°)에서 rc=185 로 멈췄다(HD_AMR InspectionRunWaypointChainTests 의 실측 관절값).
/// 세 자세(② · 정렬 준비 · 초점)만 보는 종전 조사는 8개 모두 Clear 였다.
/// </summary>
public class ReachTraversalTests
{
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    /// <summary>App 설정(Acs:Reach)과 같은 실기 모델.</summary>
    private static readonly ReachRobotModel Field = ReachRobotModel.Default with
    {
        MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, ScanStandoffMm = 400,
        StartJointsByWall = new Dictionary<string, double[]> { ["F"] = Ready },
    };

    private const double FloorZ = 3.2175;   // CT1 L2 발판
    private static readonly ReachStation Station = new(13.7255, 2.4803, -0.0302, FloorZ);
    private static readonly double[] Normal = { -1.0, 0, 0 };   // F 벽 내부향
    private static readonly IReadOnlyList<ReachPlane> Planes = new[]
    {
        new ReachPlane("F", new[] { 15.48, 0, 0 }, Normal),
        new ReachPlane(ReachPlane.DeckCode, new[] { 0.0, 0, FloorZ }, new[] { 0.0, 0, 1 }),
    };

    /// <summary>F 면 (u, v) → 도면 — 원점 (15.48, 8.64, 0), u = −y, v = z.</summary>
    private static double[] F(double u, double v) => new[] { 15.48, 8.64 - u, v };

    /// <summary>L2-R02C05 용접선 H01~H04 — 가로 360 mm, v 5.22.</summary>
    private static double[][] Seam(int seq)
    {
        var u0 = 5.7599 + 0.36 * (seq - 1);
        return new[] { F(u0, 5.22), F(u0 + 0.18, 5.22), F(u0 + 0.36, 5.22) };
    }

    private static double[] StartSeed(ReachRobotModel m, double[][] pts) =>
        ReachSimulator.ScanSeed(ReachSimulator.Evaluate(m, Planes, Station, pts[0], Normal, 0, withGeometry: false, wallCode: "F"))!;

    [Fact]
    public void 자세_모델은_실기_H04_경유점5_관절값을_재현한다()
    {
        // 실측 [-49.86, -38.87, 80.35, -223.91, 0.48, -87.57] — FK 하면 TCP 가 F 면에서 417 mm, H04 시작점(u 6.832) 높이, 광축 +39° 기울기.
        var pts = Seam(4);
        var m = Field with { ScanStandoffMm = 417, ScanTiltProfile = new[] { new[] { -8.0, 39 } } };
        // 특이점 근처에선 손목을 뒤집은 두 해(J5 ±0.5°)가 거의 같은 자세라, 실기 출발 자세 쪽(HD_AMR 테스트의 경유점 #1 근사, J5 +20°)에서 잇는다.
        var t = ReachSimulator.Traverse(m, Station, 0, pts[0], pts[2], Normal, new[] { -46.0, -40, 80, -221, 20, 90 });

        var j = Assert.Single(t.Points).JointsDeg;
        double[] real = { -49.86, -38.87, 80.35, -223.91, 0.48, -87.57 };
        for (var i = 0; i < 3; i++) Assert.True(Math.Abs(j[i] - real[i]) < 1.0, $"J{i + 1} {j[i]:0.00} vs {real[i]}");
        // 손목 특이점 근처라 J4·J6 나눔은 정해지지 않는다 — |J5| 와 J4+J6(손목 뒤집기 해도 같은 값)으로 대조한다.
        // 광축 둘레 비틀림(0/180°)은 J6 만 180° 바꾸므로 180° 주기로 본다(비틀림 선택은 아래 테스트가 따로 본다).
        Assert.True(Math.Abs(Math.Abs(j[4]) - 0.48) < 1.0, $"J5 {j[4]:0.00}");
        var sum = ((j[3] + j[5] - (real[3] + real[5])) % 180 + 270) % 180 - 90;
        Assert.True(Math.Abs(sum) < 3.0, $"J4+J6 차 {sum:0.0}");
        Assert.False(t.Ok);
        Assert.Contains("손목 특이점", t.Blocker);
    }

    [Fact]
    public void 실기_L2_R02C05_H04_는_시작점_기울인_자세에서_손목_특이점으로_막힌다()
    {
        var pts = Seam(4);
        var t = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, StartSeed(Field, pts));

        Assert.False(t.Ok);
        Assert.StartsWith("도면 순회 — 0mm 지점 기울기 +", t.Blocker);
        Assert.Contains("손목 특이점", t.Blocker);
        Assert.True(t.MinMarginDeg < 0);
        // 비틀림 — 실기 로그 '현재 J6 180.4°, 후보 0°→J6 90.0°' 와 같게 0° 를 고르고 첫 자세 J6 은 90°(= 감김 기준 −270°).
        Assert.Equal(0, t.TwistDeg);
        Assert.Equal(90, t.Points[0].JointsDeg[5], 0);
    }

    [Fact]
    public void 실기에서_순회를_마친_H01_H02_는_통과한다()
    {
        foreach (var seq in new[] { 1, 2 })
        {
            var pts = Seam(seq);
            var t = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, StartSeed(Field, pts));
            Assert.True(t.Ok, $"H0{seq}: {t.Blocker}");
            Assert.True(t.MinMarginDeg >= 0);
        }
    }

    [Fact]
    public void 포괄_근사는_보수적이다_H03_은_실기에서_마쳤지만_막힌다()
    {
        // ±40° 를 용접선 전 구간에 적용하므로 실제 프로필보다 엄하다 — 실기 H03 은 순회를 마쳤다.
        // 실제 경유점(거리, 기울기)을 주면 그 순서만 본다(여기서는 기울기 0 — 통과).
        var pts = Seam(3);
        var seed = StartSeed(Field, pts);
        Assert.False(ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed).Ok);

        var flat = Field with { ScanTiltProfile = new[] { new[] { 0.0, 0 }, new[] { 360.0, 0 } } };
        var t = ReachSimulator.Traverse(flat, Station, 0, pts[0], pts[2], Normal, seed);
        Assert.True(t.Ok, t.Blocker);
        Assert.Equal(7, t.Points.Count);   // 0 → 360 mm 를 60 mm 간격으로
    }

    [Fact]
    public void 조사는_세_자세가_닿아도_순회가_막히면_도달불가다()
    {
        var old = ReachSimulator.Survey(Field with { CheckTraversal = false }, Planes, new[] { Station }, Seam(4), Normal, new[] { 0.0 }, "F");
        Assert.Equal(ReachVerdict.Clear, old.Verdict);
        Assert.Null(old.TraverseMarginDeg);

        var now = ReachSimulator.Survey(Field, Planes, new[] { Station }, Seam(4), Normal, new[] { 0.0 }, "F");
        Assert.Equal(ReachVerdict.Unreachable, now.Verdict);
        Assert.StartsWith("도면 순회", now.Blocker);

        var h01 = ReachSimulator.Survey(Field, Planes, new[] { Station }, Seam(1), Normal, new[] { 0.0 }, "F");
        Assert.Equal(ReachVerdict.Clear, h01.Verdict);
        Assert.True(h01.TraverseMarginDeg >= 0);
    }

    [Fact]
    public void 영역_리프트_고정_조사도_순회를_본다()
    {
        var seams = Enumerable.Range(1, 4).Select(s => (IReadOnlyList<double[]>)Seam(s)).ToList();
        var (_, rows) = AreaLift.EvaluateFixed(Field, Planes, Station, seams, Normal, new[] { 0.0 }, "F");

        Assert.Equal(ReachVerdict.Clear, rows[0].Verdict);
        Assert.Equal(ReachVerdict.Clear, rows[1].Verdict);
        Assert.Equal(ReachVerdict.Unreachable, rows[3].Verdict);
        Assert.StartsWith("도면 순회", rows[3].Blocker);
    }

    [Fact]
    public void 두_비틀림_모두_J6_안전_범위_밖이면_막힌다()
    {
        // 순회 직전 J6 0° — 어느 감김으로도 −300°~−160° 에 들지 않아 감김 이동 없이 보면 첫 자세 J6 90°·270° 모두 범위 밖.
        var pts = Seam(1);
        var seed = (double[])StartSeed(Field, pts).Clone();
        seed[5] = 0;
        var t = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed);

        Assert.False(t.Ok);
        Assert.Contains("J6 안전 범위", t.Blocker);
    }

    [Fact]
    public void 점수는_전부_도달을_먼저_그다음_여유_확보_수를_본다()
    {
        ReachSeamSurvey S(ReachVerdict v, double? m) => new(v, 0, 0.1, 0, TraverseMarginDeg: m);
        // A: 간섭 없음 3개지만 여유 확보(≥10°) 1개, B: 여유 확보 2개지만 하나는 도달 불가 → A 가 낫다
        // (AMR 은 한 점이라도 불가면 정차 전체를 실행하지 않는다).
        var a = StationOptimizer.Score(new[] { S(ReachVerdict.Clear, 2), S(ReachVerdict.Clear, 3), S(ReachVerdict.Clear, 12) }, 10);
        var b = StationOptimizer.Score(new[] { S(ReachVerdict.Clear, 15), S(ReachVerdict.Clear, 11), S(ReachVerdict.Unreachable, null) }, 10);
        Assert.Equal((1, 3, 3, 2.0, 0), a);
        Assert.Equal((2, 2, 2, 10.0, 1), b);
        Assert.True(StationOptimizer.CompareScore(a, b) > 0);
        // 둘 다 전부 도달이면 여유 확보 수가 먼저다.
        var c = StationOptimizer.Score(new[] { S(ReachVerdict.Clear, 15), S(ReachVerdict.Clear, 11), S(ReachVerdict.Collision, 12) }, 10);
        Assert.True(StationOptimizer.CompareScore(c, a) > 0);
        // 최소 여유는 목표에서 자르고 0.5° 단위 — 순회 판정이 없으면(null) 목표로 본다.
        Assert.Equal(10, StationOptimizer.MarginKey(23.4, 10));
        Assert.Equal(4.5, StationOptimizer.MarginKey(4.6, 10));
        Assert.Equal(10, StationOptimizer.MarginKey(null, 10));
    }

    [Fact]
    public void 정차_최적화는_실기_R02C05_에서_순회_여유_확보_수가_많은_정차를_찾는다()
    {
        // 실제 프로필(LINE-1-COR)로 H01~H04 — 실기 정차(리프트 0)에서는 H03·H04 가 막힌다.
        var m = Field with { ScanTiltProfile = ReachTraversalBacktestTests.Line1Cor };
        var center = F(6.4799, 5.4);
        var space = new StationSearchSpace(new[] { center[0], center[1] }, new[] { -1.0, 0 }, 0, FloorZ,
            BaselineDistM: 15.48 - Station.X, BaselineLateralM: center[1] - Station.Y, BaselineYawDeg: Station.Yaw * 180 / Math.PI);
        var seams = Enumerable.Range(1, 4).Select(s => (IReadOnlyList<double[]>)Seam(s)).ToList();
        var strokes = Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray();

        var r = StationOptimizer.Optimize(m, Planes, space, seams, Normal, strokes, wallCode: "F");

        // 지금 정차: 간섭 없음 2(여유 확보 1) — H03·H04 순회 막힘. 찾은 정차(거리 2.1 m·리프트 100): 4개 모두 순회, 여유 확보 3.
        // 영역 오른쪽 끝 H04 는 여유 1.5° 에 그친다 — 한 정차로는 네 용접선 모두 10° 를 확보하지 못한다(영역 재분할 대상).
        Assert.Equal(2, r.Baseline.Clear);
        Assert.Equal(1, r.Baseline.SafeClear);
        Assert.Equal(4, r.Best.Clear);
        Assert.Equal(3, r.Best.SafeClear);
        Assert.True(r.Best.TraverseMarginDeg > 0);
    }

    [Fact]
    public void 순회_판정을_끄면_TraverseSeam_은_null()
    {
        var pts = Seam(4);
        Assert.Null(ReachSimulator.TraverseSeam(Field with { CheckTraversal = false }, Station, 0, pts, Normal, StartSeed(Field, pts)));
        Assert.NotNull(ReachSimulator.TraverseSeam(Field, Station, 0, pts, Normal, StartSeed(Field, pts)));
    }
}
