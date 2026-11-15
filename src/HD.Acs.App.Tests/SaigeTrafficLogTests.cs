using System.Net;
using System.Text;
using HD.Acs.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// SAIGE 송수신 기록 — 보낸 본문과 응답(코드·본문)을 전용 파일과 앱 로그에 남긴다.
/// 5초마다 되풀이되는 정상 상태 전송은 전용 파일에만 남기고 앱 로그에는 올리지 않는다.
/// </summary>
public class SaigeTrafficLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdacs-saige-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 */ } }

    private sealed class ListLogger : ILogger<SaigeTrafficLog>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
            Lines.Add((level, format(state, ex)));
    }

    private sealed class FakeSaige(Func<HttpRequestMessage, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request) ?? throw new HttpRequestException("연결 거부"));
    }

    private (HttpClient Client, SaigeTrafficLog Log, ListLogger App) Build(Func<HttpRequestMessage, HttpResponseMessage?> respond)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Saige:TrafficLogDir"] = _dir, ["Acs:Saige:TrafficLogMaxBodyChars"] = "300",
            ["Acs:Saige:HealthPath"] = "/admin/v3/external/robot-health-check",
        }).Build();
        var app = new ListLogger();
        var log = new SaigeTrafficLog(cfg, app);
        var handler = new SaigeTrafficHandler(log, cfg) { InnerHandler = new FakeSaige(respond) };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://saige.test") }, log, app);
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
    private static HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code) { Content = Json(body) };

    private string FileText()
    {
        var file = Directory.GetFiles(_dir, "saige-*.log").Single();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task GasResult_RequestAndResponse_AreLoggedToFileAndAppLog()
    {
        var (client, log, app) = Build(_ => Reply(HttpStatusCode.OK, """{"status":200,"message":"success","timestamp":1762151786000}"""));

        var resp = await client.PostAsync("/admin/v3/external/gas/results", Json("""{"tankId":"CT1","round":1,"result":"NG"}"""));

        Assert.Contains("success", await resp.Content.ReadAsStringAsync());   // 기록한 뒤에도 호출자가 응답 본문을 읽을 수 있다
        var x = Assert.Single(log.Recent);
        Assert.Equal(("OUT", "POST", "http://saige.test/admin/v3/external/gas/results", 200, false),
            (x.Direction, x.Method, x.Url, x.Status!.Value, x.Routine));
        Assert.Contains("\"result\":\"NG\"", x.RequestBody);
        Assert.Contains("success", x.ResponseBody);

        var (level, text) = Assert.Single(app.Lines);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("HTTP 200", text);
        Assert.Contains("\"round\":1", text);      // 보낸 본문
        Assert.Contains("\"message\":\"success\"", text);   // 받은 응답
        log.Dispose();
        Assert.Contains("[OUT] POST http://saige.test/admin/v3/external/gas/results → HTTP 200", FileText());
    }

    /// <summary>거부(4xx)·오류(5xx) 응답은 본문까지 경고로 남는다 — 세이지가 왜 거부했는지가 로그에 있어야 한다.</summary>
    [Fact]
    public async Task RejectedResponse_IsLoggedAsWarning_WithSaigeMessage()
    {
        var (client, log, app) = Build(_ => Reply(HttpStatusCode.Conflict, """{"status":409,"code":40901,"message":"inspection not started"}"""));

        await client.PostAsync("/admin/v3/external/gas/results", Json("""{"tankId":"CT1"}"""));

        var (level, text) = Assert.Single(app.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("HTTP 409", text);
        Assert.Contains("40901", text);
        Assert.Equal(409, Assert.Single(log.Recent).Status);
    }

    /// <summary>되풀이되는 정상 상태 전송은 전용 파일·최근 기록에는 남고 앱 로그에는 올리지 않는다. 실패하면 앱 로그에도 남는다.</summary>
    [Fact]
    public async Task RoutineHealthCheck_GoesToFileOnly_ButFailuresReachAppLog()
    {
        bool ok = true;
        var (client, log, app) = Build(_ => ok ? Reply(HttpStatusCode.OK, """{"status":200}""") : Reply(HttpStatusCode.InternalServerError, """{"code":50001}"""));

        await client.PostAsync("/admin/v3/external/robot-health-check", Json("""{"robotId":"AMR-01","status":"IDLE"}"""));
        Assert.Empty(app.Lines);
        Assert.True(Assert.Single(log.Recent).Routine);

        ok = false;
        await client.PostAsync("/admin/v3/external/robot-health-check", Json("""{"robotId":"AMR-01","status":"IDLE"}"""));
        Assert.Equal(LogLevel.Warning, Assert.Single(app.Lines).Level);
        Assert.Equal(new[] { true, false }, log.Recent.Select(x => x.Routine));

        log.Dispose();
        var file = FileText();
        Assert.Equal(2, file.Split('\n').Count(l => l.Contains("robot-health-check")));   // 파일에는 둘 다
    }

    /// <summary>응답이 없을 때(연결 거부·시간 초과)도 보낸 본문과 사유를 남기고, 예외는 호출자에게 그대로 올린다.</summary>
    [Fact]
    public async Task NoResponse_IsLoggedWithReason_AndStillThrows()
    {
        var (client, log, app) = Build(_ => null);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("/admin/v3/external/gas/results", Json("""{"tankId":"CT1"}""")));

        var x = Assert.Single(log.Recent);
        Assert.Null(x.Status);
        Assert.Contains("연결 거부", x.Error);
        Assert.Contains("\"tankId\":\"CT1\"", x.RequestBody);
        Assert.Equal(LogLevel.Warning, Assert.Single(app.Lines).Level);
        Assert.Contains("응답 없음", app.Lines[0].Text);
    }

    [Fact]
    public void LongBodies_AreCut_AndRecentIsCapped_AndIncomingIsRecorded()
    {
        var (_, log, app) = Build(_ => null);

        log.Record("IN", "PUT", "/api/gas/threshold (from 10.0.0.5)", new string('x', 1000), 200, """{"tankId":"CT1","value":4.5}""", 12);
        var x = Assert.Single(log.Recent);
        Assert.Equal("IN", x.Direction);
        Assert.EndsWith("…(전체 1000자)", x.RequestBody);
        Assert.Equal(300 + "…(전체 1000자)".Length, x.RequestBody!.Length);
        Assert.Contains("[IN] PUT /api/gas/threshold", Assert.Single(app.Lines).Text);

        for (int i = 0; i < SaigeTrafficLog.RecentCapacity + 50; i++) log.Record("OUT", "POST", "/x", null, 200, null, 1, routine: true);
        Assert.Equal(SaigeTrafficLog.RecentCapacity, log.Recent.Count);
    }
}
