using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>영역(Area) 기하 단위 테스트 [PHASE2 개정].</summary>
public class AreaGeometryTests
{
    /// <summary>디폴트 정차 pose = 영역 중앙 + heading atan2(−ny,−nx)(벽면 바라봄).</summary>
    [Fact]
    public void DefaultStationPose_CenterAndFacingWall()
    {
        // 영역 [3,-0.5]-[4,0.5], 법선 (0,-1) → 중앙 (3.5,0), heading atan2(1,0)=π/2
        var (x, y, theta) = AreaGeometry.DefaultStationPose(3.0, -0.5, 4.0, 0.5, 0, -1);
        Assert.Equal(3.5, x, 6);
        Assert.Equal(0.0, y, 6);
        Assert.Equal(Math.PI / 2, theta, 6);

        // 법선 (1,0) → 벽면(−x) 방향 heading = ±π (동일 각), 중앙 (1,1)
        var (x2, y2, t2) = AreaGeometry.DefaultStationPose(0, 0, 2, 2, 1, 0);
        Assert.Equal(1.0, x2, 6);
        Assert.Equal(1.0, y2, 6);
        Assert.Equal(Math.PI, Math.Abs(t2), 6);
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
}
