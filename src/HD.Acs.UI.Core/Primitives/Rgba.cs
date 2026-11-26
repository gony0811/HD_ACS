namespace HD.Acs.UI.Primitives;

/// <summary>
/// 프레임워크 중립 색(ARGB 8비트). 상태색 매핑(TankViewModel.StatusColors)의 반환 타입.
/// 각 UI 헤드가 Brush/Color로 변환한다(Avalonia 헤드: Infrastructure/Converters.cs의 RgbaExtensions).
/// </summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public static Rgba FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
    public static Rgba FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    /// <summary>흑백 음영으로 낮춘 색 — 밝기(ITU-R BT.601 가중)만 남기고 불투명도를 <see cref="MutedAlpha"/>배로 줄인다.
    /// 운영 화면 미션 강조 보기에서 대상이 아닌 영역·용접선을 낮출 때 쓴다(밝기 차이는 남아 지난 기록이 조금 밝게 보인다).</summary>
    public Rgba Muted()
    {
        byte y = (byte)Math.Clamp(Math.Round(0.299 * R + 0.587 * G + 0.114 * B), 0, 255);
        return new((byte)Math.Round(A * MutedAlpha), y, y, y);
    }

    /// <summary>흑백 음영의 불투명도 배율.</summary>
    public const double MutedAlpha = 0.42;

    /// <summary>muted=true면 <see cref="Muted"/>, 아니면 그대로.</summary>
    public Rgba MutedIf(bool muted) => muted ? Muted() : this;

    /// <summary>#AARRGGBB 표기(디버그·테스트 골든용).</summary>
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
