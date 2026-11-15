using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 6축 경유점(HD_AMR 절대 자세 프로필) 순회 판정 — 직선 프로필은 종전 [거리, 기울기, Z] 판정과 같은 결과를 내고,
/// 교차(CROSS4) 프로필은 −90° 돌린 팔까지 이어 푼다.
/// </summary>
public class ReachWaypointTraversalTests
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

    private static double[][] Seam(int seq)
    {
        var u0 = 5.7599 + 0.36 * (seq - 1);
        return new[] { F(u0, 5.22), F(u0 + 0.18, 5.22), F(u0 + 0.36, 5.22) };
    }

    private static double[] StartSeed(ReachRobotModel m, double[][] pts) =>
        ReachSimulator.ScanSeed(ReachSimulator.Evaluate(m, Planes, Station, pts[0], Normal, 0, withGeometry: false, wallCode: "F"))!;

    /// <summary>LINE-1-COR-261109 의 앞·가운데·뒤 일부 — [거리, 기울기, Z].</summary>
    private static readonly double[][] LineTilt =
    {
        new[] { 0, 0, 33.8 }, new[] { 4.91, 10, 33.37 }, new[] { 14.13, 30, 30.02 }, new[] { 25.54, 45, 11.88 },
        new[] { 45.0, 0, 0 }, new[] { 180.0, 0, 0 }, new[] { 300.0, 0, 0 },
        new[] { 332.46, -45, 11.88 }, new[] { 345.87, -30, 30.02 }, new[] { 360.0, 0, 33.8 },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]   // 실기에서 손목 특이점으로 막힌 용접선 — 막히는 판정도 같아야 한다
    public void 직선_프로필은_6축_경유점으로_풀어도_종전_판정과_같다(int seq)
    {
        var pts = Seam(seq);
        var seed = StartSeed(Field, pts);
        var legacy = ReachSimulator.Traverse(Field with { ScanTiltProfile = LineTilt }, Station, 0, pts[0], pts[2], Normal, seed);
        var waypoints = LineTilt.Select(v => new ScanWaypoint(v[0], 0, v[2], 0, v[1], 0)).ToList();
        var exact = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed, waypoints: waypoints);

        Assert.Equal(legacy.Ok, exact.Ok);
        Assert.Equal(legacy.TwistDeg, exact.TwistDeg);
        Assert.Equal(legacy.Points.Count, exact.Points.Count);
        Assert.Equal(legacy.MinMarginDeg, exact.MinMarginDeg, 6);
        for (var i = 0; i < legacy.Points.Count; i++)
            for (var k = 0; k < 6; k++)
                Assert.Equal(legacy.Points[i].JointsDeg[k], exact.Points[i].JointsDeg[k], 6);
        if (!legacy.Ok) Assert.Contains("경유점 #", exact.Blocker);   // 사유는 경유점 번호로 적는다
    }

    [Fact]
    public void 경유점을_Rz_로_돌리면_같은_자리에서_J6_만_그만큼_돈다()
    {
        // 공구 가로 오프셋을 보지 않는 모델이라 광축 둘레 회전은 J1~J5 를 바꾸지 않는다
        var pts = Seam(1);
        var seed = StartSeed(Field, pts);
        ReachTraversal At(double rz) => ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed,
            waypoints: new[] { new ScanWaypoint(180, 0, 33.8, 0, 0, rz) });

        var flat = At(0).Points[0].JointsDeg;
        var turned = At(-90).Points[0].JointsDeg;
        for (var k = 0; k < 5; k++) Assert.Equal(flat[k], turned[k], 6);
        Assert.Equal(90, Math.Abs(turned[5] - flat[5]), 6);
    }

    [Fact]
    public void 경유점_Y_는_용접선에_수직인_면_안쪽_방향으로_옮긴다()
    {
        // F 벽 가로 용접선: 작업물 X = 용접선 방향, Z = 면에서 멀어지는 쪽, Y = Z × X
        var pts = Seam(1);
        var seed = StartSeed(Field, pts);
        double[] Tcp(double x, double y, double z) => ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed,
            waypoints: new[] { new ScanWaypoint(x, y, z, 0, 0, 0) }).Points[0].Tcp;

        var o = Tcp(0, 0, 33.8);
        Assert.Equal(pts[0][0] - 0.4, o[0], 9);   // Top = 면에서 순회 이격만큼
        Assert.Equal(pts[0][1], o[1], 9);
        Assert.Equal(pts[0][2], o[2], 9);

        var dir = new[] { pts[2][0] - pts[0][0], pts[2][1] - pts[0][1], pts[2][2] - pts[0][2] };
        var len = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
        var along = Tcp(100, 0, 33.8);
        Assert.Equal(o[1] + 0.1 * dir[1] / len, along[1], 9);   // X 는 용접선을 따라

        var side = Tcp(0, 100, 33.8);
        Assert.Equal(o[0], side[0], 9);                         // 면까지 거리는 그대로
        Assert.Equal(o[1], side[1], 9);
        Assert.Equal(0.1, Math.Abs(side[2] - o[2]), 9);         // 가로 용접선이면 위아래로 100 mm

        Assert.Equal(o[0] + 0.0338, Tcp(0, 0, 0)[0], 9);        // Z = 0 은 면 쪽으로 33.8 mm
    }

    [Fact]
    public void 교차_프로필은_돌린_팔까지_이어_풀고_막히는_경유점_번호를_적는다()
    {
        // 가로 팔(Rz 0) → 세로 팔(Rz −90, Y 방향) — 세로 팔 끝을 닿지 않는 곳으로 보내면 그 경유점에서 막힌다
        var pts = Seam(2);
        var seed = StartSeed(Field, pts);
        var cross = new[]
        {
            new ScanWaypoint(0, 0, 33.8, 0, 0, 0), new ScanWaypoint(180, 0, 0, 0, 0, 0), new ScanWaypoint(360, 0, 33.8, 0, 0, 0),
            new ScanWaypoint(205, -195, 29.8, 0, 0, -90), new ScanWaypoint(205, -12.5, 0, 0, 0, -90),
            new ScanWaypoint(205, 170, 29.8, 0, 0, -90),
        };
        var t = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed, waypoints: cross);

        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6 }, t.Points.Select(p => p.Waypoint).Distinct().ToArray());
        var arm1 = t.Points.Last(p => p.Waypoint == 3).JointsDeg[5];
        var arm2 = t.Points.Last(p => p.Waypoint == 4).JointsDeg[5];
        Assert.InRange(Math.Abs(arm2 - arm1), 80, 100);   // 세로 팔에서 J6 가 약 90° 돈다

        var far = cross.Append(new ScanWaypoint(205, 3000, 29.8, 0, 0, -90)).ToArray();
        var blocked = ReachSimulator.Traverse(Field, Station, 0, pts[0], pts[2], Normal, seed, waypoints: far);
        Assert.False(blocked.Ok);
        Assert.Contains("경유점 #7", blocked.Blocker);
    }

    [Fact]
    public void 용접선별_프로필이_있으면_그것으로_없으면_종전대로_판정한다()
    {
        var pts = Seam(1);
        var other = Seam(2);
        var seed = StartSeed(Field, pts);
        var waypoints = new[] { new ScanWaypoint(0, 0, 33.8, 0, 0, 0), new ScanWaypoint(360, 0, 33.8, 0, 0, 0) };
        var m = Field with { SeamProfile = s => ReferenceEquals(s, pts) ? waypoints : null };

        var withProfile = ReachSimulator.TraverseSeam(m, Station, 0, pts, Normal, seed)!;
        Assert.All(withProfile.Points, p => Assert.NotNull(p.Waypoint));

        var without = ReachSimulator.TraverseSeam(m, Station, 0, other, Normal, StartSeed(Field, other))!;
        Assert.All(without.Points, p => Assert.Null(p.Waypoint));   // 포괄 근사
    }

    [Fact]
    public void 회전_사이_각과_축()
    {
        var (axis, angle) = ReachSimulator.RotBetween(ReachSimulator.RotZyx(0, 0, 0), ReachSimulator.RotZyx(0, 0, -90));
        Assert.Equal(90, angle, 9);
        Assert.Equal(-1, axis[2], 9);
        Assert.Equal(0, ReachSimulator.RotBetween(ReachSimulator.RotZyx(5, 10, 20), ReachSimulator.RotZyx(5, 10, 20)).AngleDeg, 9);
        Assert.Equal(180, ReachSimulator.RotBetween(ReachSimulator.RotZyx(0, 0, 0), ReachSimulator.RotZyx(0, 0, 180)).AngleDeg, 6);
    }
}
