using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>영역(Area) 기하 단위 테스트 [PHASE2 개정].</summary>
public class AreaGeometryTests
{
    /// <summary>standoff 정차점 — 수직벽: 중심 투영에서 내부향 법선 방향으로 standoff만큼 이격. [standoff 정차]</summary>
    [Fact]
    public void StationDrawing_OffsetsAlongHorizontalNormal()
    {
        // 벽면: origin (10,0,0), U=+x, V=+z (y=0 평면의 수직벽), 내부향 법선 = +y
        var pose = new WallPose(new[] { 10.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 });
        var st = AreaGeometry.StationDrawing(pose, 2.0, 1.5, new[] { 0.0, 1.0, 0.0 }, 0.8);

        Assert.Equal(12.0, st[0], 6);   // x = origin.x + u
        Assert.Equal(0.8, st[1], 6);    // y = 0 + 법선(+y) × standoff
        Assert.Equal(1.5, st[2], 6);    // z = v (참고값)
    }

    /// <summary>기울어진 챔퍼면 법선은 수평 성분만 정규화해 사용 — 이격 거리는 바닥 평면에서 standoff. [standoff 정차]</summary>
    [Fact]
    public void StationDrawing_NormalizesTiltedNormalHorizontally()
    {
        var pose = new WallPose(new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, -0.7071, 0.7071 });
        // 챔퍼 법선 (0, 0.7071, 0.7071) → 수평 성분 정규화 = (0,1,0)
        var st = AreaGeometry.StationDrawing(pose, 0.0, 0.0, new[] { 0.0, 0.7071, 0.7071 }, 1.0);

        Assert.Equal(0.0, st[0], 6);
        Assert.Equal(1.0, st[1], 6);    // 수평 이격이 정확히 standoff (성분 그대로면 0.7071이 됨)
    }

    /// <summary>바닥/천장(법선 수직) 또는 standoff≤0 — 이격 없이 중심 투영 폴백. [standoff 정차]</summary>
    [Fact]
    public void StationDrawing_FallsBackForVerticalNormalOrZeroStandoff()
    {
        var pose = new WallPose(new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 });
        var center = pose.LocalToDrawing(3.0, 2.0);

        var floor = AreaGeometry.StationDrawing(pose, 3.0, 2.0, new[] { 0.0, 0.0, 1.0 }, 0.8);   // 바닥 법선 +z
        Assert.Equal(center, floor);

        var zero = AreaGeometry.StationDrawing(pose, 3.0, 2.0, new[] { 0.0, 1.0, 0.0 }, 0.0);    // standoff 0
        Assert.Equal(center, zero);
    }

    /// <summary>영역 중앙 계산. [정차각 자동화]</summary>
    [Fact]
    public void AreaCenter_ReturnsRectangleMidpoint()
    {
        var (cx, cy) = AreaGeometry.AreaCenter(3.0, -0.5, 4.0, 0.5);
        Assert.Equal(3.5, cx, 6);
        Assert.Equal(0.0, cy, 6);
    }

    /// <summary>정차각 자동 = 정차 위치 → seam 점 중심 방향(도면 yaw). 다중 seam은 평균. [정차각 자동화]</summary>
    [Fact]
    public void FacingYawToward_PointsToSeamCentroid()
    {
        // 정차 (3.5, 0), seam 점들이 +y쪽(벽) → 중심 (3.5, 0.4) → yaw = atan2(0.4, 0) = π/2
        var yaw = AreaGeometry.FacingYawToward(3.5, 0.0,
            new List<double[]> { new[] { 3.2, 0.4, 1.4 }, new[] { 3.8, 0.4, 1.4 } });
        Assert.NotNull(yaw);
        Assert.Equal(Math.PI / 2, yaw!.Value, 6);

        // seam이 +x쪽 → yaw = 0
        var yaw2 = AreaGeometry.FacingYawToward(0.0, 0.0, new List<double[]> { new[] { 1.0, 0.0, 0.0 } });
        Assert.Equal(0.0, yaw2!.Value, 6);
    }

    /// <summary>정차 위치와 seam 중심이 일치하면 방향 불명 → null(degenerate). [정차각 자동화]</summary>
    [Fact]
    public void FacingYawToward_ReturnsNullWhenDegenerate()
    {
        var yaw = AreaGeometry.FacingYawToward(3.5, 0.0,
            new List<double[]> { new[] { 3.5, 0.0, 1.4 } });
        Assert.Null(yaw);
        Assert.Null(AreaGeometry.FacingYawToward(0, 0, new List<double[]>()));   // 타깃 없음
    }

    /// <summary>경계 판정 — 내부/경계는 true, 밖은 false.</summary>
    [Fact]
    public void InBounds_IncludesBoundaryRejectsOutside()
    {
        Assert.True(AreaGeometry.InBounds(3.5, 0, 3, -0.5, 4, 0.5));    // 내부
        Assert.True(AreaGeometry.InBounds(3.0, -0.5, 3, -0.5, 4, 0.5)); // 경계(포함)
        Assert.False(AreaGeometry.InBounds(5.0, 0, 3, -0.5, 4, 0.5));   // x 밖
        Assert.False(AreaGeometry.InBounds(3.5, 1.0, 3, -0.5, 4, 0.5)); // y 밖
    }

    // ── 임의 4점 사각형 기하 ─────────────────────────────
    private static double[][] Quad() => new[]
    {
        new[] { 0.0, 0.0 }, new[] { 4.0, 1.0 }, new[] { 3.0, 4.0 }, new[] { -1.0, 3.0 },   // 회전·비축정렬 사각형
    };

    [Fact]
    public void Bbox_And_Centroid()
    {
        var (minU, minV, maxU, maxV) = AreaGeometry.Bbox(Quad());
        Assert.Equal(-1.0, minU, 6); Assert.Equal(0.0, minV, 6);
        Assert.Equal(4.0, maxU, 6); Assert.Equal(4.0, maxV, 6);
        var (cu, cv) = AreaGeometry.Centroid(Quad());
        Assert.Equal(1.5, cu, 6); Assert.Equal(2.0, cv, 6);
    }

    [Fact]
    public void PointInPolygon_InsideBoundaryOutside()
    {
        var q = Quad();
        Assert.True(AreaGeometry.PointInPolygon(1.5, 2.0, q));    // 내부(centroid)
        Assert.True(AreaGeometry.PointInPolygon(0.0, 0.0, q));    // 꼭짓점(경계)
        Assert.True(AreaGeometry.PointInPolygon(2.0, 0.5, q));    // 변 위(0,0)-(4,1)
        Assert.False(AreaGeometry.PointInPolygon(4.0, 4.0, q));   // bbox 안이지만 다각형 밖
        Assert.False(AreaGeometry.PointInPolygon(10.0, 10.0, q)); // 완전 밖
    }
}
