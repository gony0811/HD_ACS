using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>선창 파라메트릭 정의 + 면 자동생성 단위 테스트 [SPEC v3 §2/§3].</summary>
public class TankGeometryTests
{
    // 예시 선창: L=30, w_floor=10, θ=45°, h_low=3, h_wall=8, h_up=2 → B=16, W_ceil=12, H=13
    private static TankGeometry Sample() => new(
        L: 30, WFloor: 10, ThetaLow: Math.PI / 4, HLow: 3,
        HWall: 8, ThetaUp: Math.PI / 4, HUp: 2,
        LevelZ: new[] { 0.0, 3.2, 6.4, 9.6 });

    private static Dictionary<string, GeneratedWall> Walls() => Sample().GenerateWalls().ToDictionary(w => w.WallCode);

    [Fact]
    public void Derived_Values()
    {
        var d = Sample().Derived();
        Assert.Equal(3.0, d.WLow, 6);
        Assert.Equal(16.0, d.B, 6);
        Assert.Equal(2.0, d.WUp, 6);
        Assert.Equal(12.0, d.WCeil, 6);
        Assert.Equal(13.0, d.H, 6);
    }

    [Fact]
    public void TenWalls_Generated()
    {
        var w = Walls();
        Assert.Equal(10, w.Count);
        foreach (var code in new[] { "B", "SL", "PL", "SM", "PM", "SU", "PU", "T", "F", "A" })
            Assert.True(w.ContainsKey(code), $"면 {code} 누락");
    }

    [Fact]
    public void EveryFrame_IsOrthonormal_WithConsistentNormal()
    {
        foreach (var w in Walls().Values)
        {
            Assert.Equal(1.0, Vec3.Length(w.Pose.U), 9);
            Assert.Equal(1.0, Vec3.Length(w.Pose.V), 9);
            Assert.Equal(1.0, Vec3.Length(w.Normal), 9);
            Assert.Equal(0.0, Vec3.Dot(w.Pose.U, w.Pose.V), 9);   // U⟂V
            Assert.Equal(0.0, Vec3.Dot(w.Normal, w.Pose.U), 9);   // N⟂U
            Assert.Equal(0.0, Vec3.Dot(w.Normal, w.Pose.V), 9);   // N⟂V
            // N은 U×V 와 평행(부호는 면별) — |N·(U×V)|=1
            Assert.Equal(1.0, Math.Abs(Vec3.Dot(w.Normal, Vec3.Cross(w.Pose.U, w.Pose.V))), 9);
        }
    }

    [Fact]
    public void AdjacentFaces_ShareEdges()
    {
        var w = Walls();
        // 바닥 SL 하단 = B의 −y 가장자리 (y=−5, z=0)
        AssertVec(w["SL"].To3D(0, 0), -15, -5, 0);
        // SL 상단 = SM 하단 (−B/2=−8, z=h_low=3)
        AssertVec(w["SL"].To3D(0, w["SL"].VLen), -15, -8, 3);
        AssertVec(w["SM"].To3D(0, 0), -15, -8, 3);
        // SM 상단 = SU 하단 (z=h_low+h_wall=11)
        AssertVec(w["SM"].To3D(0, w["SM"].VLen), -15, -8, 11);
        AssertVec(w["SU"].To3D(0, 0), -15, -8, 11);
        // SU 상단 = 천장 T의 −y 가장자리 (−W_ceil/2=−6, z=H=13)
        AssertVec(w["SU"].To3D(0, w["SU"].VLen), -15, -6, 13);
        AssertVec(w["T"].To3D(0, 0), -15, -6, 13);
    }

    [Fact]
    public void To3D_MapsLocalToGlobal()
    {
        var sm = Walls()["SM"];   // 수직벽 우현: origin (−15,−8,3), U=+x, V=+z
        AssertVec(sm.To3D(0, 0), -15, -8, 3);
        AssertVec(sm.To3D(30, 8), 15, -8, 11);   // u=길이끝, v=벽높이끝
    }

    [Fact]
    public void FacingYaw_PerFace()
    {
        var w = Walls();
        Assert.Null(w["B"].FacingYaw);            // 바닥 — 수평법선 없음
        Assert.Null(w["T"].FacingYaw);            // 천장 — 수평법선 없음
        Assert.Equal(-Math.PI / 2, w["SM"].FacingYaw!.Value, 6);   // 우현 벽: 법선 +y → 바라봄 −y
        Assert.Equal(Math.PI / 2, w["PM"].FacingYaw!.Value, 6);    // 좌현 벽
        Assert.Equal(0.0, w["F"].FacingYaw!.Value, 6);            // 선수(x=+L/2): 법선 −x → 바라봄 +x
        Assert.Equal(Math.PI, Math.Abs(w["A"].FacingYaw!.Value), 6); // 선미(x=−L/2)
    }

    [Fact]
    public void Validate_Passes_ForSample()
    {
        Assert.Empty(Sample().Validate(checkHTotal: 13.0, checkBeam: 16.0, checkWCeil: 12.0));
    }

    [Fact]
    public void Validate_Fails_OnBadDimensions()
    {
        // 상부 챔퍼 과대 → W_ceil ≤ 0
        var bad = new TankGeometry(30, 10, Math.PI / 4, 3, 8, Math.PI / 4, 20, new[] { 0.0 });
        Assert.NotEmpty(bad.Validate(null, null, null));
        // level_z 비오름차순
        var badLz = Sample() with { LevelZ = new[] { 0.0, 6.0, 3.0 } };
        Assert.NotEmpty(badLz.Validate(null, null, null));
        // 검증치수 불일치 (H=13인데 15로 대조)
        Assert.NotEmpty(Sample().Validate(checkHTotal: 15.0, checkBeam: null, checkWCeil: null));
        // 각도 범위 밖
        var badAng = Sample() with { ThetaLow = 0 };
        Assert.NotEmpty(badAng.Validate(null, null, null));
    }

    private static void AssertVec(double[] p, double x, double y, double z)
    {
        Assert.Equal(x, p[0], 6);
        Assert.Equal(y, p[1], 6);
        Assert.Equal(z, p[2], 6);
    }
}
