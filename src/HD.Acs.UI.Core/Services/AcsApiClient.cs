using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using HD.Acs.UI.Models;

namespace HD.Acs.UI.Services;

/// <summary>
/// typed HttpClient 기반 REST 클라이언트. BaseAddress는 DI(AddHttpClient)에서 AcsOptions로 주입한다.
/// System.Net.Http.Json 은 JsonSerializerOptions.Web(대소문자 무시)을 사용하므로 PascalCase DTO가 그대로 매핑된다.
/// </summary>
public sealed class AcsApiClient : IAcsApiClient
{
    private readonly HttpClient _http;

    public AcsApiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<RobotDto>> GetRobotsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<RobotDto>>("/api/robots", ct) ?? new();

    public async Task<RobotContextDto?> GetRobotContextAsync(string robotId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/robots/{robotId}/context", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RobotContextDto>(ct);
    }

    public async Task<IReadOnlyList<ScenarioSummaryDto>> GetScenariosAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<ScenarioSummaryDto>>("/api/scenarios", ct) ?? new();

    public async Task<ScenarioRunDto?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/runs/{runId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ScenarioRunDto>(ct);
    }

    public async Task<IReadOnlyList<WorkItemDto>> GetWorkItemsAsync(Guid runId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<WorkItemDto>>($"/api/runs/{runId}/work-items", ct) ?? new();

    public async Task<IReadOnlyList<TaskActionDto>> GetTaskActionsAsync(Guid runId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<TaskActionDto>>($"/api/runs/{runId}/task-actions", ct) ?? new();

    public async Task<RunProgressDto?> GetRunProgressAsync(Guid runId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/runs/{runId}/progress", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RunProgressDto>(ct);
    }

    public async Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/runs",
            new { ScenarioId = scenarioId, RobotId = robotId }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 409(활성 run 존재)·400의 {error} 메시지 노출
        var body = await resp.Content.ReadFromJsonAsync<StartRunResult>(ct);
        return body?.RunId ?? Guid.Empty;
    }

    public async Task AbortRunAsync(Guid runId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/runs/{runId}/abort", null, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
    }

    public async Task ResumeRunAsync(Guid runId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/runs/{runId}/resume", null, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 400(재개 불가)·409(다른 활성 run) 메시지 노출
    }

    public async Task GotoAsync(string robotId, int level, double xDrawing, double yDrawing, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/robots/{Uri.EscapeDataString(robotId)}/goto",
            new { Level = level, XDrawing = xDrawing, YDrawing = yDrawing, UserId = "operator" }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 409(진행 run/타층)·400(T_W_D)의 {error} 메시지 노출
    }

    public async Task<ResumableRunDto?> GetResumableRunAsync(string robotId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/runs/resumable?robotId={Uri.EscapeDataString(robotId)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ResumableRunDto>(ct);
    }

    public async Task<bool> ReleaseNextMissionAsync(Guid runId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/runs/{runId}/release-next", content: null, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ReleaseResult>(ct);
        return body?.Released ?? false;
    }

    public async Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        double x, double y, double theta, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/robots/{robotId}/zone",
            new { MapId = mapId, UserId = userId, X = x, Y = y, Theta = theta }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task EmergencyStopAsync(string robotId, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/robots/{robotId}/emergency-stop",
            new { UserId = userId }, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── 캘리브레이션 (T_W_D) [PHASE2 WP-1/5a] ──────────
    public async Task<CalibrationPointDto> CaptureCalibrationPointAsync(string mapId,
        double drawingX, double drawingY, string unit, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/maps/{mapId}/calibration/points",
            new { DrawingX = drawingX, DrawingY = drawingY, Unit = unit, UserId = userId }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 404/409의 {error} 메시지를 예외로 노출
        return (await resp.Content.ReadFromJsonAsync<CalibrationPointDto>(ct))!;
    }

    public async Task<IReadOnlyList<CalibrationPointDto>> GetCalibrationPointsAsync(string mapId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/maps/{mapId}/calibration/points", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<CalibrationPointDto>();
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<CalibrationPointDto>>(ct) ?? new();
    }

    public async Task DeleteCalibrationPointAsync(string mapId, Guid pointId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/maps/{mapId}/calibration/points/{pointId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<CalibrationSolveResultDto> SolveCalibrationAsync(string mapId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/maps/{mapId}/calibration/solve", content: null, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 404/400(<2점)의 {error} 메시지를 예외로 노출
        return (await resp.Content.ReadFromJsonAsync<CalibrationSolveResultDto>(ct))!;
    }

    public async Task<MapCalibrationDto?> GetCalibrationAsync(string mapId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/maps/{mapId}/calibration", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<MapCalibrationDto>(ct);
    }

    // ── 슬라이싱/TASK (전개도) [PHASE2 WP-5b] ──────────
    public async Task<Guid> CreateScenarioAsync(string name, string tankId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/scenarios", new { Name = name, TankId = tankId }, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdResult>(ct))?.ScenarioId ?? Guid.Empty;
    }

    public async Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/scenarios/{scenarioId}", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 409(참조 run 존재)의 {error} 메시지 노출
    }

    public async Task<IReadOnlyList<ScenarioAreaDto>> GetScenarioAreasAsync(Guid scenarioId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/scenarios/{scenarioId}/areas", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<ScenarioAreaDto>();
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<ScenarioAreaDto>>(ct) ?? new();
    }

    public async Task SetScenarioAreasAsync(Guid scenarioId, IReadOnlyList<Guid> areaIds, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync($"/api/scenarios/{scenarioId}/areas", new { AreaIds = areaIds }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 400(미존재/타 선창 영역)의 {error} 메시지 노출
    }

    public async Task<Guid> CreateSeamAsync(string tankId, int level, string wallCode, string seamType,
        double[][] pathDrawing, double[] normalDrawing, string sectionDxfId, string profileId,
        string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/seams", new
        {
            TankId = tankId, Level = level, WallCode = wallCode, SeamType = seamType,
            PathDrawing = pathDrawing, NormalDrawing = normalDrawing,
            SectionDxfId = sectionDxfId, ProfileId = profileId, UserId = userId
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<IdResult>(ct))?.SeamId ?? Guid.Empty;
    }

    public async Task<IReadOnlyList<SeamDto>> GetSeamsAsync(string tankId, int? level = null, CancellationToken ct = default)
    {
        var url = $"/api/seams?tankId={Uri.EscapeDataString(tankId)}" + (level is int l ? $"&level={l}" : "");
        return await _http.GetFromJsonAsync<List<SeamDto>>(url, ct) ?? new();
    }

    public async Task DeleteSeamAsync(Guid seamId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/seams/{seamId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<(int Stations, int Tasks)> GenerateFromSeamsAsync(Guid scenarioId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/scenarios/{scenarioId}/generate-from-seams", content: null, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 400(유효 T_W_D 없음) 서버 메시지 노출
        var r = await resp.Content.ReadFromJsonAsync<GenerateResult>(ct);
        return (r?.Stations ?? 0, r?.Tasks ?? 0);
    }

    public async Task<IReadOnlyList<SlicedStationDto>> GetStationsAsync(Guid scenarioId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<SlicedStationDto>>($"/api/scenarios/{scenarioId}/stations", ct) ?? new();

    // ── 선창 3D 정의 [SPEC v3 §2/§3] — 파라미터 등록 → 면 자동생성 ──────────
    public async Task<int> RegisterTankGeometryAsync(string tankId, double lengthL, double wFloor, double thetaLowDeg,
        double hLow, double hWall, double thetaUpDeg, double hUp, double[] levelZ,
        double originOx, double originOy, string userId,
        double? reachZMin = null, double? reachZMax = null, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/tanks/{Uri.EscapeDataString(tankId)}/geometry", new
        {
            LengthL = lengthL, WFloor = wFloor, ThetaLowDeg = thetaLowDeg, HLow = hLow,
            HWall = hWall, ThetaUpDeg = thetaUpDeg, HUp = hUp,
            LevelZ = levelZ, OriginOx = originOx, OriginOy = originOy, UserId = userId,
            ReachZMin = reachZMin, ReachZMax = reachZMax
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 검증 실패 400 reasons 메시지 노출
        return (await resp.Content.ReadFromJsonAsync<GeometryResult>(ct))?.WallsGenerated ?? 0;
    }

    public async Task<TankGeometryDto?> GetTankGeometryAsync(string tankId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/tanks/{Uri.EscapeDataString(tankId)}/geometry", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TankGeometryDto>(ct);
    }

    public async Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default)
    {
        var url = $"/api/tanks/{Uri.EscapeDataString(tankId)}/walls" + (level is int l ? $"?level={l}" : "");
        return await _http.GetFromJsonAsync<List<WallDto>>(url, ct) ?? new();
    }

    // ── 영역·검사 작업 [SPEC v3 §4] — 벽면-로컬 (u,v). v3.1: level은 서버가 유도(응답에 유도 층) ──────────
    public async Task<(Guid AreaId, int Level)> CreateAreaAsync(string tankId, string wallCode, string name,
        double[][] corners,
        double? stationX, double? stationY, double? stationTheta, string userId,
        double? stationStandoffM = null, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/areas", new
        {
            TankId = tankId, WallCode = wallCode, Name = name,
            Corners = corners,
            StationX = stationX, StationY = stationY, StationTheta = stationTheta, UserId = userId,
            StationStandoffM = stationStandoffM
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 면범위 400·층유도실패 400·중복 409·면없음 404 메시지 노출
        var r = await resp.Content.ReadFromJsonAsync<IdResult>(ct);
        return (r?.AreaId ?? Guid.Empty, r?.Level ?? 0);
    }

    public async Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, string? wallCode = null, int? level = null, CancellationToken ct = default)
    {
        var url = $"/api/areas?tankId={Uri.EscapeDataString(tankId)}"
                  + (wallCode is not null ? $"&wallCode={Uri.EscapeDataString(wallCode)}" : "")
                  + (level is int l ? $"&level={l}" : "");
        return await _http.GetFromJsonAsync<List<AreaDto>>(url, ct) ?? new();
    }

    public async Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/areas/{areaId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<int> CreateAreaTaskAsync(Guid areaId, double startU, double startV, double endU, double endV,
        string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/areas/{areaId}/tasks", new
        {
            StartU = startU, StartV = startV, EndU = endU, EndV = endV,
            SeamType = seamType, SectionDxfId = sectionDxfId, ProfileId = profileId, UserId = userId
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 경계 밖 400 메시지 노출
        return (await resp.Content.ReadFromJsonAsync<AreaTaskResult>(ct))?.Seq ?? 0;
    }

    public async Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AreaTaskDto>>($"/api/areas/{areaId}/tasks", ct) ?? new();

    public async Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/area-tasks/{taskId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>비성공 응답이면 서버 {error} 필드를 담아 예외를 던진다(캡처 409 / solve 400 등 UX 메시지).</summary>
    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorBody>(ct);
            message = err?.Error;
        }
        catch (Exception) { /* 본문이 JSON이 아니면 상태코드로 폴백 */ }
        throw new HttpRequestException(message ?? $"요청 실패 ({(int)resp.StatusCode})");
    }

    private sealed record StartRunResult(Guid RunId);
    private sealed record ReleaseResult(bool Released);
    private sealed record ErrorBody(string? Error);
    private sealed record IdResult(Guid ScenarioId, Guid SeamId, Guid AreaId, int Level);
    private sealed record AreaTaskResult(Guid TaskId, int Seq);
    private sealed record GenerateResult(int Stations, int Tasks);
    private sealed record GeometryResult(int WallsGenerated);
}
