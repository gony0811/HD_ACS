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
    int Errors,
    double? ReportedTheta = null,    // 맵 프레임 heading(rad, VDA agvPosition.theta). 구서버 미포함 → null
    string? OperatingMode = null,    // AUTOMATIC | SEMIAUTOMATIC | MANUAL | SERVICE | TEACHIN. 구서버 → null
    string? EStop = null,            // safetyState.eStop: NONE | AUTOACK | MANUAL | REMOTE
    string[]? ErrorDescriptions = null);   // 활성 errors "errorType: description" 목록

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

/// <summary>GET /api/runs — 이력 목록.</summary>
public sealed record RunSummaryDto(
    Guid RunId, Guid ScenarioId, string? ScenarioName, string? TankId,
    string RobotId, string State, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

/// <summary>GET /api/runs/{id}/results — TASK별 최종 성공/실패 이력.</summary>
public sealed record RunResultsDto(Guid RunId, int Total, IReadOnlyList<RunTaskResultDto> Items);
public sealed record RunTaskResultDto(
    Guid TaskId, Guid AreaId, string? AreaName, int WallId, string? WallCode, int? Level,
    string Status, int Attempts, DateTimeOffset OccurredAt, string? Description,
    TaskSeamPositionDto? Position);
public sealed record TaskSeamPositionDto(
    int WallId, UvMmDto SeamStart, UvMmDto SeamEnd, int SeamLength, string SeamType);
public sealed record UvMmDto(int U, int V);

/// <summary>SignalR "WorkItemProgress" 푸시 — work_item 상태 변화 단건(배차/완료/재큐잉/스킵).</summary>
public sealed record WorkItemProgressDto(
    Guid RunId,
    Guid WorkItemId,
    Guid AreaId,
    string MapId,
    string Status,
    int Attempts,
    string? Reason = null);   // 실패(재큐잉 PENDING/SKIPPED) 시 AMR 보고 사유 요약

/// <summary>SignalR "RunState" 푸시 — run 상태 변화(RUNNING | WAITING_FLOOR_TRANSFER | COMPLETED | ABORTED).</summary>
public sealed record RunStateDto(Guid RunId, string State);

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

/// <summary>GET /api/internal/tanks/{id}/geometry — 선창 파라미터 + 유도값 [SPEC v3 §2].</summary>
public sealed record TankGeometryDto(
    string TankId,
    double LengthL, double WFloor, double ThetaLowDeg, double HLow,
    double HWall, double ThetaUpDeg, double HUp,
    double[]? LevelZ, double OriginOx, double OriginOy,
    TankDerivedDto Derived,
    double? ReachZMin = null, double? ReachZMax = null);   // v3.1 §5-A 도달 밴드 보정(선택)
public sealed record TankDerivedDto(double WLow, double B, double WUp, double WCeil, double H);

/// <summary>GET /api/internal/tanks/{id}/walls 항목 — 자동 생성된 면 [SPEC v3 §3].</summary>
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

/// <summary>GET /api/internal/areas 항목 — ref.inspection_area (벽면-로컬 u,v) [SPEC v3 §4].</summary>
public sealed record AreaDto(
    Guid AreaId, string TankId, string WallCode, int Level, string Name,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta, int SortOrder, int TaskCount,
    double[][]? Corners = null,    // 임의 4점 사각형 [[u,v]…]. bbox(u/v min·max)와 함께 반환
    double? StationStandoffM = null);   // 정차 이격 [m] — null=서버 설정 기본

/// <summary>GET /api/internal/areas/{id}/tasks 항목 — ref.area_task (u,v) [SPEC v3 §4].</summary>
public sealed record AreaTaskDto(
    Guid TaskId, int Seq, string? Name, string SeamType,
    double StartU, double StartV, double EndU, double EndV,
    string SectionDxfId, string ProfileId,
    double[][]? Points = null)    // CROSS3/4 교차 가지 끝점 [[u,v],...] 면-로컬 m, 중심=Start [VDA §8.5.1]
{
    /// <summary>작업 목록 "유형" 열 표시 — ▲CROSS3·■CROSS4·가지 없음 표시(<see cref="HD.Acs.UI.Rendering.SeamGlyph.TypeLabel"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TypeLabel => HD.Acs.UI.Rendering.SeamGlyph.TypeLabel(SeamType, Points);
}

/// <summary>CROSS 교차 기하 미리보기 응답 [VDA §8.5.1] — 서버가 회전 유도·AMR 점 정렬.
/// FrameOk=false면 바닥/천장(AMR u 미정의)이라 CROSS3 회전 불가.</summary>
public sealed record CrossPreviewResult(string SeamType, double[][]? Points, double SnapResidualDeg, bool FrameOk);

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

/// <summary>GET /api/scenarios/{id}/area-stations — 시나리오 계획 정차점(도면 프레임) + 도착 허용 오차(m, rad).</summary>
public sealed record PlannedStationsDto(Guid ScenarioId, string TankId, double AllowedDevXy, double AllowedDevTheta,
    List<PlannedStationDto> Stations);

/// <summary>영역 1개의 계획 정차점. Yaw=도면 yaw[rad](없으면 null), WallU/WallNormal=면 u축·내부향 법선 수평 단위벡터.</summary>
public sealed record PlannedStationDto(Guid AreaId, string AreaName, string WallCode, int Level, string MapId,
    double X, double Y, double? Yaw, double StandoffM, bool Manual, double[]? WallU, double[]? WallNormal);

// ── 계획 자연어 어시스턴트 [ADR-013] — 서버 PlanningAssistantService 페이로드 미러 ──

/// <summary>대화 1줄(role = user | assistant) — LLM에 직전 대화로 전달.</summary>
public sealed record PlanChatMessageDto(string Role, string Content);

/// <summary>POST /api/planning/assistant/propose 요청. WallCode/Level = 현재 화면 선택(명령에 면·층이 없을 때의 힌트).</summary>
public sealed record PlanProposeRequestDto(string TankId, string Prompt, string? WallCode, int? Level, PlanChatMessageDto[]? History);

/// <summary>원자 연산 1건의 검증 결과. Corners/Segment 는 **면-전체 v**(미리보기 그리기용).</summary>
public sealed record PlanChangeOpDto(
    int Index, int Source, string Kind, bool Ok, string? Error,
    string? WallCode, int? Level, string? AreaName, double[][]? Corners, double[]? Segment, string Summary);

/// <summary>변경안 미리보기. ChangeSetId = 적용 가능할 때만(변경 있음 + 전 연산 통과).</summary>
public sealed record PlanChangeSetDto(
    Guid? ChangeSetId, string TankId, string Reply, string[] Messages, List<PlanChangeOpDto> Results,
    int Creates, int Updates, int Deletes, int Failed, bool AllOk, bool HasChanges,
    string[]? OpsSummary = null);   // LLM 이 낸 연산 요약 — 대화창 "해석:" 줄(구서버는 없음)

public sealed record PlanApplyResultDto(Guid ChangeSetId, int Applied, int Creates, int Updates, int Deletes);

/// <summary>GET /api/integrations/llm — Ollama 연결 상태(Reachable·ModelAvailable 은 조회 시 점검).</summary>
public sealed record LlmStatusDto(bool Enabled, string BaseUrl, string Model, DateTimeOffset? LastOkAt, string? LastError,
    bool? Reachable, bool? ModelAvailable, int? AssistantRevision = null);   // null = 개정 번호 도입 전 구빌드 서버
