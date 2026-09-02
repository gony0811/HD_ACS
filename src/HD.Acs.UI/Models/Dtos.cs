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

/// <summary>GET /api/runs/{id}/work-items — 실행 큐 항목(정차 1곳=영역 1개).
/// Status: PENDING | DISPATCHED | DONE | SKIPPED (+ Attempts 재시도 누적) [INSPECTION_SCENARIO §3.1]</summary>
public sealed record WorkItemDto(
    Guid WorkItemId,
    Guid AreaId,
    string AreaName,
    int Level,
    string MapId,
    int Seq,
    string Status,
    int Attempts);

/// <summary>GET /api/runs/resumable — 로봇의 가장 최근 재개 가능 run(미종결 작업 보유).</summary>
public sealed record ResumableRunDto(
    Guid RunId, Guid ScenarioId, string State, DateTimeOffset StartedAt,
    int Pending, int Done, int Skipped);

/// <summary>GET /api/runs/{id}/task-actions — 용접라인(액션) 단위 상태. CreatedAt 오름차순(재시도 시 나중 것이 최신).</summary>
public sealed record TaskActionDto(
    Guid ActionId,
    Guid? WorkItemId,
    Guid? TaskId,
    int? TaskSeq,
    string? TaskName,
    string Status,
    string? Result,        // 종결 시 {"ActionStatus","ResultDescription"} json
    DateTimeOffset CreatedAt);

/// <summary>SignalR "TaskActionProgress" 푸시 — 액션 상태 변화 단건(WAITING→RUNNING→FINISHED/FAILED).</summary>
public sealed record TaskActionProgressDto(
    Guid RunId,
    Guid? WorkItemId,
    Guid? TaskId,
    Guid ActionId,
    string Status,
    string? ResultDescription);

/// <summary>SignalR "WorkItemProgress" 푸시 — work_item 상태 변화 단건(배차/완료/재큐잉/스킵).</summary>
public sealed record WorkItemProgressDto(
    Guid RunId,
    Guid WorkItemId,
    Guid AreaId,
    string MapId,
    string Status,
    int Attempts);

/// <summary>SignalR "RunProgress" 푸시 / GET /api/runs/{id}/progress — Run 단위 TASK 진행률.
/// Percent = CompletedTasks / TotalTasks × 100 (종결 기준). Completed = Succeeded + Failed.</summary>
public sealed record RunProgressDto(
    Guid RunId,
    int TotalTasks,
    int ReleasedTasks,
    int CompletedTasks,
    int SucceededTasks,
    int FailedTasks,
    int PendingTasks,
    double Percent)
{
    /// <summary>0~1 진행바 값.</summary>
    public double Fraction => TotalTasks > 0 ? (double)CompletedTasks / TotalTasks : 0.0;
}

/// <summary>GET /api/scenarios 투영. AreaCount = 연결된 검사 대상 영역 수 (0 = 선창 전체 검사).</summary>
public sealed record ScenarioSummaryDto(
    Guid ScenarioId,
    string Name,
    int Version,
    string TankId,
    string Status,
    int AreaCount = 0)
{
    /// <summary>그리드 "대상" 컬럼 표시용.</summary>
    public string TargetText => AreaCount == 0 ? "전체" : $"{AreaCount}개 영역";
}

/// <summary>GET /api/scenarios/{id}/areas 항목 — 시나리오 검사 대상 영역 [부분 검사 계획].</summary>
public sealed record ScenarioAreaDto(Guid AreaId, string WallCode, int Level, string Name, int SortOrder);

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

/// <summary>GET /api/maps/{mapId}/calibration/points 항목 — ref.map_calibration_point [PHASE2 WP-1].</summary>
public sealed record CalibrationPointDto(
    Guid Id,
    string MapId,
    int MapVersion,
    double DrawingXM,
    double DrawingYM,
    double MapX,
    double MapY,
    DateTimeOffset CapturedAt,
    string? CapturedBy);

/// <summary>POST /api/maps/{mapId}/calibration/solve 응답. Warning은 RMS 임계 초과 시.</summary>
public sealed record CalibrationSolveResultDto(
    double Tx,
    double Ty,
    double YawRad,
    double RmsM,
    double MaxResidualM,
    int PointCount,
    string? Warning);

/// <summary>GET /api/maps/{mapId}/calibration — 현재 유효 T_W_D (ref.map_calibration).</summary>
public sealed record MapCalibrationDto(
    string MapId,
    int MapVersion,
    double Tx,
    double Ty,
    double YawRad,
    double RmsM,
    int PointCount,
    string? RegisteredBy,
    DateTimeOffset RegisteredAt);

/// <summary>GET /api/seams 항목 — ref.weld_seam (도면 좌표 원본은 제외한 관리용 투영) [PHASE2 WP-5b].</summary>
public sealed record SeamDto(
    Guid SeamId,
    string TankId,
    int Level,
    string WallCode,
    string SeamType,
    string SectionDxfId,
    string ProfileId);

/// <summary>도면/맵 pose (x,y,theta).</summary>
public sealed record PoseDto(double X, double Y, double Theta);

/// <summary>GET /api/scenarios/{id}/stations 의 TASK — 전개도 렌더용(도면 좌표) [PHASE2 WP-5b].</summary>
public sealed record SlicedTaskDto(
    int SeqInGroup,
    string SeamType,
    string? JobRef,
    string AnchorGroupId,
    double[] SeamStartDrawing,
    double[] SeamEndDrawing,
    double[] WallNormalDrawing);

/// <summary>GET /api/scenarios/{id}/stations 의 스테이션(anchorGroup) [PHASE2 WP-5b].</summary>
public sealed record SlicedStationDto(
    string AnchorGroupId,
    string WallCode,
    int Level,
    PoseDto StationDrawing,
    PoseDto StationMap,
    List<SlicedTaskDto> Tasks);

/// <summary>GET /api/tanks/{id}/geometry — 선창 파라미터 + 유도값 [SPEC v3 §2].</summary>
public sealed record TankGeometryDto(
    string TankId,
    double LengthL, double WFloor, double ThetaLowDeg, double HLow,
    double HWall, double ThetaUpDeg, double HUp,
    double[]? LevelZ, double OriginOx, double OriginOy,
    TankDerivedDto Derived,
    double? ReachZMin = null, double? ReachZMax = null);   // v3.1 §5-A 도달 밴드 보정(선택)
public sealed record TankDerivedDto(double WLow, double B, double WUp, double WCeil, double H);

/// <summary>GET /api/tanks/{id}/walls 항목 — 자동 생성된 면 [SPEC v3 §3].</summary>
public sealed record WallDto(
    string TankId,
    string WallCode,
    double[]? Origin,
    double[]? UAxis,
    double[]? VAxis,
    double[]? Normal,
    double ULen,
    double VLen,
    double? FacingYaw,
    bool Generated,
    string? Description,
    double[]? ReachableVBand = null);   // v3.1 §8: level 필터 조회 시 [vLo,vHi] 도달 v구간

/// <summary>GET /api/tanks/{tankId}/amr-teaching-table 항목 — STATION 노드 맵 pose + AMR Job 인덱스.
/// 작업자가 이 pose로 AMR을 수동 티칭한 뒤 Job/Task 인덱스를 회수·등록하는 참조 테이블.</summary>
public sealed record AmrTeachingRowDto(
    string NodeId, string MapId, string Name,
    double MapX, double MapY, double ThetaRad, double ThetaDeg,
    double? AllowedDevXy, double? AllowedDevTheta,
    int? AmrJobIndex, string? GotoMode);

/// <summary>GET /api/areas 항목 — ref.inspection_area (벽면-로컬 u,v) [SPEC v3 §4].</summary>
public sealed record AreaDto(
    Guid AreaId, string TankId, string WallCode, int Level, string Name,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta, int SortOrder, int TaskCount,
    double[][]? Corners = null,    // 임의 4점 사각형 [[u,v]…]. bbox(u/v min·max)와 함께 반환
    double? StationStandoffM = null);   // 정차 이격 [m] — null=서버 설정 기본

/// <summary>GET /api/areas/{id}/tasks 항목 — ref.area_task (u,v) [SPEC v3 §4].</summary>
public sealed record AreaTaskDto(
    Guid TaskId, int Seq, string? Name, string SeamType,
    double StartU, double StartV, double EndU, double EndV,
    string SectionDxfId, string ProfileId);

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
