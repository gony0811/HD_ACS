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
    public void SideWall_TallV_Passes_ButNotBeyond29_AndNotOnFloor()
    {
        Assert.Null(AreaRules.ValidateCorners(Rect(1, 0.5, 2.44, 3.4), "PM", 45, 5.4, out _));        // v 2.9
        Assert.Contains("v 2.9m", AreaRules.ValidateCorners(Rect(1, 0.5, 2.44, 3.5), "PM", 45, 5.4, out _));
        Assert.Contains("각 1.44m", AreaRules.ValidateCorners(Rect(1, 0.5, 2.44, 2.5), "B", 45, 10, out _)); // 바닥은 v 도 수평
        Assert.Contains("각 1.44m", AreaRules.ValidateCorners(Rect(1, 0.5, 2.44, 2.5), "F", 17, 12, out _)); // 격벽
    }

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

    [Fact]
    public void OrderByLine_HorizontalByLineThenU_ThenVerticalByLineThenV_ObliqueLast()
    {
        // 담은 순서는 뒤섞여 있다 — 세로 u 6.30 라인과 5.94 라인이 번갈아, 가로·사선이 중간에
        var v630a = new AreaTaskEntity { Name = "V03", StartU = 6.30, StartV = 0.5, EndU = 6.30, EndV = 0.8 };
        var oblique = new AreaTaskEntity { Name = "사선", StartU = 1.0, StartV = 1.0, EndU = 1.2, EndV = 1.3 };
        var v594b = new AreaTaskEntity { Name = "V04", StartU = 5.94, StartV = 0.8, EndU = 5.94, EndV = 0.5 };   // 역방향도 min 기준
        var hHigh = new AreaTaskEntity { Name = "H07", StartU = 5.0, StartV = 0.9, EndU = 5.5, EndV = 0.9 };
        var v594a = new AreaTaskEntity { Name = "V05", StartU = 5.94, StartV = 0.2, EndU = 5.94, EndV = 0.4 };
        var hLowRight = new AreaTaskEntity { Name = "내 이름", StartU = 6.0, StartV = 0.1, EndU = 6.4, EndV = 0.1 };
        var hLowLeft = new AreaTaskEntity { Name = "H09", StartU = 5.5, StartV = 0.1, EndU = 5.0, EndV = 0.1 };
        var v630b = new AreaTaskEntity { Name = "V10", StartU = 6.30, StartV = 0.1, EndU = 6.30, EndV = 0.3 };

        var ordered = AreaRules.OrderByLine(new[] { v630a, oblique, v594b, hHigh, v594a, hLowRight, hLowLeft, v630b });

        Assert.Equal(new[] { hLowLeft, hLowRight, hHigh, v594a, v594b, v630b, v630a, oblique }, ordered);
        Assert.Equal(new[] { "H01", "내 이름", "H03", "V04", "V05", "V06", "V07", "사선" },
            ordered.Select((t, i) => AreaRules.RenameForSeq(t, i + 1)));
    }

    [Fact]
    public void AutoCornersForTasks_EnclosesTasks_AndPassesValidationAndLevelDerivation()
    {
        var g = new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 30, WFloor = 10, ThetaLow = Math.PI / 4, HLow = 1.9, HWall = 8.2, ThetaUp = Math.PI / 4, HUp = 2.9,
            LevelZ = "[0,3.2,6.4,9.6]", OriginOx = 0, OriginOy = 0,
        };
        var wall = new WallEntity
        {
            TankId = "CT1", WallCode = "PM", Origin = "[0,6,1.9]", VAxis = "[0,0,1]",
            ULen = 45, VLen = 5.4,
        };
        // 수평 단일 용접선 (v=3.0 동일) + 인접 용접선 1개
        var tasks = new[]
        {
            new AreaTaskEntity { Seq = 1, StartU = 3.2, StartV = 3.0, EndU = 3.8, EndV = 3.0 },
            new AreaTaskEntity { Seq = 2, StartU = 3.3, StartV = 3.0, EndU = 3.7, EndV = 3.4 },
        };

        var corners = AreaRules.AutoCornersForTasks(tasks, wall, g, targetLevel: 2);

        Assert.Null(AreaRules.ValidateCorners(corners, wall.WallCode, wall.ULen, wall.VLen, out var bb));
        Assert.Empty(AreaRules.TasksOutside(corners, tasks));
        Assert.Equal(2, AreaRules.DeriveLevel(g, wall, bb.MinV, bb.MaxV, out _));
        Assert.Equal(3.15, bb.MinU, 6);
        Assert.Equal(3.85, bb.MaxU, 6);
        Assert.Equal(2.95, bb.MinV, 6);
        Assert.Equal(3.45, bb.MaxV, 6);
    }
}
