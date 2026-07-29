using HD.Acs.UI.Models;

namespace HD.Acs.UI.Services;

/// <summary>
/// HD.Acs.App REST API 계약. UI는 API-First(ADR-005)로 이 인터페이스만 통해 서버와 통신한다.
/// 미구현 백엔드 엔드포인트(알람/이력 등)는 별도 표기하며, 추가 시 여기에 메서드를 확장한다.
/// </summary>
public interface IAcsApiClient
{
    // ── 조회 ──────────────────────────────────────────────
    Task<IReadOnlyList<RobotDto>> GetRobotsAsync(CancellationToken ct = default);
    Task<RobotContextDto?> GetRobotContextAsync(string robotId, CancellationToken ct = default);
    Task<IReadOnlyList<ScenarioSummaryDto>> GetScenariosAsync(CancellationToken ct = default);
    Task<ScenarioRunDto?> GetRunAsync(Guid runId, CancellationToken ct = default);

    // ── 명령 ──────────────────────────────────────────────
    Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default);
    Task<bool> ReleaseNextMissionAsync(Guid runId, CancellationToken ct = default);
    Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        double x, double y, double theta, CancellationToken ct = default);
    Task EmergencyStopAsync(string robotId, string userId, CancellationToken ct = default);

    // ── 미구현 백엔드 대비 (엔드포인트 추가 시 연결) ──────────
    // Task<IReadOnlyList<AlarmDto>> GetActiveAlarmsAsync(CancellationToken ct = default);
}
