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
    public void 벽_최소_거리를_주면_그보다_가까운_정차는_쓰지_않는다()
    {
        // A 면 가운데 — 차체 중심이 벽에서 1.5 m 이상 떨어져야 한다.
        var a = Ct1.GenerateWalls().Single(w => w.WallCode == "A");
        var c = a.To3D(a.ULen / 2, 5.5);
        var near = new StationSearchSpace(new[] { c[0], c[1] }, new[] { a.Normal[0], a.Normal[1] }, a.FacingYaw!.Value, 0, 1.2);
        var far = near with { MinWallDistM = 1.5 };
        var m = ReachRobotModel.Default with { CheckTraversal = false };

        var d0 = StationOptimizer.FirstBodyFeasible(m, Planes, near)!.Value.DistM;
        Assert.True(d0 < 1.5, $"제한 없는 첫 가능 거리 {d0:0.00}");
        Assert.Equal(1.5, StationOptimizer.FirstBodyFeasible(m, Planes, far)!.Value.DistM, 6);

        Assert.True(near.BodyAllowed(m, Planes, 1.4, 0, 0, out var cl));
        Assert.False(far.BodyAllowed(m, Planes, 1.4, 0, 0, out var cl2));
        Assert.Equal(cl, cl2, 9);                        // 보고하는 차체 여유는 그 자리 값 그대로
        Assert.True(far.BodyAllowed(m, Planes, 1.5, 0, 0, out _));

        var seams = new IReadOnlyList<double[]>[] { new[] { a.To3D(a.ULen / 2 - 0.3, 5.5), c, a.To3D(a.ULen / 2 + 0.3, 5.5) } };
        Assert.False(StationOptimizer.Evaluate(m, Planes, far, 1.2, 0, 0, seams, a.Normal, Strokes).Feasible);
        var r = StationOptimizer.Optimize(m, Planes, far, seams, a.Normal, Strokes);   // 지금 정차 1.2 m — 제한보다 가깝다
        Assert.False(r.Baseline.Feasible);
        if (r.Best.Feasible) Assert.True(r.Best.DistM >= 1.5 - 1e-9, $"거리 {r.Best.DistM:0.00}");
    }

    [Fact]
    public void 수평면_탐색에는_벽_최소_거리를_쓰지_않는다()
    {
        var t = Ct1.GenerateWalls().Single(w => w.WallCode == "T");
        var c = t.To3D(10, 3);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { t.Pose.U[0], t.Pose.U[1] }, 0, 10.53, 0,
            MinDistM: -1.2, MaxDistM: 1.2, MinWallDistM: 1.5);
        var planes = ReachSimulator.PlanesForLevel(Ct1.GenerateWalls().Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)), 10.53, 4);
        Assert.True(space.BodyAllowed(ReachRobotModel.Default, planes, 0, 0, 0, out _));
    }

    [Fact]
    public void 하부경사면에_걸친_횡격벽_끝_영역은_옆으로_비켜_선다()
    {
        // A 면 u = 4 m(SL 쪽 끝) — 1.2 m 이격 정차는 차체가 SL 밑. 벽에서 멀어져도 안 빠지고 선창 가운데(좌측 +)로 비켜야 한다.
        var a = Ct1.GenerateWalls().Single(w => w.WallCode == "A");
        var c = a.To3D(4, 2.5);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { a.Normal[0], a.Normal[1] }, a.FacingYaw!.Value, 0, 1.2);
        var seams = new IReadOnlyList<double[]>[] { new[] { a.To3D(3.7, 2.5), c, a.To3D(4.3, 2.5) } };

        // 차체 회피(옆으로 비키기)만 본다 — 도면 순회 판정을 켜면 이 격벽 끝 용접선은 ±40° 포괄 근사에서 손목 여유가 모자란다.
        var r = StationOptimizer.Optimize(ReachRobotModel.Default with { CheckTraversal = false }, Planes, space, seams, a.Normal, Strokes);

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

    [Fact]
    public void 정차_최적화는_영역의_모든_용접선에_동일한_리프트_높이를_고른다()
    {
        // SM 수직벽 영역 — 높이가 다른 용접선 2개(바닥 위 1.2 m, 1.8 m). 용접선별로는 h=0 과 h=500 을 따로 고르지만,
        // 정차 최적화는 영역 공통 리프트 높이 하나로 두 용접선이 함께 닿는 정차·높이를 고른다.
        var sm = Ct1.GenerateWalls().Single(w => w.WallCode == "SM");
        var vLow = 3.63 + 1.2 - 3.9776;
        var vHigh = 3.63 + 1.8 - 3.9776;
        var c = sm.To3D(12, (vLow + vHigh) / 2);
        var planes = ReachSimulator.PlanesForLevel(
            Ct1.GenerateWalls().Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)), 3.63, 2);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { sm.Normal[0], sm.Normal[1] }, sm.FacingYaw!.Value, 3.63, 1.2);
        var seams = new IReadOnlyList<double[]>[]
        {
            new[] { sm.To3D(11.8, vLow), sm.To3D(12.0, vLow), sm.To3D(12.2, vLow) },
            new[] { sm.To3D(11.8, vHigh), sm.To3D(12.0, vHigh), sm.To3D(12.2, vHigh) },
        };

        var r = StationOptimizer.Optimize(ReachRobotModel.Default, planes, space, seams, sm.Normal, Strokes);

        Assert.Equal(2, r.Best.Clear);
        Assert.NotNull(r.Best.StrokeMm);
        Assert.All(r.Best.Seams, s => Assert.Equal(r.Best.StrokeMm, s.StrokeMm));
    }

    [Fact]
    public void 천장_T_면은_중심_주위_수평_이동과_회전으로_운영_진입_가능한_정차를_찾는다()
    {
        // L4(10.53 m) 천장 T 면(z ≈ 13.785 m) — 출발 자세(홈 위치_2)가 있을 때 영역 중심 바로 아래(0,0,0°)에 서면
        // 머리 위·뒤쪽 용접선이 손목 반전·J1 회전 한계로 막히지만, 옆으로 비켜 서고 방향을 돌리면 같은 리프트로 모두 닿는다.
        var t = Ct1.GenerateWalls().Single(w => w.WallCode == "T");
        var planes = ReachSimulator.PlanesForLevel(
            Ct1.GenerateWalls().Select(w => new ReachPlane(w.WallCode, w.Pose.Origin, w.Normal)), 10.53, 4);
        var model = ReachRobotModel.Default with
        {
            MountYawDeg = 180,
            ToolZMm = 365,
            StageStandoffsMm = new[] { 200.0, 300, 10 },
            StartJointsByWall = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["T"] = new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 },
            },
        };
        var c = t.To3D(15, 2.0);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { t.Pose.U[0], t.Pose.U[1] }, 0, 10.53,
            0, 0, 0, MinDistM: -1.2, MaxDistM: 1.2, MaxLateralM: 1.2, MaxYawDeg: 180, YawStepDeg: 15);
        var seams = new IReadOnlyList<double[]>[]
        {
            new[] { t.To3D(14.7, 1.8), t.To3D(15.0, 1.8), t.To3D(15.3, 1.8) },
            new[] { t.To3D(14.7, 2.2), t.To3D(15.0, 2.2), t.To3D(15.3, 2.2) },
        };

        var r = StationOptimizer.Optimize(model, planes, space, seams, t.Normal, Strokes, wallCode: "T");

        Assert.True(r.Best.Clear > r.Baseline.Clear, $"Baseline={r.Baseline.Clear}, Best={r.Best.Clear}");
        Assert.Equal(2, r.Best.Clear);
        Assert.NotNull(r.Best.StrokeMm);
        Assert.All(r.Best.Seams, s => Assert.Equal(r.Best.StrokeMm, s.StrokeMm));
    }
}
