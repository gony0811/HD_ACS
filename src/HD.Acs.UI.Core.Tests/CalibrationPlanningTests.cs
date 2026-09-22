using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

public class CalibrationPlanningTests
{
    [Fact]
    public void CanvasCenter_IsDrawingOrigin()
    {
        var p = CalibrationPlanning.CanvasToDrawing(260, 260, 520, 28, -15.5, 15.5, -8.65, 8.65);

        Assert.Equal(0, p.X, 6);
        Assert.Equal(0, p.Y, 6);
    }

    [Fact]
    public void CanvasAxes_FollowForeAndPortConvention()
    {
        var topRight = CalibrationPlanning.CanvasToDrawing(492, 28, 520, 28, -15.5, 15.5, -8.65, 8.65);

        Assert.Equal(15.5, topRight.X, 6); // 우측 = 선수 = +X
        Assert.Equal(8.65, topRight.Y, 6); // 상단 = 좌현 = +Y
    }

    [Fact]
    public void Layout_RequiresThreePointsSpreadAcrossBothAxes()
    {
        Assert.False(CalibrationPlanning.IsWellSpread(
            new[] { (-12d, 0d), (0d, 0d), (12d, 0d) }, -15.5, 15.5, -8.65, 8.65));
        Assert.True(CalibrationPlanning.IsWellSpread(
            new[] { (-12d, -6d), (0d, 5d), (12d, -4d) }, -15.5, 15.5, -8.65, 8.65));
    }
}
