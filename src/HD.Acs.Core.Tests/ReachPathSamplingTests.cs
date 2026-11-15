using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 도면 순회 경로 분할 — 경유점 사이를 3D 이동 거리(면 쪽으로 넣었다 빼는 Z 포함)와 자세 회전각 기준으로 나눠 판정한다.
/// 2026-11-16 까지는 60 mm·10° 간격이었고, 6축 경유점 사이 거리에 Z 를 넣지 않아 넣었다 빼는 구간이 한 칸으로 지나갔다.
/// </summary>
public class ReachPathSamplingTests
{
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    private static readonly ReachRobotModel Field = ReachRobotModel.Default with
    {
        MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, ScanStandoffMm = 400,
        StartJointsByWall = new Dictionary<string, double[]> { ["F"] = Ready },
    };

    private const double FloorZ = 3.2175;
    private static readonly ReachStation Station = new(13.7255, 2.4803, -0.0302, FloorZ);
    private static readonly double[] Normal = { -1.0, 0, 0 };
    private static readonly IReadOnlyList<ReachPlane> Planes = new[]
    {
        new ReachPlane("F", new[] { 15.48, 0, 0 }, Normal),
        new ReachPlane(ReachPlane.DeckCode, new[] { 0.0, 0, FloorZ }, new[] { 0.0, 0, 1 }),
    };

    private static double[] F(double u, double v) => new[] { 15.48, 8.64 - u, v };
    private static readonly double[][] Seam = { F(5.7599, 5.22), F(5.9399, 5.22), F(6.1199, 5.22) };   // L2-R02C05 H01

    private static double[] Seed(ReachRobotModel m) =>
        ReachSimulator.ScanSeed(ReachSimulator.Evaluate(m, Planes, Station, Seam[0], Normal, 0, withGeometry: false, wallCode: "F"))!;

    private static ReachTraversal Run(ReachRobotModel m, IReadOnlyList<ScanWaypoint> wps) =>
        ReachSimulator.Traverse(m, Station, 0, Seam[0], Seam[2], Normal, Seed(m), waypoints: wps);

    [Fact]
    public void 기본_간격은_25mm_5도다()
    {
        Assert.Equal(25, ReachRobotModel.Default.ScanSampleMm);
        Assert.Equal(5, ReachRobotModel.Default.ScanTiltStepDeg);
        Assert.Equal(10, ReachRobotModel.Default.ChainSampleDeg);   // 관절 튐 판정 창은 HD_AMR 경로 검증 간격 그대로
        Assert.Equal(100, ReachRobotModel.Default.PlanarSampleMm);
    }

    [Fact]
    public void 면_쪽으로_넣었다_빼는_Z_이동도_나눠_본다()
    {
        // 같은 X·Y 에서 Z 만 100 mm 움직이는 두 경유점 — X·Y 거리는 0 이지만 3D 로 100 mm 다.
        var wps = new[] { new ScanWaypoint(0, 0, 133.8, 0, 0, 0), new ScanWaypoint(0, 0, 33.8, 0, 0, 0) };
        var t = Run(Field, wps);
        Assert.Equal(5, t.Points.Count);   // 시작 + 100 mm ÷ 25 mm 4칸 (종전: 2점 — 사이를 보지 않았다)
        // 사이 점들은 면 쪽으로 고르게 다가간다
        var x = t.Points.Select(p => p.Tcp[0]).ToArray();
        for (var i = 1; i < x.Length; i++) Assert.Equal(0.025, x[i] - x[i - 1], 6);   // F 면은 +x 쪽 — Z 가 줄면 면으로 다가간다
        Assert.All(t.Points.Skip(1).Take(3), p => Assert.Equal(2, p.Waypoint));        // 경유점 #2 앞 경로

        // 간격을 넓히면 그만큼 성기게 본다
        Assert.Equal(2, Run(Field with { ScanSampleMm = 200 }, wps).Points.Count);
    }

    [Fact]
    public void 자세가_도는_구간은_5도마다_본다()
    {
        // 제자리에서 툴을 40° 기울이는 두 경유점 — 위치는 그대로, 자세만 돈다.
        var wps = new[] { new ScanWaypoint(100, 0, 33.8, 0, 0, 0), new ScanWaypoint(100, 0, 33.8, 0, 40, 0) };
        var fine = Run(Field, wps);
        var coarse = Run(Field with { ScanTiltStepDeg = 10 }, wps);
        Assert.Equal(9, fine.Points.Count);     // 시작 + 40° ÷ 5° 8칸
        Assert.Equal(5, coarse.Points.Count);   // 종전 간격 10°
        // 촘촘히 보면 최소 여유가 같거나 더 작게 나온다(표본 사이에서 여유가 잠깐 작아지는 것을 잡는다)
        Assert.True(fine.MinMarginDeg <= coarse.MinMarginDeg + 1e-9, $"{fine.MinMarginDeg:0.00} vs {coarse.MinMarginDeg:0.00}");
    }

    [Fact]
    public void 촘촘한_판정은_실기_프로필에서_여유를_더_작게_또는_같게_본다()
    {
        // 실제 티칭 프로필(LINE-1-COR, 거리·기울기·Z)로 H01 순회 — 60 mm·10° 간격과 25 mm·5° 간격.
        var old = Field with { ScanTiltProfile = ReachTraversalBacktestTests.Line1Cor, ScanSampleMm = 60, ScanTiltStepDeg = 10 };
        var now = Field with { ScanTiltProfile = ReachTraversalBacktestTests.Line1Cor };
        var a = ReachSimulator.Traverse(old, Station, 0, Seam[0], Seam[2], Normal, Seed(old));
        var b = ReachSimulator.Traverse(now, Station, 0, Seam[0], Seam[2], Normal, Seed(now));
        Assert.True(b.Points.Count > a.Points.Count);
        Assert.True(b.MinMarginDeg <= a.MinMarginDeg + 1e-9, $"{b.MinMarginDeg:0.00} vs {a.MinMarginDeg:0.00}");
        Assert.Equal(a.Ok, b.Ok);   // 이 용접선은 실기에서 순회를 마쳤다 — 촘촘히 봐도 통과
    }

    [Fact]
    public void 관절_튐은_촘촘한_간격에서도_AMR_검증_간격의_변화로_본다()
    {
        // 가지가 바뀌지 않는 경로 — 촘촘히 나눠도(한 칸의 변화는 작아져도) 판정은 그대로 통과한다.
        var wps = new[] { new ScanWaypoint(0, 0, 33.8, 0, 0, 0), new ScanWaypoint(200, 0, 33.8, 0, 0, 0) };
        var t = Run(Field, wps);
        Assert.True(t.Ok, t.Blocker);
        Assert.Equal(9, t.Points.Count);   // 200 mm ÷ 25 mm
        // 관절 튐 허용을 아주 작게 하면, 한 칸(25 mm)의 변화는 그보다 작아도 100 mm 창의 변화로 걸린다.
        var perStep = Enumerable.Range(1, t.Points.Count - 1)
            .Max(i => Enumerable.Range(0, 6).Max(k => Math.Abs(t.Points[i].JointsDeg[k] - t.Points[i - 1].JointsDeg[k])));
        var over100 = Enumerable.Range(4, t.Points.Count - 4)
            .Max(i => Enumerable.Range(0, 6).Max(k => Math.Abs(t.Points[i].JointsDeg[k] - t.Points[i - 4].JointsDeg[k])));
        Assert.True(over100 > perStep * 2);
        var limit = (perStep + over100) / 2;   // 한 칸 변화보다는 크고 100 mm 변화보다는 작은 허용값
        var strict = Run(Field with { MaxPathJointStepDeg = limit }, wps);
        Assert.False(strict.Ok);
        Assert.Contains("튐", strict.Blocker);
    }
}
