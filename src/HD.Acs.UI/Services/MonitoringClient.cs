using System.Windows.Threading;
using HD.Acs.UI.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.Services;

/// <summary>
/// SignalR 모니터링 허브 클라이언트. 기존 MainWindow code-behind의 연결 로직을 이관한 것.
/// WithAutomaticReconnect로 두절 내성(ADR-002)을 확보하고, 수신 페이로드를 Dispatcher로 마샬링해 이벤트로 전파한다.
/// </summary>
public sealed class MonitoringClient : IMonitoringClient, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<MonitoringClient> _log;
    private readonly HubConnection _hub;

    public HubStatus Status { get; private set; } = HubStatus.Disconnected;

    public event EventHandler<HubStatus>? StatusChanged;
    public event EventHandler<RobotStateDto>? RobotStateReceived;
    public event EventHandler<RobotConnectionDto>? RobotConnectionReceived;
    public event EventHandler<MissionProgressDto>? MissionProgressReceived;
    public event EventHandler<RunProgressDto>? RunProgressReceived;
    public event EventHandler<AlarmDto>? AlarmRaised;

    public MonitoringClient(IOptions<AcsOptions> options, ILogger<MonitoringClient> log)
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;
        _log = log;

        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        _hub = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/monitoring")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<RobotStateDto>("RobotState", p => Raise(RobotStateReceived, p));
        _hub.On<RobotConnectionDto>("RobotConnection", p => Raise(RobotConnectionReceived, p));
        _hub.On<MissionProgressDto>("MissionProgress", p => Raise(MissionProgressReceived, p));
        _hub.On<RunProgressDto>("RunProgress", p => Raise(RunProgressReceived, p));
        _hub.On<AlarmDto>("AlarmRaised", p => Raise(AlarmRaised, p)); // 미발화여도 무해

        _hub.Reconnecting += _ => { SetStatus(HubStatus.Reconnecting); return Task.CompletedTask; };
        _hub.Reconnected += _ => { SetStatus(HubStatus.Connected); return Task.CompletedTask; };
        _hub.Closed += _ => { SetStatus(HubStatus.Disconnected); return Task.CompletedTask; };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        SetStatus(HubStatus.Connecting);
        try
        {
            await _hub.StartAsync(ct);
            SetStatus(HubStatus.Connected);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SignalR 연결 실패 — HD.Acs.App 실행 확인 필요");
            SetStatus(HubStatus.Failed);
        }
    }

    public Task StopAsync() => _hub.StopAsync();

    private void Raise<T>(EventHandler<T>? handler, T payload)
    {
        if (handler is null) return;
        _dispatcher.InvokeAsync(() => handler(this, payload));
    }

    private void SetStatus(HubStatus status)
    {
        Status = status;
        _dispatcher.InvokeAsync(() => StatusChanged?.Invoke(this, status));
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
