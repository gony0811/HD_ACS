using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HD.Acs.UI.Infrastructure;

/// <summary>true→Collapsed, false→Visible (빈 상태 안내 표시용).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>true→Visible, false→Collapsed.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// 정규화 좌표(0~1)를 전개도 Canvas 픽셀로 변환. ConverterParameter="캔버스크기|박스크기"(예: "400|72")로
/// 박스 중심이 해당 위치에 오도록 Canvas.Left/Top 값을 반환한다.
/// </summary>
public sealed class NormalizedToCanvasConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double norm = value is double d ? d : 0;
        double canvas = 400, box = 72;
        if (parameter is string p)
        {
            var parts = p.Split('|');
            if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) canvas = c;
            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var b)) box = b;
        }
        return norm * canvas - box / 2;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
