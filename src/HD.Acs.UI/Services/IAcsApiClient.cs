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

    // ── 명령 ──────────────────────────────────────────────
    Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default);
    Task<bool> ReleaseNextMissionAsync(Guid runId, CancellationToken ct = default);
    Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        double x, double y, double theta, CancellationToken ct = default);
    Task EmergencyStopAsync(string robotId, string userId, CancellationToken ct = default);

    // ── 도면→맵 캘리브레이션 (T_W_D) [PHASE2 WP-1/5a] ──────────
    Task<CalibrationPointDto> CaptureCalibrationPointAsync(string mapId,
        double drawingX, double drawingY, string unit, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<CalibrationPointDto>> GetCalibrationPointsAsync(string mapId, CancellationToken ct = default);
    Task DeleteCalibrationPointAsync(string mapId, Guid pointId, CancellationToken ct = default);
    Task<CalibrationSolveResultDto> SolveCalibrationAsync(string mapId, CancellationToken ct = default);
    Task<MapCalibrationDto?> GetCalibrationAsync(string mapId, CancellationToken ct = default);

    // ── 슬라이싱/TASK (전개도) [PHASE2 WP-5b] ──────────
    Task<Guid> CreateScenarioAsync(string name, string tankId, CancellationToken ct = default);
    // 참조하는 run이 있으면 서버가 409(메시지 포함)로 거부한다.
    Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct = default);
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
    Task<TankGeometryDto?> GetTankGeometryAsync(string tankId, CancellationToken ct = default);
    // v3.1 §8: level 지정 시 그 층 도달 가능 면만 + 면별 reachableVBand 반환.
    Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default);

    // ── 영역·검사 작업 [SPEC v3 §4] — 벽면-로컬 (u,v). v3.1: level은 서버가 유도(반환값에 유도 층) ──────────
    // corners = 임의 4점 사각형 [[u,v]…]. 서버가 bbox 유도.
    Task<(Guid AreaId, int Level)> CreateAreaAsync(string tankId, string wallCode, string name,
        double[][] corners,
        double? stationX, double? stationY, double? stationTheta, string userId,
        double? stationStandoffM = null, CancellationToken ct = default);
    Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, string? wallCode = null, int? level = null, CancellationToken ct = default);
    Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default);
    Task<int> CreateAreaTaskAsync(Guid areaId, double startU, double startV, double endU, double endV,
        string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default);
    Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default);

    // ── 미구현 백엔드 대비 (엔드포인트 추가 시 연결) ──────────
    // Task<IReadOnlyList<AlarmDto>> GetActiveAlarmsAsync(CancellationToken ct = default);
}
