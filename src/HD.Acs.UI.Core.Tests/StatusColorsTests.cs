using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>상태 → 색 매핑 골든(전개도 컨버터·3D material·상태 배지가 공유하는 단일 소스).</summary>
public class StatusColorsTests
{
    [Theory]
    [InlineData("PENDING", "#26808A94", "#FF808A94")]
    [InlineData("PLANNED", "#26808A94", "#FF808A94")]
    [InlineData("WAITING", "#26808A94", "#FF808A94")]
    [InlineData("DISPATCHED", "#382E86DE", "#FF2E86DE")]
    [InlineData("RUNNING", "#382E86DE", "#FF2E86DE")]
    [InlineData("DONE", "#4027AE60", "#FF2ECC71")]
    [InlineData("FINISHED", "#4027AE60", "#FF2ECC71")]
    [InlineData("FAILED", "#38E74C3C", "#FFE74C3C")]
    [InlineData("SKIPPED", "#38E74C3C", "#FFE74C3C")]
    [InlineData(null, "#2627AE60", "#FF229954")]      // 계획(상태 없음)=녹
    [InlineData("UNKNOWN", "#2627AE60", "#FF229954")]
    public void StatusColors_MapsStatusToFillAndLine(string? status, string fill, string line)
    {
        var (f, l) = TankViewModel.StatusColors(status);
        Assert.Equal(fill, f.ToString());
        Assert.Equal(line, l.ToString());
    }

    [Fact]
    public void WeldLineColor_NullIsPlanningOrange_OtherwiseStatusLine()
    {
        Assert.Equal(Rgba.FromRgb(0xE6, 0x7E, 0x22), TankViewModel.WeldLineColor(null));
        Assert.Equal(TankViewModel.StatusColors("RUNNING").Line, TankViewModel.WeldLineColor("RUNNING"));
        Assert.Equal(TankViewModel.StatusColors("FAILED").Line, TankViewModel.WeldLineColor("FAILED"));
    }
}
