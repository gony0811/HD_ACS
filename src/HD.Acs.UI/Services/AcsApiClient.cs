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

    private sealed record StartRunResult(Guid RunId);
    private sealed record ReleaseResult(bool Released);
}
