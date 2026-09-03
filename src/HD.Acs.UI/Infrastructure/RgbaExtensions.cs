using System.Windows.Media;
using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Infrastructure;

/// <summary>코어 중립 색(Rgba) → WPF Color/Brush 변환. 상태색 소비처(컨버터·3D material)가 공유하는 단일 지점.</summary>
public static class RgbaExtensions
{
    public static Color ToMediaColor(this Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Freeze된 SolidColorBrush(바인딩·3D material 공용).</summary>
    public static SolidColorBrush ToBrush(this Rgba c)
    {
        var brush = new SolidColorBrush(c.ToMediaColor());
        brush.Freeze();
        return brush;
    }
}
