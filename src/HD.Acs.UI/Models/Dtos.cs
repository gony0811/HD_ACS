namespace HD.Acs.UI.Models;

// 백엔드(HD.Acs.App) 페이로드 미러 DTO.
// REST(System.Text.Json.Web) · SignalR(JsonHubProtocol) 모두 대소문자 무시 매칭이므로
// PascalCase 속성명이 서버의 camelCase 필드와 그대로 대응된다.

/// <summary>GET /api/robots — ref.robot</summary>
public sealed record RobotDto(
    string RobotId,
    string Name,
    string Manufacturer,
    string SerialNumber,
    string VdaVersion,
    bool IsActive);

/// <summary>GET /api/robots/{id}/context — run.robot_context. 수동 지정 vs 로봇 보고 대조.</summary>
public sealed record RobotContextDto(
    string RobotId,
    string? ManualMapId,
    string? ManualUpdatedBy,
    DateTimeOffset? ManualUpdatedAt,
    string? ReportedMapId,
    double? ReportedX,
    double? ReportedY,
    double? ReportedTheta,
    double? BatteryPct,
    string? ConnectionState,
    DateTimeOffset? ReportedAt);

/// <summary>SignalR "RobotState" 푸시 (RobotStateService.cs 익명 객체).</summary>
public sealed record RobotStateDto(
    string RobotId,
    string? ReportedMapId,
    double? ReportedX,
    double? ReportedY,
    double? BatteryPct,
    string? OrderId,
    string? LastNodeId,
    bool Driving,
    int Errors);

/// <summary>SignalR "RobotConnection" 푸시. ConnectionState: ONLINE | OFFLINE | CONNECTIONBROKEN</summary>
public sealed record RobotConnectionDto(
    string RobotId,
    string ConnectionState);

/// <summary>SignalR "MissionProgress" 푸시. State는 MissionState 이름.</summary>
public sealed record MissionProgressDto(
    Guid MissionId,
    string State);

/// <summary>GET /api/scenarios 투영.</summary>
public sealed record ScenarioSummaryDto(
    Guid ScenarioId,
    string Name,
    int Version,
    string TankId,
    string Status);

/// <summary>GET /api/runs/{id} — run.scenario_run (+ 층 미션 시퀀스).
/// State: RUNNING | WAITING_FLOOR_TRANSFER | COMPLETED | ABORTED</summary>
public sealed record ScenarioRunDto(
    Guid RunId,
    Guid ScenarioId,
    int ScenarioVer,
    string RobotId,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    List<MissionDto> Missions);

/// <summary>run.mission — 한 미션 = 한 층(mapId).</summary>
public sealed record MissionDto(
    Guid MissionId,
    Guid RunId,
    int Seq,
    string MapId,
    string RobotId,
    string OrderId,
    int OrderUpdateId,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>SignalR "AlarmRaised" 푸시 대비 (백엔드 미발화 — 스키마 기반 예상 shape).
/// Severity: INFO | WARNING | CRITICAL</summary>
public sealed record AlarmDto(
    Guid AlarmId,
    string AlarmCode,
    string? RobotId,
    Guid? MissionId,
    string? Detail,
    string? Severity,
    string? Title,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ClearedAt,
    string? ClearedBy);
