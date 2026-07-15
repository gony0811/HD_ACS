using Microsoft.AspNetCore.SignalR;

namespace HD.Acs.App.Hubs;

/// <summary>
/// 다중 사용자 실시간 모니터링 허브 [ADR-003/005].
/// 서버 → 클라이언트 이벤트: RobotState, RobotConnection, MissionProgress, AlarmRaised
/// </summary>
public sealed class MonitoringHub : Hub { }
