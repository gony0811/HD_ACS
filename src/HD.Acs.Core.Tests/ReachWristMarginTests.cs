using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 손목 여유 |J5| — ACS 가 HD_AMR 과 같은 10° 로 판정하는지(2026-11-16 이전 ACS 는 5° 라 AMR 이 막는 5~10° 자세를 통과시켰다).
/// 실기 2026-11-03 L2-R02C06-3(F) 순회의 최소 |J5| 는 10.7° 다 — 5° 기준 여유 5.7°, 10° 기준 0.7°.
/// </summary>
public class ReachWristMarginTests
{
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    private static readonly ReachRobotModel Model = ReachRobotModel.Default with
    {
        MountYawDeg = 0, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, ScanStandoffMm = 400,
        ScanTiltProfile = ReachTraversalBacktestTests.Line1Cor,
        StartJointsByWall = new Dictionary<string, double[]> { ["F"] = Ready },
    };
    private static readonly ReachStation BaseStation = new(0, 0, 0, -ReachRobotModel.Default.MountZMm / 1000);

    /// <summary>작업물 프레임(BASE mm·RPY 도)과 순회 직전 J6 로 순회를 푼다 — <see cref="ReachTraversalBacktestTests"/> 와 같은 방법.</summary>
    private static ReachTraversal Run(ReachRobotModel m, double[] originMm, double[] rpyDeg, double j6)
    {
        double a = rpyDeg[0] * Math.PI / 180, b = rpyDeg[1] * Math.PI / 180, g = rpyDeg[2] * Math.PI / 180;
        double[] x = { Math.Cos(g) * Math.Cos(b), Math.Sin(g) * Math.Cos(b), -Math.Sin(b) };
        double[] z =
        {
            Math.Cos(g) * Math.Sin(b) * Math.Cos(a) + Math.Sin(g) * Math.Sin(a),
            Math.Sin(g) * Math.Sin(b) * Math.Cos(a) - Math.Cos(g) * Math.Sin(a),
            Math.Cos(b) * Math.Cos(a),
        };
        var top = Enumerable.Range(0, 3).Select(i => originMm[i] + ReachRobotModel.ScanTopZMm * z[i]).ToArray();
        var s0 = Enumerable.Range(0, 3).Select(i => (top[i] - 400 * z[i]) / 1000).ToArray();
        var s1 = Enumerable.Range(0, 3).Select(i => s0[i] + 0.36 * x[i]).ToArray();
        var r = ReachSimulator.Evaluate(m, Array.Empty<ReachPlane>(), BaseStation, s0, z, 0, withGeometry: false, wallCode: "F", checkEntry: false);
        var seed = (double[])ReachSimulator.TraverseSeed(r)!.Clone();
        seed[5] = j6;
        return ReachSimulator.Traverse(m, BaseStation, 0, s0, s1, z, seed);
    }

    private static readonly double[] R02C06_3 = { -1532.3, 107.0, 367.1 };
    private static readonly double[] Rpy = { 89.98, -0.03, 90.18 };

    [Fact]
    public void 순회_여유는_손목_여유_기준만큼_줄어든다()
    {
        var at5 = Run(Model with { PlanarWristMarginDeg = 5 }, R02C06_3, Rpy, 90);
        var at10 = Run(Model, R02C06_3, Rpy, 90);

        Assert.Contains("손목 특이점", at5.Limiting);
        Assert.Equal(at5.MinMarginDeg - 5, at10.MinMarginDeg, 6);   // 같은 자세, 기준만 5° 높다
        Assert.True(at10.Ok);                                        // 최소 |J5| 10.7° — 10° 기준에서도 통과(여유 0.7°)
        Assert.InRange(at10.MinMarginDeg, 0, 2);
    }

    [Fact]
    public void 손목_여유보다_J5_가_작아지는_순회는_막는다()
    {
        // 같은 용접선을 손목 여유 12° 로 보면(최소 |J5| 10.7° < 12°) 막힌다 — 5~10° 구간도 같은 식으로 10° 기준에서 막힌다.
        var strict = Run(Model with { PlanarWristMarginDeg = 12 }, R02C06_3, Rpy, 90);
        Assert.False(strict.Ok);
        Assert.Contains("손목 특이점", strict.Blocker);
        Assert.True(strict.MinMarginDeg < 0);

        // 실기에서 5° 기준으로 통과했던 R02C05-2(최소 |J5| 16.2°)는 10° 기준에서도 통과한다.
        var ok = Run(Model, new[] { -1374.0, -108.2, 367.2 }, new[] { 89.99, -0.02, 90.62 }, 90);
        Assert.True(ok.Ok);
        Assert.InRange(ok.MinMarginDeg, 5, 7.5);   // 16.2 − 10
    }

    [Fact]
    public void 순회가_막힌_용접선은_도달_불가라_정차_선택의_완화_분기도_고르지_않는다()
    {
        // 순회에서 |J5| 가 손목 여유보다 작아지면 조사(Survey)는 그 용접선을 Unreachable 로 돌려준다. 배차 직전 정차 선택(Nearest)의
        // '전부 도달만 만족' 완화 분기는 도달 불가가 하나라도 있는 후보를 건너뛰므로, |J5| < 10° 자세가 있는 정차는 고르지 않는다.
        // 실기 정차·용접선(ReachTraversalTests 의 L2-R02C05 H04 — 순회 중 손목 특이점)으로 확인한다.
        var station = new ReachStation(13.7255, 2.4803, -0.0302, 3.2175);
        var normal = new[] { -1.0, 0, 0 };
        var planes = new[]
        {
            new ReachPlane("F", new[] { 15.48, 0, 0 }, normal),
            new ReachPlane(ReachPlane.DeckCode, new[] { 0.0, 0, 3.2175 }, new[] { 0.0, 0, 1 }),
        };
        var field = Model with { MountYawDeg = 180 };
        static double[] F(double u, double v) => new[] { 15.48, 8.64 - u, v };
        var u0 = 5.7599 + 0.36 * 3;
        var h04 = new[] { F(u0, 5.22), F(u0 + 0.18, 5.22), F(u0 + 0.36, 5.22) };

        var s = ReachSimulator.Survey(field, planes, new[] { station }, h04, normal, new[] { 0.0 }, "F");
        Assert.Equal(ReachVerdict.Unreachable, s.Verdict);
        Assert.Contains("손목 특이점", s.Blocker);

        var space = new StationSearchSpace(new[] { 15.48, 8.64 - (u0 + 0.18) }, new[] { -1.0, 0 }, 0, 3.2175,
            BaselineDistM: 15.48 - station.X, BaselineLateralM: 0, BaselineYawDeg: 0);
        var c = StationOptimizer.Evaluate(field, planes, space, 15.48 - station.X, 0, 0,
            new IReadOnlyList<double[]>[] { h04 }, normal, new[] { 0.0 }, wallCode: "F");
        if (c.Seams[0].Verdict == ReachVerdict.Unreachable) Assert.Equal(1, c.Unreachable);   // 완화 분기의 조건(Unreachable > 0 → 건너뜀)
    }
}
