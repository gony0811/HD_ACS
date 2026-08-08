using System.Windows;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telerik.Windows.Controls;

namespace HD.Acs.UI;

/// <summary>
/// 애플리케이션 부트스트랩 — Generic Host 기반 DI(MS.DI로 백엔드와 일관) + Telerik Fluent 테마.
/// API-First(ADR-005): UI는 REST(IAcsApiClient) + SignalR(IMonitoringClient)로만 서버와 통신.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Telerik 전역 테마 — Fluent 다크 팔레트 (모든 Telerik 컨트롤 생성 이전에 설정).
        // LoadPreset은 static이며 FluentTheme 생성 전에 호출해야 다크 색상이 반영된다.
        FluentPalette.LoadPreset(FluentPalette.ColorVariation.Dark);
        StyleManager.ApplicationTheme = new FluentTheme();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.Services.Configure<AcsOptions>(builder.Configuration.GetSection(AcsOptions.SectionName));

        // REST — typed HttpClient (BaseAddress는 AcsOptions.BaseUrl)
        builder.Services.AddHttpClient<IAcsApiClient, AcsApiClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<AcsOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
        });

        // SignalR 실시간 푸시 (연결 상태 공유 필요 → 싱글턴)
        builder.Services.AddSingleton<IMonitoringClient, MonitoringClient>();

        // 프로젝트 파일(.hdacs) — 스냅샷 입출력 + 새 프로젝트/파일 대화상자
        builder.Services.AddSingleton<IProjectService, ProjectService>();
        builder.Services.AddSingleton<IProjectDialogService, ProjectDialogService>();

        // ViewModel (라이브 상태 보유 → 싱글턴)
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<RobotStatusViewModel>();
        builder.Services.AddSingleton<MissionViewModel>();
        builder.Services.AddSingleton<AlarmsViewModel>();
        builder.Services.AddSingleton<ManualZoneChangeViewModel>();
        builder.Services.AddSingleton<CalibrationViewModel>();
        builder.Services.AddSingleton<AreaPlanningViewModel>();
        builder.Services.AddSingleton<TankViewModel>();

        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = _host.Services.GetRequiredService<ShellViewModel>();
        window.Show();

        // 서버 연동 시작(비동기, 실패해도 UI는 뜬다 — 두절 내성 ADR-002)
        _ = _host.Services.GetRequiredService<ShellViewModel>().InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var monitoring = _host.Services.GetRequiredService<IMonitoringClient>();
            try { await monitoring.StopAsync(); } catch { /* 종료 중 무시 */ }
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
