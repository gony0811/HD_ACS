using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>정차점 최적화 — CT1 하부 경사면에서 기본 정차(차체가 강판 밑)를 옮겨 닿게 하고, 못 닿는 영역은 옮기지 않는다.</summary>
public class StationOptimizerTests
{
    private static readonly TankGeometry Ct1 = new(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976,
        new[] { 0.0, 3.63, 6.63, 10.53 });
    private static readonly GeneratedWall Sl = Ct1.GenerateWalls().Single(w => w.WallCode == "SL");
    private static readonly IReadOnlyList<ReachPlane> Planes = ReachSimulator.PlanesForLevel(
        Ct1.GenerateWalls().Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)), 0, 1);
    private static readonly double[] Strokes = Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray();

    /// <summary>SL 위 높이 h[m] 영역 — 중심과 가로 1 m 용접선 하나, 지금 정차는 ACS 기본(수평 법선 × 0.8 m).</summary>
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

    [Fact]
    public void 차체가_묻히는_기본_정차를_벽에서_떼어_닿게_한다()
    {
        var (space, seams) = Area(1.45);
        var r = StationOptimizer.Optimize(ReachRobotModel.Default, Planes, space, seams, Sl.Normal, Strokes);

        Assert.False(r.Baseline.Feasible);
        Assert.Equal(0, r.Baseline.Clear);
        Assert.Equal(1, r.Best.Clear);
        Assert.True(r.Best.Feasible);
        Assert.True(r.Best.DistM > r.Baseline.DistM + 0.5, $"거리 {r.Best.DistM:0.00}");
        Assert.True(r.Best.BodyClearanceM >= space.MinBodyClearanceM);
        Assert.True(r.Evaluated > 10);
    }

    [Fact]
    public void 어디서도_닿지_않으면_지금_정차를_그대로_둔다()
    {
        var (space, seams) = Area(3.6);   // 바닥에서 3.6 m — 리프트 최대로도 닿지 않는다
        var r = StationOptimizer.Optimize(ReachRobotModel.Default, Planes, space, seams, Sl.Normal, Strokes);

        Assert.Same(r.Baseline, r.Best);
        Assert.Equal(0, r.Best.Clear);
    }

    [Fact]
    public void 하부경사면에_걸친_횡격벽_끝_영역은_옆으로_비켜_선다()
    {
        // A 면 u = 4 m(SL 쪽 끝) — 1.2 m 이격 정차는 차체가 SL 밑. 벽에서 멀어져도 안 빠지고 선창 가운데(좌측 +)로 비켜야 한다.
        var a = Ct1.GenerateWalls().Single(w => w.WallCode == "A");
        var c = a.To3D(4, 2.5);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { a.Normal[0], a.Normal[1] }, a.FacingYaw!.Value, 0, 1.2);
        var seams = new IReadOnlyList<double[]>[] { new[] { a.To3D(3.7, 2.5), c, a.To3D(4.3, 2.5) } };

        var r = StationOptimizer.Optimize(ReachRobotModel.Default, Planes, space, seams, a.Normal, Strokes);

        Assert.False(r.Baseline.Feasible);
        Assert.Equal(1, r.Best.Clear);
        Assert.True(r.Best.LateralM >= 0.3, $"좌우 {r.Best.LateralM:0.0}");
    }

    [Fact]
    public void 정차_좌표는_중심에서_안쪽과_좌측으로_간다()
    {
        var s = new StationSearchSpace(new[] { 1.0, 2.0 }, new[] { 0.0, 1.0 }, -Math.PI / 2, 0.5, 1);
        var st = s.StationOf(2, 0.5, 10);
        Assert.Equal(1 - 0.5, st.X, 9);   // 안쪽 +y, 좌측 = 안쪽을 반시계 90° = −x
        Assert.Equal(4, st.Y, 9);
        Assert.Equal(-Math.PI / 2 + Math.PI / 18, st.Yaw, 9);
        Assert.Equal(0.5, st.FloorZ);
    }
}
