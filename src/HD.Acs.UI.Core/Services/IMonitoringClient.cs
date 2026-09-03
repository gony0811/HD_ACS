using HD.Acs.UI.Models;

namespace HD.Acs.UI.Services;

/// <summary>SignalR 허브 연결 상태.</summary>
public enum HubStatus { Disconnected, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// SignalR /hubs/monitoring 실시간 푸시 래퍼. 서버→클라 이벤트를 C# event로 노출한다.
/// 이벤트는 UI 스레드(Dispatcher)로 마샬링되어 발생하므로 구독자(ViewModel)는 별도 마샬링이 불필요하다.
/// </summary>
public interface IMonitoringClient
{
    HubStatus Status { get; }

    event EventHandler<HubStatus>? StatusChanged;
    event EventHandler<RobotStateDto>? RobotStateReceived;
    event EventHandler<RobotConnectionDto>? RobotConnectionReceived;
    event EventHandler<MissionProgressDto>? MissionProgressReceived;
    event EventHandler<RunProgressDto>? RunProgressReceived;   // TASK 단위 진행률
    event EventHandler<WorkItemProgressDto>? WorkItemProgressReceived;   // 실행 큐 항목 상태 변화 단건
    event EventHandler<TaskActionProgressDto>? TaskActionProgressReceived;   // 용접라인(액션) 상태 변화 단건
    event EventHandler<AlarmDto>? AlarmRaised;   // 백엔드 미발화 — 대비용 구독

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
