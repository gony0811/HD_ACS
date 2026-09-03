using Avalonia;
using Avalonia.Media;

namespace HD.Acs.UI.Desktop;

internal static class Program
{
    // Avalonia 초기화 전에는 AppMain 이외의 Avalonia API를 쓰지 말 것.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>앱 빌더 — 디자이너·헤드리스 테스트도 이 메서드를 재사용한다.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 한글 글리프 폴백 — 기본 폰트에 한글이 없을 때 OS별 한글 시스템 폰트로 대체(macOS: Apple SD Gothic Neo, Windows: 맑은 고딕, Linux: Noto CJK)
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Apple SD Gothic Neo") },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK KR") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans KR") },
                    new FontFallback { FontFamily = new FontFamily("NanumGothic") },
                },
            })
            .LogToTrace();
}
