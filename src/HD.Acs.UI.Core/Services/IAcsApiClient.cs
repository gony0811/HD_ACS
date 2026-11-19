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
    Task<RunProgressDto?> GetRunProgressAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItemDto>> GetWorkItemsAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskActionDto>> GetTaskActionsAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<RunSummaryDto>> GetRunsAsync(int limit = 50, CancellationToken ct = default);
    Task<RunResultsDto?> GetRunResultsAsync(Guid runId, CancellationToken ct = default);

    // ── 명령 ──────────────────────────────────────────────
    Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default);
    /// <summary>미션 시작 + 가스검사 회차(1|2, null = 지정 안 함). 기본 구현 = 회차 없이 시작(기존 대역 무수정).</summary>
    Task<Guid> StartRunAsync(Guid scenarioId, string robotId, int? round, CancellationToken ct) =>
        StartRunAsync(scenarioId, robotId, ct);
    /// <summary>리페어 run — 지정한 용접선(TASK)만 검사. optimize = 시작 전에 그 용접선만 놓고 정차점 최적화를 돌려 반영.
    /// null = 서버 미지원(구빌드). 거부(400·409)는 예외.</summary>
    Task<RepairRunStartedDto?> StartRepairRunAsync(string robotId, IReadOnlyCollection<Guid> taskIds, Guid? scenarioId = null,
        bool optimize = true, CancellationToken ct = default) => Task.FromResult<RepairRunStartedDto?>(null);
    /// <summary>리페어 run + 가스검사 회차(1|2, null = 용접검사). 기본 구현 = 회차 없이 시작(기존 대역 무수정).</summary>
    Task<RepairRunStartedDto?> StartRepairRunAsync(string robotId, IReadOnlyCollection<Guid> taskIds, Guid? scenarioId,
        bool optimize, int? round, CancellationToken ct) => StartRepairRunAsync(robotId, taskIds, scenarioId, optimize, ct);
    Task AbortRunAsync(Guid runId, CancellationToken ct = default);
    Task ResumeRunAsync(Guid runId, CancellationToken ct = default);
    Task<ResumableRunDto?> GetResumableRunAsync(string robotId, CancellationToken ct = default);
    Task<bool> ReleaseNextMissionAsync(Guid runId, CancellationToken ct = default);
    /// <summary>수동 이동(goto) — 도면 위치·선택 방향을 서버가 T_W_D로 변환해 액션 없는 단일 노드 Order 발행.</summary>
    Task GotoAsync(string robotId, int level, double xDrawing, double yDrawing,
        double? thetaDrawing = null, CancellationToken ct = default);
    Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        CancellationToken ct = default);
    Task EmergencyStopAsync(string robotId, string userId, CancellationToken ct = default);

    // ── 도면→맵 캘리브레이션 (T_W_D) [PHASE2 WP-1/5a] ──────────
    Task<CalibrationPointDto> CaptureCalibrationPointAsync(string mapId,
        double drawingX, double drawingY, string unit, string userId, CancellationToken ct = default,
        double? mapX = null, double? mapY = null);
    Task<IReadOnlyList<CalibrationPointDto>> GetCalibrationPointsAsync(string mapId, CancellationToken ct = default);
    Task DeleteCalibrationPointAsync(string mapId, Guid pointId, CancellationToken ct = default);
    /// <summary>기준점 수정 — pointId 유지(삭제→재등록과 달리 대응쌍이 끊기지 않는다).</summary>
    Task<CalibrationPointDto> UpdateCalibrationPointAsync(string mapId, Guid pointId,
        double drawingX, double drawingY, string unit, double mapX, double mapY, string userId,
        CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>도면 좌표만 원점 기준 deg 회전(맵 좌표 불변) — 입력 프레임 오류 교정. 반환=회전된 점 수.</summary>
    Task<int> RotateCalibrationDrawingAsync(string mapId, double deg, string userId,
        CancellationToken ct = default) => throw new NotSupportedException();
    Task<CalibrationSolveResultDto> SolveCalibrationAsync(string mapId, CancellationToken ct = default);
    Task<MapCalibrationDto?> GetCalibrationAsync(string mapId, CancellationToken ct = default);

    // ── 슬라이싱/TASK (전개도) [PHASE2 WP-5b] ──────────
    Task<Guid> CreateScenarioAsync(string name, string tankId, CancellationToken ct = default);
    // 참조하는 run이 있으면 서버가 409(메시지 포함)로 거부한다.
    Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct = default);
    // 부분 검사 계획 — 시나리오 검사 대상 영역 (빈 목록 = 전체 검사)
    Task<IReadOnlyList<ScenarioAreaDto>> GetScenarioAreasAsync(Guid scenarioId, CancellationToken ct = default);
    Task SetScenarioAreasAsync(Guid scenarioId, IReadOnlyList<Guid> areaIds, CancellationToken ct = default);
    // 계획 정차점(도면 프레임) — 로봇 상태 카드의 "계획 정차점까지 거리". 시나리오 없음(404)=null
    Task<PlannedStationsDto?> GetPlannedStationsAsync(Guid scenarioId, CancellationToken ct = default) =>
        Task.FromResult<PlannedStationsDto?>(null);
    // 리치·간섭 시뮬레이션 — 정차에서 코봇이 세 자세로 용접선에 닿는지·탱크 면과 부딪히는지(조회 전용). 선창·영역 없음=null
    /// <remarks>branch = "clearance" 면 IK 가지를 간섭 여유 최대로(대안 검토), null 이면 운영 규칙(자세 여유 최대).</remarks>
    /// <summary>리치 조사 — areaLift 면 영역마다 리프트를 한 높이로 고정(고정 높이·용접선별 최적과 비교 포함).</summary>
    Task<IReadOnlyList<ReachSurveyAreaDto>?> GetReachSurveyAsync(string tankId, int? level, string? wallCode,
        CancellationToken ct = default, string? branch = null, bool areaLift = false) => Task.FromResult<IReadOnlyList<ReachSurveyAreaDto>?>(null);
    /// <summary>영역 검사 가능 지도 — 운영 조건(저장된 정차·리프트)에서 영역의 용접선이 전부 가능한지. 선창 없음·구서버면 null.</summary>
    Task<IReadOnlyList<ReachAreaVerdictDto>?> GetReachAreaMapAsync(string tankId, int? level, string? wallCode, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReachAreaVerdictDto>?>(null);
    Task<ReachDetailDto?> GetReachDetailAsync(Guid areaId, Guid taskId, int point, double? strokeMm,
        double offsetForwardMm, double offsetLeftMm, int yawIndex, CancellationToken ct = default, string? branch = null) =>
        Task.FromResult<ReachDetailDto?>(null);
    /// <summary>정차점 최적화(계산만) — 층·면·영역 필터. 선창이 없으면 null.</summary>
    Task<IReadOnlyList<ReachStationOptimumDto>?> GetStationOptimaAsync(string tankId, int? level, string? wallCode, Guid? areaId,
        CancellationToken ct = default, string? branch = null) => Task.FromResult<IReadOnlyList<ReachStationOptimumDto>?>(null);
    /// <summary>정차·리치 보고서 백데이터(CSV 조각) — 층·면·영역 필터, optimize 면 정차점 최적화 제안 포함. 선창 없음·구서버면 null.</summary>
    Task<ReachReportDto?> GetReachReportAsync(string tankId, int? level, string? wallCode, Guid? areaId, bool optimize,
        CancellationToken ct = default, string? branch = null) => Task.FromResult<ReachReportDto?>(null);
    /// <summary>영역 재분할 제안(계산만, 수십 초) — 영역이 없으면 null.</summary>
    Task<ReachSplitPlanDto?> GetSplitPlanAsync(Guid areaId, int maxGroups = 4, CancellationToken ct = default, string? branch = null) =>
        Task.FromResult<ReachSplitPlanDto?>(null);
    /// <summary>영역 재분할 적용 — 제안의 묶음(작업·정차·리프트)을 서버가 한 트랜잭션으로 반영. 구서버면 null.</summary>
    Task<AreaSplitResultDto?> ApplySplitAsync(ReachSplitPlanDto plan, CancellationToken ct = default) =>
        Task.FromResult<AreaSplitResultDto?>(null);
    /// <summary>영역 정차 덮어쓰기(x·y·방향·리프트 높이) 설정 — 모두 null 이면 해제.</summary>
    Task SetAreaStationAsync(Guid areaId, double? x, double? y, double? theta, string reason, double? stationLiftMm = null, CancellationToken ct = default) =>
        Task.CompletedTask;
    Task<Guid> CreateSeamAsync(string tankId, int level, string wallCode, string seamType,
        double[][] pathDrawing, double[] normalDrawing, string sectionDxfId, string profileId,
        string userId, CancellationToken ct = default);
    Task<IReadOnlyList<SeamDto>> GetSeamsAsync(string tankId, int? level = null, CancellationToken ct = default);
    Task DeleteSeamAsync(Guid seamId, CancellationToken ct = default);
    Task<(int Stations, int Tasks)> GenerateFromSeamsAsync(Guid scenarioId, CancellationToken ct = default);
    Task<IReadOnlyList<SlicedStationDto>> GetStationsAsync(Guid scenarioId, CancellationToken ct = default);

    // ── 선창 3D 정의 [SPEC v3 §2/§3] — 파라미터 등록 → 면 자동생성 ──────────
    Task<int> RegisterTankGeometryAsync(string tankId, double lengthL, double wFloor, double thetaLowDeg,
        double hLow, double hWall, double thetaUpDeg, double hUp, double[] levelZ,
        double originOx, double originOy, string userId,
        double? reachZMin = null, double? reachZMax = null, CancellationToken ct = default);
    /// <summary>선창 등록 + 검사 종류(WELD|GAS, null = 기존 선창은 유지·새 선창은 WELD). 기본 구현 = 검사 종류 없이 등록(기존 대역 무수정).</summary>
    Task<int> RegisterTankGeometryAsync(string tankId, double lengthL, double wFloor, double thetaLowDeg,
        double hLow, double hWall, double thetaUpDeg, double hUp, double[] levelZ,
        double originOx, double originOy, string userId,
        double? reachZMin, double? reachZMax, string? inspectionKind, CancellationToken ct = default) =>
        RegisterTankGeometryAsync(tankId, lengthL, wFloor, thetaLowDeg, hLow, hWall, thetaUpDeg, hUp, levelZ,
            originOx, originOy, userId, reachZMin, reachZMax, ct);
    Task<TankGeometryDto?> GetTankGeometryAsync(string tankId, CancellationToken ct = default);
    // v3.1 §8: level 지정 시 그 층 도달 가능 면만 + 면별 reachableVBand 반환.
    Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default);

    // ── 영역·검사 작업 [SPEC v3 §4] — 벽면-로컬 (u,v). v3.1: level은 서버가 유도(반환값에 유도 층) ──────────
    // corners = 임의 4점 사각형 [[u,v]…]. 서버가 bbox 유도.
    Task<(Guid AreaId, int Level)> CreateAreaAsync(string tankId, string wallCode, string name,
        double[][] corners,
        double? stationX, double? stationY, double? stationTheta, string userId,
        double? stationStandoffM = null, Guid? areaId = null, double? stationMaxBackwardMm = null,
        double? stationLiftMm = null, CancellationToken ct = default);
    /// <summary>영역 수정(PUT) — areaId·면 유지, 이름·코너(면-전체 v)·정차 전체 교체. 반환=재유도된 층.</summary>
    Task<int> UpdateAreaAsync(Guid areaId, string name, double[][] corners,
        double? stationX, double? stationY, double? stationTheta, double? stationStandoffM, string userId,
        double? stationMaxBackwardMm = null, double? stationLiftMm = null, CancellationToken ct = default);
    /// <summary>선택 작업들을 묶어 새 영역으로 분리·이동(taskId 유지). corners 미지정 시 서버가 작업 외접 사각형을 자동 생성.</summary>
    Task<(Guid AreaId, int Level, int MovedCount)> SplitTasksToNewAreaAsync(
        string tankId, string wallCode, string name, IReadOnlyList<Guid> taskIds, double[][]? corners,
        double? stationX, double? stationY, double? stationTheta, string userId,
        double? stationStandoffM = null, double? stationMaxBackwardMm = null, CancellationToken ct = default) =>
        Task.FromResult((Guid.Empty, 0, 0));
    Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, string? wallCode = null, int? level = null, CancellationToken ct = default);
    Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default);
    /// <summary>
    /// 영역 삭제 — 서버가 작업을 원래 영역으로 되돌린 뒤 지운다. 갈 곳이 없는 작업이 있으면 <c>Blocked</c>(삭제 안 됨),
    /// <paramref name="deleteTasks"/> = true 면 그 작업도 함께 지운다. 기본 구현 = 종전 삭제(구서버·테스트 대역).
    /// </summary>
    async Task<AreaDeleteOutcome> DeleteAreaAsync(Guid areaId, bool deleteTasks, string userId, CancellationToken ct = default)
    {
        await DeleteAreaAsync(areaId, ct);
        return new AreaDeleteOutcome(false, null, Array.Empty<string>(), Array.Empty<string>());
    }
    Task<int> CreateAreaTaskAsync(Guid areaId, double startU, double startV, double endU, double endV,
        string seamType, string sectionDxfId, string profileId, string userId,
        int? seq = null, string? name = null, Guid? taskId = null, double[][]? points = null, CancellationToken ct = default);
    Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default);
    /// <summary>선창 전 작업 일괄 조회(프로젝트 저장용). null = 서버 미지원(구빌드) — 호출자는 영역별 조회로 폴백.</summary>
    Task<IReadOnlyList<TankAreaTaskDto>?> GetTankAreaTasksAsync(string tankId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TankAreaTaskDto>?>(null);
    /// <summary>선창 누적 검사 기록 — 용접선마다 가장 최근 결과 1건(미검사는 없음). null = 서버 미지원(구빌드).</summary>
    Task<IReadOnlyList<TaskLatestResultDto>?> GetTaskLatestResultsAsync(string tankId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaskLatestResultDto>?>(null);
    /// <summary>프로젝트 일괄 가져오기(선창 재등록+영역·작업, 전부 또는 전무). null = 서버 미지원(구빌드) — 호출자는 단건 등록으로 폴백.
    /// 검증 거부는 <see cref="ProjectRejectedException"/>(DB 무변경).</summary>
    Task<ProjectImportResultDto?> ImportProjectAsync(string tankId, ProjectImportRequestDto request, CancellationToken ct = default) =>
        Task.FromResult<ProjectImportResultDto?>(null);
    /// <summary>검사 작업 수정 — taskId 유지(영구 식별자). 좌표 필수, seq/name/seamType은 null=기존값 유지. points: null=기존 유지, []=지움(CROSS 가지).</summary>
    Task UpdateAreaTaskAsync(Guid taskId, double startU, double startV, double endU, double endV,
        string? seamType, string userId, int? seq = null, string? name = null, double[][]? points = null, CancellationToken ct = default);
    Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default);
    /// <summary>서버에 등록된 선창 ID 목록(등록 순). null = 서버 미지원(구빌드).</summary>
    Task<IReadOnlyList<string>?> GetTankIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
    /// <summary>
    /// 선창 ID 변경 — 형상·면·영역·시나리오·층 맵을 함께 옮긴다(영역 ID·taskId 유지). false = 서버 미지원(구빌드).
    /// 형식 오류 400 · 이미 쓰는 ID·진행 중 run 409 · 없는 선창 404 는 예외(메시지에 사유).
    /// </summary>
    Task<bool> RenameTankAsync(string tankId, string newTankId, string userId, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>
    /// CROSS4 작업의 정면 좌우(CROSS4_L/R) 판정 [VDA §8.2 · 1.22]. apply=false = 미리보기, true = 바뀐 작업 저장.
    /// null = 서버 미지원(구빌드).
    /// </summary>
    Task<Cross4SideResultDto?> ClassifyCross4SidesAsync(string tankId, bool apply, string userId, CancellationToken ct = default) =>
        Task.FromResult<Cross4SideResultDto?>(null);
    /// <summary>용접선 임시 제외/복귀 — enabled=false 면 검사(큐 전개·재개·재검사)에서 빠진다. false 반환 = 서버 미지원(구빌드).</summary>
    Task<bool> SetAreaTasksEnabledAsync(IReadOnlyList<Guid> taskIds, bool enabled, string userId, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>CROSS 교차 기하 미리보기 — 중심·가지(면-전체 v)로 회전 유도·AMR 점 정렬(비영속) [VDA §8.5.1].</summary>
    Task<CrossPreviewResult?> CrossPreviewAsync(Guid areaId, string seamType, double centerU, double centerV,
        double[][] arms, CancellationToken ct = default);
    /// <summary>코봇 seam 시작점 이동 시험 — 지정 용접선의 정차점으로 주행 후 코봇을 seam 시작점까지만 이동(촬영 없음) [VDA §8].</summary>
    Task<SeamStartTestResultDto?> MoveToSeamStartTestAsync(string robotId, Guid taskId, CancellationToken ct = default);
    /// <summary>로봇 현재 위치(도면 좌표) — 404 보고 없음·409 층/위치 없음·400 T_W_D 무효는 예외 메시지로.</summary>
    Task<RobotDrawingPoseDto> GetRobotDrawingPoseAsync(string robotId, CancellationToken ct = default)
        => throw new NotSupportedException();

    // ── 계획 자연어 어시스턴트 [ADR-013] — 기본 구현은 미지원(기존 테스트 대역 무수정) ──
    /// <summary>자연어 명령 → 변경안 미리보기(DB 무변경). 서버 비활성 503·LLM 실패 502는 {error} 메시지 예외.</summary>
    Task<PlanChangeSetDto> ProposePlanAsync(PlanProposeRequestDto request, CancellationToken ct = default) =>
        throw new NotSupportedException("계획 어시스턴트 미지원 클라이언트");
    /// <summary>미리보기한 변경안 적용(전부 또는 전무). 만료 404·동시 변경 409.</summary>
    Task<PlanApplyResultDto> ApplyPlanChangeSetAsync(Guid changeSetId, string userId, CancellationToken ct = default) =>
        throw new NotSupportedException("계획 어시스턴트 미지원 클라이언트");
    Task<LlmStatusDto?> GetLlmStatusAsync(CancellationToken ct = default) => Task.FromResult<LlmStatusDto?>(null);
    /// <summary>서버 리프트 사용 여부(<c>Acs:Lift:Enabled</c>). null = 모름(구서버) — 호출 측은 사용으로 본다.</summary>
    Task<bool?> GetLiftEnabledAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);
    /// <summary>서버 리프트 운영 방식(<c>Acs:Lift:Mode</c> — Off | Auto | Fixed). null = 모름(구서버) — 호출 측은 자동으로 본다.
    /// 기본 구현은 <see cref="GetLiftEnabledAsync"/> 로 미사용/자동만 구분한다.</summary>
    async Task<LiftSettingsDto?> GetLiftSettingsAsync(CancellationToken ct = default) =>
        await GetLiftEnabledAsync(ct) is { } on ? new LiftSettingsDto(on ? "Auto" : "Off", null) : null;
    /// <summary>정차 묶음 최적화(set cover) 시작 — 작업 번호. null = 구서버(미지원). 격자·반경은 null 이면 서버 기본(0.3 m·2.8 m).</summary>
    Task<Guid?> StartSetCoverAsync(string tankId, int level, string wallCode, double? gridStepM = null, double? reachRadiusM = null,
        CancellationToken ct = default) => Task.FromResult<Guid?>(null);
    /// <summary>정차 묶음 최적화 작업 상태·결과. null = 작업 없음.</summary>
    Task<SetCoverJobDto?> GetSetCoverAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult<SetCoverJobDto?>(null);
    /// <summary>정차 묶음 최적화 결과를 영역에 적용(그 면·층 영역을 다시 구성). 거부(409·400)는 예외 메시지에 사유. null = 구서버.</summary>
    Task<SetCoverApplyResultDto?> ApplySetCoverAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult<SetCoverApplyResultDto?>(null);
    /// <summary>정차 묶음 최적화 중지.</summary>
    Task CancelSetCoverAsync(Guid jobId, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>리치·간섭 화면의 리프트·차체 설정(지금 적용 중인 값). null = 구서버(미지원).</summary>
    Task<ReachSettingsDto?> GetReachSettingsAsync(CancellationToken ct = default) => Task.FromResult<ReachSettingsDto?>(null);
    /// <summary>설정 저장 — 서버가 바로 적용(재시작 불필요). 진행 중 run 이면 409, 범위 밖 400(예외 메시지에 사유). null = 구서버.</summary>
    Task<ReachSettingsDto?> SaveReachSettingsAsync(ReachSettingsUpdateDto settings, CancellationToken ct = default) => Task.FromResult<ReachSettingsDto?>(null);
    /// <summary>화면에서 덮어쓴 값을 모두 지움(appsettings.json 값으로). null = 구서버.</summary>
    Task<ReachSettingsDto?> ResetReachSettingsAsync(CancellationToken ct = default) => Task.FromResult<ReachSettingsDto?>(null);

    // ── 배터리 교체 장소 [HD_AMR 배터리관리 §6] — mapId당 1곳, 운영자 수동 디스패치 ──
    Task<IReadOnlyList<BatterySwapNodeDto>> GetBatterySwapNodesAsync(string? mapId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BatterySwapNodeDto>>(Array.Empty<BatterySwapNodeDto>());
    /// <summary>신규 교체 장소 등록. 서버가 nodeId 생성(요청에 포함 시 그 값), 중복 mapId 409.</summary>
    Task<BatterySwapNodeDto> CreateBatterySwapNodeAsync(string mapId, string name, double x, double y,
        double? theta, string? userId, CancellationToken ct = default) =>
        throw new NotSupportedException("배터리 교체 장소 미지원 클라이언트");
    Task DeleteBatterySwapNodeAsync(string nodeId, string? userId, CancellationToken ct = default) =>
        Task.CompletedTask;
    /// <summary>운영자 수동 교체 디스패치 — 현재 층 교체 장소로 Order 1건 발행. 활성 run 있으면 함께 abort.</summary>
    Task<BatterySwapDispatchResultDto?> DispatchBatterySwapAsync(string robotId, string? reason, string? userId,
        CancellationToken ct = default) => Task.FromResult<BatterySwapDispatchResultDto?>(null);

    /// <summary>통신 단절 일시정지 재개 — stopPause 1건 발행 [HD_AMR 통신단절 Fail-Safe §4.1]. 409=일시정지 아님/미연결.</summary>
    Task<ResumePauseResultDto?> ResumePauseAsync(string robotId, string? userId, CancellationToken ct = default) =>
        Task.FromResult<ResumePauseResultDto?>(null);

    // ── 미구현 백엔드 대비 (엔드포인트 추가 시 연결) ──────────
    // Task<IReadOnlyList<AlarmDto>> GetActiveAlarmsAsync(CancellationToken ct = default);
}
