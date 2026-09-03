using HD.Acs.UI.Abstractions;
using HD.Acs.UI.Desktop.Services;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.Desktop;

/// <summary>
/// DI 컨테이너 구성 — WPF 헤드 App.xaml.cs와 같은 등록에 Avalonia 어댑터 3종만 다르다.
/// 헤드리스 테스트가 같은 구성을 재사용한다.
/// </summary>
public static class AppHost
{
    public static IHost Build(Action<IServiceCollection>? configure = null)
    {
        // content root = 실행 파일 위치(작업 디렉터리와 무관하게 appsettings.json 로드)
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.Services.Configure<AcsOptions>(builder.Configuration.GetSection(AcsOptions.SectionName));

        // REST — typed HttpClient (BaseAddress는 AcsOptions.BaseUrl)
        builder.Services.AddHttpClient<IAcsApiClient, AcsApiClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<AcsOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
        });

        // Avalonia 어댑터(HD.Acs.UI.Core 추상화 구현) — UI 스레드 마샬링·메시지 대화상자·프로젝트 대화상자
        builder.Services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        builder.Services.AddSingleton<IDialogService, AvaloniaDialogService>();
        builder.Services.AddSingleton<IProjectDialogService, AvaloniaProjectDialogService>();

        // SignalR 실시간 푸시 (연결 상태 공유 필요 → 싱글턴)
        builder.Services.AddSingleton<IMonitoringClient, MonitoringClient>();
        // 프로젝트 파일(.hdacs) 스냅샷 입출력
        builder.Services.AddSingleton<IProjectService, ProjectService>();

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

        configure?.Invoke(builder.Services);
        return builder.Build();
    }
}
