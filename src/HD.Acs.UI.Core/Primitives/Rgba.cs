namespace HD.Acs.UI.Primitives;

/// <summary>
/// 프레임워크 중립 색(ARGB 8비트). 상태색 매핑(TankViewModel.StatusColors)의 반환 타입.
/// 각 UI 헤드가 Brush/Color로 변환한다(WPF: RgbaExtensions.ToMediaColor).
/// </summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public static Rgba FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
    public static Rgba FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    /// <summary>#AARRGGBB 표기(디버그·테스트 골든용).</summary>
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
