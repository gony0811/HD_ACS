using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using Serilog.Core;

namespace HD.Acs.App.Services;

/// <summary>SAIGE 와 주고받은 요청·응답 1건.</summary>
/// <param name="Direction">OUT = ACS → SAIGE, IN = SAIGE → ACS</param>
/// <param name="Status">HTTP 응답 코드. 연결 실패·시간 초과처럼 응답이 없으면 null</param>
/// <param name="Routine">주기적으로 되풀이되는 정상 전송(로봇 상태 5초 주기 성공) — 전용 파일에만 남기고 앱 로그에는 올리지 않는다</param>
public sealed record SaigeExchange(DateTimeOffset At, string Direction, string Method, string Url,
    string? RequestBody, int? Status, string? ResponseBody, long ElapsedMs, string? Error, bool Routine);

/// <summary>
/// SAIGE 송수신 기록 — ACS 가 SAIGE 로 보낸 것(가스 측정 결과·로봇 상태)과 SAIGE 가 ACS 로 보낸 것(임계값)의
/// **요청 본문과 응답(코드·본문)** 을 모두 남긴다.
/// - 전용 파일 <c>logs/saige-yyyyMMdd.log</c>(하루 1개, 31일 보관): 한 건도 빼지 않고 전부.
/// - 앱 로그: 되풀이되는 정상 상태 전송(5초마다)만 빼고 전부 — 앱 로그가 상태 전송으로 묻히지 않게.
/// - 메모리: 최근 200건(<c>GET /api/integrations/saige/traffic</c>).
/// 설정 [Acs:Saige]: TrafficLogEnabled(기본 true)·TrafficLogDir(기본 실행 폴더의 logs)·TrafficLogMaxBodyChars(기본 20000).
/// </summary>
public sealed class SaigeTrafficLog : IDisposable
{
    public const int RecentCapacity = 200;

    private readonly Logger? _file;
    private readonly ILogger<SaigeTrafficLog> _log;
    private readonly int _maxBody;
    private readonly ConcurrentQueue<SaigeExchange> _recent = new();

    public string? FilePath { get; }

    public SaigeTrafficLog(IConfiguration config, ILogger<SaigeTrafficLog> log)
    {
        _log = log;
        _maxBody = Math.Max(200, config.GetValue("Acs:Saige:TrafficLogMaxBodyChars", 20000));
        if (!config.GetValue("Acs:Saige:TrafficLogEnabled", true)) return;
        try
        {
            var dir = config["Acs:Saige:TrafficLogDir"];
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(AppContext.BaseDirectory, "logs");
            FilePath = Path.Combine(dir, "saige-.log");
            // 호스트 로그 파이프라인과 분리된 전용 로거 — 앱 로그 설정(레벨·싱크)에 좌우되지 않는다
            _file = new LoggerConfiguration()
                .WriteTo.File(FilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31, shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Message:lj}{NewLine}")
                .CreateLogger();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SAIGE 송수신 전용 로그 파일을 열지 못했습니다 — 앱 로그에만 남깁니다.");
        }
    }

    /// <summary>최근 기록(오래된 것부터).</summary>
    public IReadOnlyList<SaigeExchange> Recent => _recent.ToArray();

    public void Record(string direction, string method, string url, string? requestBody, int? status, string? responseBody,
        long elapsedMs, string? error = null, bool routine = false)
    {
        var x = new SaigeExchange(DateTimeOffset.Now, direction, method, url, Cut(requestBody), status, Cut(responseBody), elapsedMs, error, routine);
        _recent.Enqueue(x);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }

        string result = x.Status is int s ? $"HTTP {s}" : $"응답 없음({x.Error})";
        string line = $"[{x.Direction}] {x.Method} {x.Url} → {result} ({x.ElapsedMs}ms) | 요청: {x.RequestBody ?? "-"} | 응답: {x.ResponseBody ?? "-"}";
        _file?.Information("{Line}", line);
        if (x.Routine) return;
        if (x.Status is >= 200 and < 300) _log.LogInformation("SAIGE {Line}", line);
        else _log.LogWarning("SAIGE {Line}", line);
    }

    private string? Cut(string? s) =>
        s is null ? null : s.Length <= _maxBody ? s : s[.._maxBody] + $"…(전체 {s.Length}자)";

    public void Dispose() => _file?.Dispose();
}

/// <summary>
/// "saige" HttpClient 에 끼우는 처리기 — ACS → SAIGE 요청마다 본문·응답 코드·응답 본문·소요 시간을 기록한다.
/// 로봇 상태 전송의 정상 응답만 "되풀이 전송"으로 표시한다(전용 파일에는 남고 앱 로그에는 올리지 않음).
/// </summary>
public sealed class SaigeTrafficHandler(SaigeTrafficLog traffic, IConfiguration config) : DelegatingHandler
{
    private readonly string _healthPath = config["Acs:Saige:HealthPath"] ?? "/agent/v3/external/robot-health-check";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri?.ToString() ?? "";
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await base.SendAsync(request, ct);
            string? respBody = null;
            if (resp.Content is not null)
            {
                await resp.Content.LoadIntoBufferAsync();   // 호출자가 본문을 다시 읽을 수 있게 버퍼에 담는다
                respBody = await resp.Content.ReadAsStringAsync(ct);
            }
            bool routine = resp.IsSuccessStatusCode
                           && string.Equals(request.RequestUri?.AbsolutePath, _healthPath, StringComparison.OrdinalIgnoreCase);
            traffic.Record("OUT", request.Method.Method, url, body, (int)resp.StatusCode, respBody, sw.ElapsedMilliseconds, routine: routine);
            return resp;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            traffic.Record("OUT", request.Method.Method, url, body, null, null, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    }
}
