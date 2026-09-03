using Avalonia;

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
            .LogToTrace();
}
