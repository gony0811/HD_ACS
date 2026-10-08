using HD.Acs.App.Services;
using HD.Acs.Data.Entities;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>검사 영역 등록(POST)·수정(PUT) 공통 검증 규칙.</summary>
public class AreaRulesTests
{
    private static double[][] Rect(double u0, double v0, double u1, double v1) =>
        new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } };

    [Fact]
    public void ValidRect_Passes_WithBbox()
    {
        Assert.Null(AreaRules.ValidateCorners(Rect(1, 2, 2.4, 3), "PM", 45, 5.4, out var bb));
        Assert.Equal((1.0, 2.0, 2.4, 3.0), bb);
    }

    [Fact]
    public void CornerOutsideFace_Rejected() =>
        Assert.Contains("면 범위", AreaRules.ValidateCorners(Rect(44, 1, 45.5, 2), "PM", 45, 5.4, out _));

    [Fact]
    public void Degenerate_Rejected() =>
        Assert.Contains("퇴화", AreaRules.ValidateCorners(Rect(1, 1, 2, 1), "PM", 45, 5.4, out _));

    [Fact]
    public void TooLarge_Rejected() =>
        Assert.Contains("1.44m", AreaRules.ValidateCorners(Rect(1, 1, 3, 2), "PM", 45, 5.4, out _));

    [Fact]
    public void TooFewCorners_Rejected() =>
        Assert.Contains("3점 미만", AreaRules.ValidateCorners(new[] { new[] { 1.0, 1 }, new[] { 2.0, 2 } }, "PM", 45, 5.4, out _));

    [Fact]
    public void NonFinite_Rejected() =>
        Assert.Contains("유한한", AreaRules.ValidateCorners(Rect(double.NaN, 1, 2, 2), "PM", 45, 5.4, out _));

    [Fact]
    public void TasksOutside_ListsOnlyEscapingSeqs()
    {
        var poly = Rect(0, 0, 1, 1);
        var tasks = new[]
        {
            new AreaTaskEntity { Seq = 1, StartU = 0.1, StartV = 0.5, EndU = 0.9, EndV = 0.5 },   // 내부
            new AreaTaskEntity { Seq = 3, StartU = 0.1, StartV = 0.5, EndU = 1.2, EndV = 0.5 },   // 끝점 밖
            new AreaTaskEntity { Seq = 2, StartU = -0.1, StartV = 0.5, EndU = 0.5, EndV = 0.5 },  // 시작점 밖
        };
        Assert.Equal(new[] { 2, 3 }, AreaRules.TasksOutside(poly, tasks));
    }

    [Fact]
    public void DeriveLevel_UsesWallPoseAndBands()
    {
        // 수직 측벽(PM): origin z=1.9, vAxis=+z. level_z [0,3.2,6.4,9.6], H=13.
        var g = new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 30, WFloor = 10, ThetaLow = Math.PI / 4, HLow = 1.9, HWall = 8.2, ThetaUp = Math.PI / 4, HUp = 2.9,
            LevelZ = "[0,3.2,6.4,9.6]", OriginOx = 0, OriginOy = 0,
        };
        var wall = new WallEntity { TankId = "CT1", WallCode = "PM", Origin = "[0,6,1.9]", VAxis = "[0,0,1]" };

        Assert.Equal(2, AreaRules.DeriveLevel(g, wall, 2.0, 3.0, out _));     // z 3.9~4.9 → L2
        Assert.Null(AreaRules.DeriveLevel(g, wall, 0.5, 3.0, out var reason)); // z 2.4~4.9 → 층 경계 걸침
        Assert.NotNull(reason);
    }
}
