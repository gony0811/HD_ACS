using HD.Acs.UI.Models;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>화면 쪽 T_W_D 계산(CalTransform) — 강체는 종전 식과 같고, affine 은 서버가 준 선형부를 그대로 쓴다.</summary>
public class CalTransformTests
{
    [Fact]
    public void Rigid_MatchesPreviousFormula()
    {
        var t = new CalTransform(16.0, 9.0, -3.13);
        var (x, y) = t.MapToDrawing(20.0, 12.0);
        double cos = Math.Cos(-3.13), sin = Math.Sin(-3.13);
        Assert.Equal(cos * 4 + sin * 3, x, 12);
        Assert.Equal(-sin * 4 + cos * 3, y, 12);
        Assert.Equal(TankViewModel.MapThetaToDrawing(1.2, -3.13), t.MapThetaToDrawing(1.2), 12);
        Assert.False(t.IsAffine);
    }

    [Fact]
    public void Affine_RoundTrips_AndCalibrationDtoExposesIt()
    {
        var dto = new MapCalibrationDto("CT1-L3", 1, 16.3186, 9.6428, 0.004, 0.04, 5, null, DateTimeOffset.UtcNow,
            A11: 1.0116, A12: 0.0010, A21: 0.0092, A22: 1.0169);
        Assert.True(dto.IsAffine);
        var t = dto.Transform;

        var (mx, my) = t.DrawingToMap(-7.38, -6.85);
        var (dx, dy) = t.MapToDrawing(mx, my);
        Assert.Equal(-7.38, dx, 9);
        Assert.Equal(-6.85, dy, 9);
        // 현장 점: 맵 (8.820, 2.596) → 도면 (−7.38, −6.85) 근처
        var (fx, fy) = t.MapToDrawing(8.820, 2.596);
        Assert.InRange(Math.Sqrt(Math.Pow(fx + 7.38, 2) + Math.Pow(fy + 6.85, 2)), 0, 0.04);
        // 방향: 축척만 조금 다른 변환이라 heading 은 1° 안에서 그대로
        Assert.InRange(Math.Abs(t.MapThetaToDrawing(0.0)), 0, 0.02);

        var (px, py, head) = CalibrationPreview.ToDrawing(8.820, 2.596, 0.0, t);
        Assert.Equal(fx, px, 12); Assert.Equal(fy, py, 12); Assert.NotNull(head);
    }

    [Fact]
    public void PartialLinearValues_FallBackToRigid()
    {
        var t = new CalTransform(1, 2, 0.5, A11: 1.01);
        Assert.False(t.IsAffine);
        Assert.Equal(new CalTransform(1, 2, 0.5).MapToDrawing(3, 4), t.MapToDrawing(3, 4));
    }
}
