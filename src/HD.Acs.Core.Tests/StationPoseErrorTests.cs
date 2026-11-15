using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 정차 위치 오차(SLAM·도착 오차) 반영과 순위 — 오차 표본(8방향 + 방향 ±)에서 도달·간섭·최소 여유를 다시 보고,
/// "제자리와 오차 표본 전체의 최소 여유(보장 여유)"가 큰 정차를 고른다. 여유는 목표(10°)에서 자르지 않고 비교한다.
/// </summary>
public class StationPoseErrorTests
{
    private static readonly TankGeometry Ct1 = new(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976,
        new[] { 0.0, 3.63, 6.63, 10.53 });
    private static readonly GeneratedWall Sl = Ct1.GenerateWalls().Single(w => w.WallCode == "SL");
    private static readonly IReadOnlyList<ReachPlane> Planes = ReachSimulator.PlanesForLevel(
        Ct1.GenerateWalls().Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)), 0, 1);
    private static readonly double[] Strokes = Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray();

    /// <summary>SL 위 높이 h[m] 영역 — 가로 1 m 용접선 하나(<see cref="StationOptimizerTests"/> 와 같은 배치).</summary>
    private static (StationSearchSpace Space, IReadOnlyList<IReadOnlyList<double[]>> Seams) Area(double heightM)
    {
        var v = heightM / Math.Sin(Math.PI / 4);
        var c = Sl.To3D(10, v);
        var h = Math.Sqrt(Sl.Normal[0] * Sl.Normal[0] + Sl.Normal[1] * Sl.Normal[1]);
        var inward = new[] { Sl.Normal[0] / h, Sl.Normal[1] / h };
        var space = new StationSearchSpace(new[] { c[0], c[1] }, inward, Sl.FacingYaw!.Value, 0, BaselineDistM: 0.8 * h);
        var seam = new[] { Sl.To3D(9.5, v), Sl.To3D(10, v), Sl.To3D(10.5, v) };
        return (space, new IReadOnlyList<double[]>[] { seam });
    }

    private static StationCandidate C(int clear, int miss, double? margin, PoseErrorStats? error = null, double robust = 0.1,
        double cap = double.PositiveInfinity, int? safe = null) =>
        new(1.5, 0, 0, new ReachStation(0, 0, 0, 0), 0.3, true, clear, 0, miss, robust, Array.Empty<ReachSeamSurvey>(), 0,
            safe ?? clear, margin, 10, error, cap);

    private static PoseErrorStats E(int miss, double? margin, int collision = 0) => new(10, miss, collision, margin, 0.3, 3);

    [Fact]
    public void 오차_표본은_반경_위_8방향과_방향_양쪽이다()
    {
        var s = StationOptimizer.PoseErrorSamples(new PoseErrorOptions(0.3, 3));
        Assert.Equal(10, s.Count);
        var moved = s.Where(x => x.YawDeg == 0).ToList();
        Assert.Equal(8, moved.Count);
        Assert.All(moved, x => Assert.Equal(0.3, Math.Sqrt(x.DistM * x.DistM + x.LateralM * x.LateralM), 9));
        Assert.Equal(4, moved.Count(x => Math.Abs(x.DistM) > 1e-9 && Math.Abs(x.LateralM) > 1e-9));   // 대각선 4개
        Assert.Equal(new[] { -3.0, 3.0 }, s.Where(x => x.DistM == 0 && x.LateralM == 0).Select(x => x.YawDeg).OrderBy(y => y));

        Assert.Equal(8, StationOptimizer.PoseErrorSamples(new PoseErrorOptions(0.3, 0)).Count);
        Assert.Equal(2, StationOptimizer.PoseErrorSamples(new PoseErrorOptions(0, 3)).Count);
        Assert.Empty(StationOptimizer.PoseErrorSamples(new PoseErrorOptions(0, 0)));
        Assert.False(new PoseErrorOptions(0, 0).Enabled);
    }

    [Fact]
    public void 여유는_목표에서_자르지_않고_비교한다()
    {
        // 종전: 10° 이상은 같게 봤다. 지금: 여유 확보 수가 같으면 실제 최소 여유가 큰 쪽.
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 25), C(4, 0, 15)) > 0);
        Assert.Equal(23.5, StationOptimizer.MarginKey(23.4, 10));
        // 상한을 10 으로 주면 종전 동작 — 여유는 같고 다음 기준(면 간섭 여유)으로 넘어간다.
        Assert.Equal(10, StationOptimizer.MarginKey(23.4, 10, 10));
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 15, robust: 0.12, cap: 10), C(4, 0, 25, robust: 0.10, cap: 10)) > 0);
        // 여유 확보 수(목표 이상인 용접선 수)는 여전히 최소 여유보다 먼저다.
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 9, safe: 4), C(4, 0, 30, safe: 3)) > 0);
    }

    [Fact]
    public void 오차에서도_전부_닿는_정차가_제자리_여유가_큰_정차보다_앞선다()
    {
        var fragile = C(4, 0, 25, E(miss: 1, margin: 20));   // 제자리 여유는 크지만 0.3 m 어긋나면 용접선 하나를 놓친다
        var sturdy = C(4, 0, 12, E(miss: 0, margin: 6));
        Assert.True(StationOptimizer.Ranking.Compare(sturdy, fragile) > 0);

        // 제자리에서 전부 닿는 것이 오차 판정보다 먼저다.
        var missing = C(3, 1, 30, E(miss: 0, margin: 30));
        Assert.True(StationOptimizer.Ranking.Compare(fragile, missing) > 0);

        // 오차에서 놓치는 수가 적은 쪽, 그다음 오차에서 간섭이 없는 쪽.
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 12, E(1, 5)), C(4, 0, 12, E(2, 9))) > 0);
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 12, E(0, 5)), C(4, 0, 12, E(0, 9, collision: 1))) > 0);
    }

    [Fact]
    public void 보장_여유는_제자리와_오차_표본_중_최소이고_순위는_그_값이_큰_쪽이다()
    {
        var a = C(4, 0, 25, E(0, 3));    // 제자리 25° 지만 어긋나면 3°
        var b = C(4, 0, 15, E(0, 9));    // 제자리 15°, 어긋나도 9°
        Assert.Equal(3, a.GuaranteedMarginDeg);
        Assert.Equal(9, b.GuaranteedMarginDeg);
        Assert.True(StationOptimizer.Ranking.Compare(b, a) > 0);
        // 보장 여유가 같으면 제자리 여유가 큰 쪽.
        Assert.True(StationOptimizer.Ranking.Compare(C(4, 0, 25, E(0, 9)), b) > 0);
        // 오차 판정이 없는 후보는 제자리 여유가 곧 보장 여유다.
        Assert.Equal(25, C(4, 0, 25).GuaranteedMarginDeg);
        Assert.Equal(7, C(4, 0, 12, E(0, 7)).GuaranteedMarginDeg);
        Assert.Equal(12, C(4, 0, 12, E(1, null)).GuaranteedMarginDeg);   // 오차 표본에 닿는 용접선이 없어 여유가 없으면 제자리 값
    }

    [Fact]
    public void 오차_판정은_도달뿐_아니라_간섭과_최소_여유도_낸다()
    {
        var (space, seams) = Area(1.45);
        var model = ReachRobotModel.Default;
        var nominal = StationOptimizer.Optimize(model, Planes, space, seams, Sl.Normal, Strokes).Best;
        Assert.Equal(1, nominal.Clear);
        Assert.Null(nominal.Error);

        // 오차가 거의 없으면(1 mm·0.01°) 제자리 판정과 같다 — 전부 닿고 여유도 거의 같다.
        var small = StationOptimizer.WithPoseError(model, Planes, space, nominal, seams, Sl.Normal, Strokes, new PoseErrorOptions(0.001, 0.01));
        Assert.Equal(10, small.Error!.Samples);
        Assert.Equal(0, small.Error.WorstMiss);
        Assert.NotNull(small.Error.MinMarginDeg);
        Assert.InRange(small.Error.MinMarginDeg!.Value, nominal.TraverseMarginDeg!.Value - 0.5, nominal.TraverseMarginDeg.Value + 0.5);
        Assert.True(small.GuaranteedMarginDeg <= nominal.TraverseMarginDeg + 1e-9);
        Assert.Equal(Math.Min(nominal.TraverseMarginDeg!.Value, small.Error.MinMarginDeg!.Value), small.GuaranteedMarginDeg);

        // 오차를 크게 잡으면(1.5 m) 닿지 않는 표본이 생긴다 — 도달 불가 수가 잡힌다.
        var large = StationOptimizer.WithPoseError(model, Planes, space, nominal, seams, Sl.Normal, Strokes, new PoseErrorOptions(1.5, 0));
        Assert.Equal(8, large.Error!.Samples);
        Assert.True(large.Error.WorstMiss + large.Error.WorstCollision > 0);
        // 제자리 판정은 그대로다.
        Assert.Equal(nominal.Clear, large.Clear);
        Assert.Equal(nominal.TraverseMarginDeg, large.TraverseMarginDeg);
    }

    [Fact]
    public void 위치_오차를_주면_최적화는_오차를_견디는_정차를_고른다()
    {
        var (space, seams) = Area(1.45);
        var model = ReachRobotModel.Default;
        var pe = new PoseErrorOptions(0.3, 3, TopCandidates: 4, MaxBatches: 2);

        var plain = StationOptimizer.Optimize(model, Planes, space, seams, Sl.Normal, Strokes);
        var robust = StationOptimizer.Optimize(model, Planes, space, seams, Sl.Normal, Strokes, poseError: pe);

        Assert.NotNull(robust.Baseline.Error);
        Assert.NotNull(robust.Best.Error);
        Assert.Equal(1, robust.Best.Clear);
        Assert.True(robust.Evaluated > plain.Evaluated);   // 오차 표본만큼 더 계산한다
        // 오차 없이 고른 정차를 같은 오차 표본으로 다시 재면, 오차를 보고 고른 정차가 순위에서 같거나 앞선다.
        var plainScored = StationOptimizer.WithPoseError(model, Planes, space, plain.Best, seams, Sl.Normal, Strokes, pe);
        Assert.True(StationOptimizer.Ranking.Compare(robust.Best, plainScored) >= 0,
            $"오차 반영 {robust.Best.Error!.WorstMiss}/{robust.Best.GuaranteedMarginDeg:0.0}° vs 미반영 {plainScored.Error!.WorstMiss}/{plainScored.GuaranteedMarginDeg:0.0}°");
    }

    [Fact]
    public void 배차_직전_정차_선택의_도착_오차_기본값은_300mm_8방향이다()
    {
        var o = new NearestOptions();
        Assert.Equal(0.3, o.RobustM);
        Assert.Equal(3, o.RobustYawDeg);
        Assert.Equal(10, StationOptimizer.PoseErrorSamples(new PoseErrorOptions(o.RobustM, o.RobustYawDeg)).Count);
    }
}
