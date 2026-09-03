using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HD.Acs.UI.Desktop;

/// <summary>
/// 애플리케이션 부트스트랩 — Generic Host DI(AppHost, WPF 헤드와 동일 등록) + Fluent Dark.
/// API-First(ADR-005): UI는 REST(IAcsApiClient) + SignalR(IMonitoringClient)로만 서버와 통신.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>false면 데스크톱 생명주기에서 호스트·메인창을 자동 생성하지 않는다(헤드리스 테스트가 직접 구성).</summary>
    public static bool AutoStart { get; set; } = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // CommunityToolkit.Mvvm ObservableObject와 Avalonia DataAnnotations 검증 플러그인의 중복 검증 회피(권장 관례).
        // 타입으로 제거 — 헤드리스 테스트가 App을 여러 번 초기화해도 안전(정적 목록).
        for (int i = BindingPlugins.DataValidators.Count - 1; i >= 0; i--)
            if (BindingPlugins.DataValidators[i] is DataAnnotationsValidationPlugin)
                BindingPlugins.DataValidators.RemoveAt(i);

        if (AutoStart && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Build();
            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = shell;
            desktop.MainWindow = window;
            desktop.Exit += OnExit;

            // 서버 연동 시작(비동기, 실패해도 UI는 뜬다 — 두절 내성 ADR-002)
            _ = shell.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_host is null) return;
        try { _host.Services.GetRequiredService<IMonitoringClient>().StopAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 종료 중 무시 */ }
        _host.Dispose();
        _host = null;
    }
}
