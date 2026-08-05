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

    // ── 도면→맵 캘리브레이션 (T_W_D) [PHASE2 WP-1/5a] ──────────
    Task<CalibrationPointDto> CaptureCalibrationPointAsync(string mapId,
        double drawingX, double drawingY, string unit, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<CalibrationPointDto>> GetCalibrationPointsAsync(string mapId, CancellationToken ct = default);
    Task DeleteCalibrationPointAsync(string mapId, Guid pointId, CancellationToken ct = default);
    Task<CalibrationSolveResultDto> SolveCalibrationAsync(string mapId, CancellationToken ct = default);
    Task<MapCalibrationDto?> GetCalibrationAsync(string mapId, CancellationToken ct = default);

    // ── 슬라이싱/TASK (전개도) [PHASE2 WP-5b] ──────────
    Task<Guid> CreateScenarioAsync(string name, string tankId, CancellationToken ct = default);
    Task<Guid> CreateSeamAsync(string tankId, int level, string wallCode, string seamType,
        double[][] pathDrawing, double[] normalDrawing, string sectionDxfId, string profileId,
        string userId, CancellationToken ct = default);
    Task<IReadOnlyList<SeamDto>> GetSeamsAsync(string tankId, int? level = null, CancellationToken ct = default);
    Task DeleteSeamAsync(Guid seamId, CancellationToken ct = default);
    Task<(int Stations, int Tasks)> GenerateFromSeamsAsync(Guid scenarioId, CancellationToken ct = default);
    Task<IReadOnlyList<SlicedStationDto>> GetStationsAsync(Guid scenarioId, CancellationToken ct = default);

    // ── 벽면(Wall) LAYER [정차각 자동화] — 벽면 레지스트리·티칭 키 (정차각 저장 안 함) ──────────
    Task CreateWallAsync(string tankId, int level, string wallCode, string? description,
        string userId, CancellationToken ct = default);
    Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default);
    Task DeleteWallAsync(string tankId, int level, string wallCode, CancellationToken ct = default);

    // ── 영역(Area) LAYER [PHASE2 개정] — 법선은 벽면에서 상속(입력 없음) ──────────
    Task<Guid> CreateAreaAsync(string tankId, int level, string wallCode, string name,
        double minX, double minY, double maxX, double maxY,
        double? stationX, double? stationY, double? stationTheta, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, int? level = null, string? wallCode = null, CancellationToken ct = default);
    Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default);
    Task<int> CreateAreaTaskAsync(Guid areaId, double[] seamStart, double[] seamEnd,
        string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default);
    Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<(int Stations, int Tasks)> GenerateFromAreasAsync(Guid scenarioId, CancellationToken ct = default);

    // ── 미구현 백엔드 대비 (엔드포인트 추가 시 연결) ──────────
    // Task<IReadOnlyList<AlarmDto>> GetActiveAlarmsAsync(CancellationToken ct = default);
}
