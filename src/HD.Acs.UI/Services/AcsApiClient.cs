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

    public async Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/runs",
            new { ScenarioId = scenarioId, RobotId = robotId }, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<StartRunResult>(ct);
        return body?.RunId ?? Guid.Empty;
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

    // ── 벽면(Wall) LAYER [정차각 자동화] — 벽면 레지스트리·티칭 키 (정차각 저장 안 함) ──────────
    public async Task CreateWallAsync(string tankId, int level, string wallCode, string? description,
        string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/walls", new
        {
            TankId = tankId, Level = level, WallCode = wallCode, Description = description, UserId = userId
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
    }

    public async Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default)
    {
        var url = $"/api/walls?tankId={Uri.EscapeDataString(tankId)}" + (level is int l ? $"&level={l}" : "");
        return await _http.GetFromJsonAsync<List<WallDto>>(url, ct) ?? new();
    }

    public async Task DeleteWallAsync(string tankId, int level, string wallCode, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(
            $"/api/walls/{Uri.EscapeDataString(tankId)}/{level}/{Uri.EscapeDataString(wallCode)}", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 참조 영역 존재 시 409 메시지 노출
    }

    // ── 영역(Area) LAYER [PHASE2 개정] — 법선은 벽면에서 상속(입력 없음) ──────────
    public async Task<Guid> CreateAreaAsync(string tankId, int level, string wallCode, string name,
        double minX, double minY, double maxX, double maxY,
        double? stationX, double? stationY, double? stationTheta, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/areas", new
        {
            TankId = tankId, Level = level, WallCode = wallCode, Name = name,
            MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            StationX = stationX, StationY = stationY, StationTheta = stationTheta, UserId = userId
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 경계 400·미등록 벽면 400·중복 409 메시지 노출
        return (await resp.Content.ReadFromJsonAsync<IdResult>(ct))?.AreaId ?? Guid.Empty;
    }

    public async Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, int? level = null, string? wallCode = null, CancellationToken ct = default)
    {
        var url = $"/api/areas?tankId={Uri.EscapeDataString(tankId)}"
                  + (level is int l ? $"&level={l}" : "")
                  + (wallCode is not null ? $"&wallCode={Uri.EscapeDataString(wallCode)}" : "");
        return await _http.GetFromJsonAsync<List<AreaDto>>(url, ct) ?? new();
    }

    public async Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/areas/{areaId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<int> CreateAreaTaskAsync(Guid areaId, double[] seamStart, double[] seamEnd,
        string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/areas/{areaId}/tasks", new
        {
            SeamStart = seamStart, SeamEnd = seamEnd, SeamType = seamType,
            SectionDxfId = sectionDxfId, ProfileId = profileId, UserId = userId
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

    public async Task<(int Stations, int Tasks)> GenerateFromAreasAsync(Guid scenarioId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/scenarios/{scenarioId}/generate-from-areas", content: null, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 400 유효 T_W_D 없음 메시지 노출
        var r = await resp.Content.ReadFromJsonAsync<GenerateResult>(ct);
        return (r?.Stations ?? 0, r?.Tasks ?? 0);
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
    private sealed record IdResult(Guid ScenarioId, Guid SeamId, Guid AreaId);
    private sealed record AreaTaskResult(Guid TaskId, int Seq);
    private sealed record GenerateResult(int Stations, int Tasks);
}
