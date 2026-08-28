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

/// <summary>enum 값이 ConverterParameter(모드명)와 같으면 Visible, 아니면 Collapsed. 모드 화면 전환용.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// enum ↔ bool. 값이 ConverterParameter(모드명)와 같으면 true. ConvertBack은 true일 때만 해당 enum을 반환
/// (false=다른 버튼 해제이므로 무시) — 모드 탭 ToggleButton IsChecked 양방향 바인딩용.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>
/// work_item 상태 문자열 → 브러시. ConverterParameter="fill"(반투명 채움) | "stroke"(외곽/배지, 기본).
/// 색 매핑의 단일 소스는 TankViewModel.StatusColors (3D material과 공유).
/// </summary>
public sealed class WorkStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string;
        var color = (parameter as string)?.ToLowerInvariant() switch
        {
            "fill" => ViewModels.TankViewModel.StatusColors(status).Fill,
            "weldstroke" => ViewModels.TankViewModel.WeldLineColor(status),   // 상태 없으면 계획 기본 주황
            _ => ViewModels.TankViewModel.StatusColors(status).Line,
        };
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
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
