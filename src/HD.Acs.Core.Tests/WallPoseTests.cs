using HD.Acs.Core.Geometry;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>3D 벡터 수학 + 벽면 pose 단위 테스트 [벽면-로컬 좌표 모델 Phase 1].</summary>
public class WallPoseTests
{
    [Fact]
    public void Vec3_CrossDotNormalize()
    {
        var c = Vec3.Cross(new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 });
        Assert.Equal(new[] { 0.0, 0, 1 }, c);
        Assert.Equal(0.0, Vec3.Dot(new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }), 12);
        var n = Vec3.Normalize(new[] { 3.0, 0, 4 });
        Assert.NotNull(n);
        Assert.Equal(0.6, n![0], 9); Assert.Equal(0.8, n[2], 9);
        Assert.Null(Vec3.Normalize(new[] { 0.0, 0, 0 }));   // 영벡터 가드
    }

    [Fact]
    public void LocalToDrawing_MapsUvToDrawing()
    {
        // 수직벽: 원점 (5,2,0), u=+x, v=+z → (u=2,v=1.4) → (7, 2, 1.4)
        var pose = new WallPose(new[] { 5.0, 2.0, 0.0 }, new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 });
        var p = pose.LocalToDrawing(2.0, 1.4);
        Assert.Equal(7.0, p[0], 9); Assert.Equal(2.0, p[1], 9); Assert.Equal(1.4, p[2], 9);
    }

    [Fact]
    public void Normal_And_HorizontalNormal()
    {
        // u=+x, v=+z → 법선 = u×v = (0,-1,0). 수평 성분 = (0,-1,0).
        var pose = new WallPose(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 });
        var n = pose.Normal();
        Assert.Equal(new[] { 0.0, -1, 0 }, new[] { System.Math.Round(n[0], 9), System.Math.Round(n[1], 9), System.Math.Round(n[2], 9) });
        var h = pose.HorizontalNormal();
        Assert.Equal(-1.0, h[1], 9); Assert.Equal(0.0, h[2], 9);
    }

    [Fact]
    public void FromThreePoints_OrthonormalizesAndHandlesNonPerpendicular()
    {
        // 비직교 3점: O=(0,0,0), PU=(2,0,0)→u=+x, PV=(1,0,3)(U성분 있음) → V는 직교화되어 +z
        var pose = WallPose.FromThreePoints(new[] { 0.0, 0, 0 }, new[] { 2.0, 0, 0 }, new[] { 1.0, 0, 3 });
        Assert.Equal(new[] { 1.0, 0, 0 }, new[] { System.Math.Round(pose.U[0], 9), System.Math.Round(pose.U[1], 9), System.Math.Round(pose.U[2], 9) });
        Assert.Equal(new[] { 0.0, 0, 1 }, new[] { System.Math.Round(pose.V[0], 9), System.Math.Round(pose.V[1], 9), System.Math.Round(pose.V[2], 9) });
        Assert.Equal(0.0, Vec3.Dot(pose.U, pose.V), 9);   // 직교
        Assert.Equal(1.0, Vec3.Length(pose.V), 9);        // 단위
    }

    [Fact]
    public void FromThreePoints_DegenerateThrows()
    {
        Assert.Throws<WallPoseInvalidException>(() =>
            WallPose.FromThreePoints(new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { 0, 0, 1.0 }));   // O==PU
        Assert.Throws<WallPoseInvalidException>(() =>
            WallPose.FromThreePoints(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 2.0, 0, 0 }));   // PV가 U축과 평행
    }
}
