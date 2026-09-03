using Avalonia;
using Avalonia.Headless;
using HD.Acs.UI.Desktop;
using HD.Acs.UI.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace HD.Acs.UI.Desktop.Tests;

/// <summary>헤드리스 앱 빌더 — 실제 App(테마·리소스·스타일 포함)을 GUI 없이 기동. 호스트/메인창 자동 생성은 끈다.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        App.AutoStart = false;
        return AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}
