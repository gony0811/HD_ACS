using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// CROSS3/CROSS4 교차 형상의 AMR 면-로컬 프레임 재구성·회전 유도·점 정렬 [VDA §8.5.1, N13 확정안 2026-10-07].
/// 핵심 검증: ACS 저장 면 축(전개도 프레임)과 AMR 프레임의 손잡이 차이를 함수가 흡수하는가.
/// </summary>
public class CrossGeometryTests
{
    private static TankGeometry Sample() => new(
        L: 30, WFloor: 10, ThetaLow: Math.PI / 4, HLow: 3,
        HWall: 8, ThetaUp: Math.PI / 4, HUp: 2, LevelZ: new[] { 0.0, 3.2, 6.4, 9.6 });

    private static Dictionary<string, GeneratedWall> Walls() => Sample().GenerateWalls().ToDictionary(w => w.WallCode);

    private static double Dot(double[] a, double[] b) => Vec3.Dot(a, b);
    private static void AssertUnit(double[] a) => Assert.Equal(1.0, Vec3.Length(a), 9);

    // ── 프레임 재구성: 수직·챔퍼·마구리 8면 전부 정규직교·오른손(u×v=n)·u 수평 ──
    [Theory]
    [InlineData("SL")] [InlineData("PL")] [InlineData("SM")] [InlineData("PM")]
    [InlineData("SU")] [InlineData("PU")] [InlineData("F")] [InlineData("A")]
    public void FromNormal_ReconstructsOrthonormalRightHandedFrame(string code)
    {
        var w = Walls()[code];
        var f = AmrFaceFrame.FromNormal(w.Normal);
        Assert.NotNull(f);

        AssertUnit(f!.U); AssertUnit(f.V); AssertUnit(f.N);
        Assert.Equal(0, Dot(f.U, f.V), 9);           // u ⟂ v
        Assert.Equal(0, Dot(f.U, f.N), 9);           // u ⟂ n
        Assert.Equal(0, Dot(f.V, f.N), 9);           // v ⟂ n
        Assert.Equal(0, f.U[2], 9);                  // u 는 수평(z=0)

        // 오른손 프레임: u × v = n
        var uxv = Vec3.Cross(f.U, f.V);
        Assert.Equal(f.N[0], uxv[0], 9);
        Assert.Equal(f.N[1], uxv[1], 9);
        Assert.Equal(f.N[2], uxv[2], 9);

        // u = (−sinθ, cosθ), θ = FacingYaw(벽 정면)
        double th = w.FacingYaw!.Value;
        Assert.Equal(-Math.Sin(th), f.U[0], 9);
        Assert.Equal(Math.Cos(th), f.U[1], 9);
    }

    // ── 바닥·천장은 수평 법선이 없어 AMR u 미정의 → null (B/T CROSS 범위 밖) ──
    [Theory]
    [InlineData("B")] [InlineData("T")]
    public void FromNormal_FloorCeiling_Null(string code) =>
        Assert.Null(AmrFaceFrame.FromNormal(Walls()[code].Normal));

    // ── 손잡이 흡수(핵심): 같은 "전개도 +u(뱃머리)" 줄기가 우현=R0, 좌현=R180 으로 갈린다 ──
    // SM(우현): ACS U == AMR u → 뱃머리 줄기 = +u → R0.
    // PM(좌현): ACS U == −AMR u(거울) → 뱃머리 줄기 = −u → R180. (ACS 저장축으로 계산하면 둘 다 R0=오답)
    [Theory]
    [InlineData("SM", "CROSS3_R0")]
    [InlineData("PM", "CROSS3_R180")]
    [InlineData("SL", "CROSS3_R0")]
    [InlineData("PL", "CROSS3_R180")]
    public void MirrorAbsorbed_StemAlongDrawingU(string code, string expected)
    {
        var w = Walls()[code];
        var f = AmrFaceFrame.FromNormal(w.Normal)!;
        // 운영자가 전개도에서 줄기를 +u_acs(뱃머리) 방향으로 그림
        var (su, sv) = f.FromAcsDir(1.0, 0.0, w.Pose.U, w.Pose.V);
        var rot = CrossGeometry.Cross3Rotation(su, sv, out double resid);
        Assert.Equal(expected, rot);
        Assert.True(resid < 1e-6);
    }

    // ── 마구리도 거울: F/A 는 ACS U=∓y, AMR u=±y(반대) ──
    [Theory]
    [InlineData("F")] [InlineData("A")]
    public void MirrorAbsorbed_Bulkhead(string code)
    {
        var w = Walls()[code];
        var f = AmrFaceFrame.FromNormal(w.Normal)!;
        var (su, sv) = f.FromAcsDir(1.0, 0.0, w.Pose.U, w.Pose.V);   // +u_acs
        // ACS U 와 AMR u 가 반대이므로 전개도 +u 줄기는 AMR −u = R180
        Assert.Equal("CROSS3_R180", CrossGeometry.Cross3Rotation(su, sv, out _));
    }

    // ── 회전 유도: 순수 AMR 성분 스냅 ──
    [Theory]
    [InlineData(1.0, 0.0, "CROSS3_R0")]
    [InlineData(0.0, 1.0, "CROSS3_R90")]
    [InlineData(-1.0, 0.0, "CROSS3_R180")]
    [InlineData(0.0, -1.0, "CROSS3_R270")]
    [InlineData(0.98, 0.17, "CROSS3_R0")]      // +10° → R0 로 스냅
    [InlineData(0.17, 0.98, "CROSS3_R90")]     // 80° → R90
    public void Cross3Rotation_Snaps(double su, double sv, string expected)
    {
        Assert.Equal(expected, CrossGeometry.Cross3Rotation(su, sv, out double resid));
        Assert.True(resid <= 10.5);
    }

    [Fact]
    public void Cross3Rotation_DegenerateStem_Null() =>
        Assert.Null(CrossGeometry.Cross3Rotation(0, 0, out _));

    // ── CROSS4 점 순서: 입력이 뒤섞여도 +u→+v→−u→−v (CCW) ──
    [Fact]
    public void OrderCcw_Cross4_PlusU_PlusV_MinusU_MinusV()
    {
        var shuffled = new (double U, double V)[] { (0, -0.5), (-0.4, 0), (0.6, 0), (0, 0.3) };
        var ordered = CrossGeometry.OrderCcw(shuffled);
        Assert.Equal(new[] { 0.6, 0.0, -0.4, 0.0 }, ordered.Select(p => p.U));   // +u, +v, −u, −v
        Assert.Equal(new[] { 0.0, 0.3, 0.0, -0.5 }, ordered.Select(p => p.V));
    }

    // ── 길이 보존: arm 길이가 프레임 변환에서 불변 ──
    [Fact]
    public void FromAcsDir_PreservesLength()
    {
        var w = Walls()["PM"];
        var f = AmrFaceFrame.FromNormal(w.Normal)!;
        var (au, av) = f.FromAcsDir(0.3, -0.4, w.Pose.U, w.Pose.V);
        Assert.Equal(0.5, Math.Sqrt(au * au + av * av), 9);   // 0.3,0.4,0.5 삼각형
    }

    // ── params.points 빌드: CROSS4 (SM) → AMR mm·CCW(+u→+v→−u→−v), 거울·단위 환산 흡수 ──
    [Fact]
    public void BuildAmrPoints_Cross4_OrdersCcw()
    {
        var w = Walls()["SM"];
        var arms = new[] { new[] { 0.5, 0.0 }, new[] { 0.0, 0.5 }, new[] { -0.5, 0.0 }, new[] { 0.0, -0.5 } };
        var pts = CrossGeometry.BuildAmrPoints("CROSS4", 0, 0, arms, w.Pose.U, w.Pose.V, w.Normal);
        Assert.NotNull(pts);
        Assert.Equal(4, pts!.Length);
        AssertPt(pts[0], 500, 0); AssertPt(pts[1], 0, 500); AssertPt(pts[2], -500, 0); AssertPt(pts[3], 0, -500);
    }

    // ── params.points 빌드: CROSS3 (PM 거울면) → [줄기, +통과, −통과], mm, AMR 프레임 ──
    [Fact]
    public void BuildAmrPoints_Cross3_StemFirst_MirrorFace()
    {
        var w = Walls()["PM"];
        var arms = new[] { new[] { 0.3, 0.0 }, new[] { 0.0, 0.3 }, new[] { 0.0, -0.3 } };   // [0]=줄기(+u_acs)
        var pts = CrossGeometry.BuildAmrPoints("CROSS3_R180", 0, 0, arms, w.Pose.U, w.Pose.V, w.Normal);
        Assert.NotNull(pts);
        Assert.Equal(3, pts!.Length);
        AssertPt(pts[0], -300, 0);   // 줄기 = AMR −u (거울 흡수 → 저장 seamType R180 과 정합)
        AssertPt(pts[1], 0, -300);   // 통과선 +(줄기+90° CCW)
        AssertPt(pts[2], 0, 300);    // 통과선 −
    }

    [Theory]
    [InlineData("LINE")] [InlineData("CORNER3")] [InlineData(null)]
    public void BuildAmrPoints_NonCross_Null(string? st)
    {
        var w = Walls()["SM"];
        Assert.Null(CrossGeometry.BuildAmrPoints(st, 0, 0, new[] { new[] { 0.1, 0.0 } }, w.Pose.U, w.Pose.V, w.Normal));
    }

    [Fact]
    public void BuildAmrPoints_FloorCeiling_Null()
    {
        var w = Walls()["B"];
        Assert.Null(CrossGeometry.BuildAmrPoints("CROSS4", 0, 0, new[] { new[] { 0.5, 0.0 } }, w.Pose.U, w.Pose.V, w.Normal));
    }

    private static void AssertPt(double[] p, double u, double v) { Assert.Equal(u, p[0], 6); Assert.Equal(v, p[1], 6); }

    // ── Preview: /cross-preview 엔드포인트 정본 (회전 유도 + 점 정렬 한 번에) ──
    [Theory]
    [InlineData("SM", "CROSS3_R0")]    // 우현: +u_acs 줄기 = +u_amr → R0
    [InlineData("PM", "CROSS3_R180")]  // 좌현(거울): +u_acs 줄기 = −u_amr → R180
    public void Preview_Cross3_DerivesRotation(string code, string expected)
    {
        var w = Walls()[code];
        var arms = new[] { new[] { 0.3, 0.0 } /*줄기 +u_acs*/, new[] { 0.0, 0.3 }, new[] { 0.0, -0.3 } };
        var r = CrossGeometry.Preview("CROSS3", 0, 0, arms, w.Pose.U, w.Pose.V, w.Normal);
        Assert.True(r.FrameOk);
        Assert.Equal(expected, r.SeamType);
        Assert.Equal(3, r.Points!.Length);
        Assert.True(r.SnapResidualDeg < 1e-6);
    }

    [Fact]
    public void Preview_Cross4_KeepsType_OrdersPoints()
    {
        var w = Walls()["SM"];
        var arms = new[] { new[] { 0.5, 0.0 }, new[] { 0.0, 0.5 }, new[] { -0.5, 0.0 }, new[] { 0.0, -0.5 } };
        var r = CrossGeometry.Preview("CROSS4", 0, 0, arms, w.Pose.U, w.Pose.V, w.Normal);
        Assert.True(r.FrameOk);
        Assert.Equal("CROSS4", r.SeamType);
        Assert.Equal(4, r.Points!.Length);
        AssertPt(r.Points[0], 500, 0);   // +u
    }

    [Fact]
    public void Preview_FloorCeiling_FrameNotOk()
    {
        var w = Walls()["B"];
        var r = CrossGeometry.Preview("CROSS3", 0, 0, new[] { new[] { 0.3, 0.0 }, new[] { 0.0, 0.3 }, new[] { 0.0, -0.3 } },
            w.Pose.U, w.Pose.V, w.Normal);
        Assert.False(r.FrameOk);
        Assert.Null(r.Points);
    }
}
