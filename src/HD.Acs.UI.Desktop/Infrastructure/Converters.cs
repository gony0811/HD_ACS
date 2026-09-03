using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Infrastructure;

/// <summary>
/// enum ↔ bool. 값이 ConverterParameter(모드명)와 같으면 true — 모드 탭 IsChecked·모드 화면 IsVisible 공용
/// (WPF의 EnumToVisibility/EnumToBoolean 두 컨버터를 Avalonia IsVisible(bool)에 맞춰 하나로).
/// ConvertBack은 true일 때만 해당 enum을 반환(false=다른 버튼 해제이므로 무시).
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : BindingOperations.DoNothing;
}

/// <summary>
/// work_item/액션 상태 문자열 → 브러시. ConverterParameter="fill"(반투명 채움) | "stroke"(외곽/배지, 기본) | "weldStroke"(용접선, 계획=주황).
/// 색 매핑의 단일 소스는 코어 TankViewModel.StatusColors(Rgba).
/// </summary>
public sealed class WorkStatusToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        var color = (parameter as string)?.ToLowerInvariant() switch
        {
            "fill" => TankViewModel.StatusColors(status).Fill,
            "weldstroke" => TankViewModel.WeldLineColor(status),
            _ => TankViewModel.StatusColors(status).Line,
        };
        return color.ToBrush();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>코어 Pt2 목록 → Avalonia Points (Polygon.Points 바인딩용).</summary>
public sealed class PointsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pts = new Points();
        if (value is IEnumerable<Pt2> src)
            foreach (var p in src) pts.Add(new Point(p.X, p.Y));
        return pts;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>코어 Pt2 → Avalonia Point (Line.StartPoint/EndPoint 바인딩용).</summary>
public sealed class PointConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Pt2 p ? new Point(p.X, p.Y) : new Point();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>
/// 층 진행 레일 Kind(done/run/wait/fail) → 브러시. WPF DataTrigger 대체.
/// ConverterParameter="bar"(ProgressBar 채움: done=Good, fail=Error, wait=Muted, 그 외 Accent)
/// | "text"(라벨: done=Good, run=Accent, fail=Error, 그 외 Muted). 앱 리소스 브러시를 조회한다.
/// </summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value?.ToString();
        bool bar = string.Equals(parameter as string, "bar", StringComparison.OrdinalIgnoreCase);
        var key = (kind, bar) switch
        {
            ("done", _) => "AppGoodBrush",
            ("fail", _) => "AppErrorBrush",
            ("wait", true) => "AppMutedTextBrush",
            ("run", false) => "AppAccentBrush",
            (_, true) => "AppAccentBrush",
            _ => "AppMutedTextBrush",
        };
        return Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var res) && res is IBrush b
            ? b
            : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>픽 모드(bool) → 커서(크로스헤어/기본). WPF DataTrigger Cursor=Cross 대체.</summary>
public sealed class BoolToCursorConverter : IValueConverter
{
    private static readonly Cursor Cross = new(StandardCursorType.Cross);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Cross : Cursor.Default;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>코어 중립 색(Rgba) → Avalonia 색/브러시.</summary>
public static class RgbaExtensions
{
    public static Color ToColor(this Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static IBrush ToBrush(this Rgba c) => new ImmutableSolidColorBrush(c.ToColor());
}
