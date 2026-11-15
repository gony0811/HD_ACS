using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>가스 측정 결과 저장 — 끝난 검사 액션의 결과가 가스 결과 본문이면 저장할 행을 만든다(아니면 null).</summary>
public static class GasResultRecorder
{
    public static GasResultEntity? Create(Guid runId, OrderActionEntity action, string? resultDescription, string robotId)
    {
        if (!SaigeGasResult.TryParse(resultDescription, out var body)) return null;
        return new GasResultEntity
        {
            ResultId = Guid.NewGuid(), ActionId = action.ActionId, RunId = runId, TaskId = action.TaskId,
            TankId = SaigeGasResult.TankOf(body), RobotId = robotId, Round = SaigeGasResult.RoundOf(body),
            Result = SaigeGasResult.ResultOf(body), PointCount = SaigeGasResult.PointCountOf(body),
            Body = body.ToJsonString(),
        };
    }
}

/// <summary>가스 결과 세이지 전송 설정 [Acs:Saige:Gas]. 주소·타임아웃은 Acs:Saige(BaseUrl·TimeoutSec)를 같이 쓴다.</summary>
public sealed class SaigeGasOptions
{
    /// <summary>false(기본)면 저장만 하고 보내지 않는다 — 켜면 쌓여 있던 PENDING 부터 순서대로 나간다.</summary>
    public bool Enabled { get; set; }
    public string ResultPath { get; set; } = "/admin/v3/external/gas/results";
    public int IntervalSec { get; set; } = 5;
    public int BatchSize { get; set; } = 20;
    public int MaxBackoffSec { get; set; } = 60;
}

/// <summary>운영 확인용 상태 — <c>GET /api/integrations/saige/gas</c>.</summary>
public sealed record SaigeGasStatus(bool Enabled, string Endpoint, int Pending, int Sent, int Rejected,
    DateTimeOffset? LastOkAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset? BackoffUntil);

/// <summary>
/// 가스 측정 결과 전송 ACS → SAIGE — <c>POST {BaseUrl}/admin/v3/external/gas/results</c>
/// [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.2 — 세이지 포밍가스 연동 규격 초안 v0.1 §2].
/// 로봇 상태 전송(최신 스냅샷만)과 달리 **결과는 한 건도 잃으면 안 되므로** DB(hist.gas_result)에 쌓아 두고 보낸다.
/// - 받은 순서대로 한 건씩. 2xx = SENT.
/// - 4xx(408·429 제외) = 세이지가 본문을 거부 — 다시 보내도 같으므로 REJECTED 로 두고 알람(다음 건은 계속 보낸다).
/// - 5xx·408·429·연결 불가·시간 초과 = 그대로 PENDING, 그 주기는 멈추고 백오프 후 같은 건부터 다시(순서 유지).
/// 검사 진행과 분리된 호스티드 서비스 — 전송 실패가 검사에 영향을 주지 않는다.
/// </summary>
public sealed class SaigeGasResultSender : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly SaigeGasOptions _opt;
    private readonly string _baseUrl;
    private readonly bool _taskIdLe;
    private readonly ILogger<SaigeGasResultSender> _log;

    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private int _backoffSec;
    private DateTimeOffset? _lastOkAt, _lastErrorAt;
    private string? _lastError;

    public SaigeGasResultSender(IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config,
        ILogger<SaigeGasResultSender> log)
    {
        _scopes = scopes; _http = http; _log = log;
        _opt = config.GetSection("Acs:Saige:Gas").Get<SaigeGasOptions>() ?? new SaigeGasOptions();
        _baseUrl = config["Acs:Saige:BaseUrl"] ?? "http://localhost:8080";
        _taskIdLe = config.GetValue("Acs:Saige:TaskIdLittleEndian", true);
    }

    internal bool InBackoff => DateTimeOffset.UtcNow < _backoffUntil;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("SAIGE 가스 결과 전송 비활성 (Acs:Saige:Gas:Enabled=false) — 결과는 저장만 한다.");
            return;
        }
        _log.LogInformation("SAIGE 가스 결과 전송 시작: {Url}{Path} · {Sec}s 주기", _baseUrl, _opt.ResultPath, _opt.IntervalSec);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opt.IntervalSec)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (InBackoff) continue;
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "SAIGE 가스 결과 전송 주기 처리 실패(DB 등) — 다음 주기 재시도."); }
        }
    }

    /// <summary>보낼 결과를 받은 순서대로 한 묶음 보낸다. 반환 = 이번에 SENT 가 된 수.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        var pending = await db.GasResults.Where(g => g.SendState == "PENDING")
            .OrderBy(g => g.CreatedAt).Take(Math.Max(1, _opt.BatchSize)).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var client = _http.CreateClient(SaigeHealthReporter.HttpClientName);
        int sent = 0;
        foreach (var row in pending)
        {
            var body = await BuildBodyAsync(db, row, ct);
            // 규격 검사 — 보내면 세이지가 400 으로 거부할 본문은 보내지 않고 사유를 남긴다(재전송 API 로 되살릴 수 있다)
            if (SaigeGasResult.Violations(body) is { Count: > 0 } violations)
            {
                row.SendState = "REJECTED";
                row.LastError = "규격 위반(전송 안 함): " + string.Join("; ", violations);
                _lastError = row.LastError; _lastErrorAt = DateTimeOffset.UtcNow;
                db.Alarms.Add(RejectAlarm(row, row.LastError));
                _log.LogWarning("가스 결과가 세이지 규격에 맞지 않아 보내지 않음 — task={Task}: {Error}", row.TaskId, row.LastError);
                await db.SaveChangesAsync(ct);
                continue;
            }
            row.SendAttempts++;
            HttpStatusCode? status = null;
            string? error = null;
            try
            {
                // Content-Length 명시(고정 길이) — 로봇 상태 전송과 같은 이유(chunked 본문을 못 읽는 수신기)
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var resp = await client.PostAsync(_opt.ResultPath, content, ct);
                status = resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                    error = $"HTTP {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync(ct))}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = ex.Message; }   // 연결 불가·시간 초과

            if (error is null)
            {
                row.SendState = "SENT"; row.SentAt = DateTimeOffset.UtcNow; row.LastError = null;
                _lastOkAt = row.SentAt; _backoffSec = 0;
                sent++;
                await db.SaveChangesAsync(ct);
                continue;
            }

            row.LastError = error;
            _lastError = error; _lastErrorAt = DateTimeOffset.UtcNow;
            bool rejected = status is { } code && (int)code is >= 400 and < 500
                            && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
            if (rejected)
            {
                // 본문을 세이지가 거부 — 다시 보내도 같다. 이 건만 빼고 다음 건을 계속 보낸다.
                row.SendState = "REJECTED";
                db.Alarms.Add(RejectAlarm(row, error));
                _log.LogWarning("SAIGE 가 가스 결과를 거부 — task={Task} round={Round}: {Error}", row.TaskId, row.Round, error);
                await db.SaveChangesAsync(ct);
                continue;
            }

            // 세이지 불능 — 순서를 지키려고 여기서 멈추고 백오프 후 같은 건부터 다시
            await db.SaveChangesAsync(ct);
            _backoffSec = Math.Min(Math.Max(1, _opt.MaxBackoffSec), _backoffSec == 0 ? Math.Max(1, _opt.IntervalSec) : _backoffSec * 2);
            _backoffUntil = DateTimeOffset.UtcNow.AddSeconds(_backoffSec);
            _log.LogWarning("SAIGE 가스 결과 전송 실패 — {Sec}s 뒤 재시도: {Error}", _backoffSec, error);
            break;
        }
        return sent;
    }

    private static AlarmEntity RejectAlarm(GasResultEntity row, string error) => new()
    {
        AlarmId = Guid.NewGuid(), AlarmCode = "SAIGE_BAD_REQUEST", RobotId = row.RobotId,
        Detail = JsonSerializer.Serialize(new
        {
            severity = "WARNING", title = "가스 측정 결과가 세이지로 전달되지 않음",
            taskId = row.TaskId, round = row.Round, result = row.Result, error,
        }),
        RaisedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>세이지로 보낼 본문 — taskId 는 세이지 조회 API 와 같은 표기, tankId·round 는 본문에 없을 때만 채운다.</summary>
    internal async Task<JsonObject> BuildBodyAsync(AcsDbContext db, GasResultEntity row, CancellationToken ct)
    {
        var body = JsonNode.Parse(row.Body)!.AsObject();
        int? round = row.Round ?? await db.ScenarioRuns.AsNoTracking().Where(r => r.RunId == row.RunId)
            .Select(r => r.Round).FirstOrDefaultAsync(ct);
        string? tank = row.TankId;
        if (tank is null)
            tank = await (from r in db.ScenarioRuns.AsNoTracking()
                          join s in db.Scenarios.AsNoTracking() on r.ScenarioId equals s.ScenarioId
                          where r.RunId == row.RunId
                          select s.TankId).FirstOrDefaultAsync(ct);
        return SaigeGasResult.ForSaige(body, row.TaskId, _taskIdLe, tank, round);
    }

    public async Task<SaigeGasStatus> StatusAsync(AcsDbContext db, CancellationToken ct)
    {
        var counts = await db.GasResults.AsNoTracking().GroupBy(g => g.SendState)
            .Select(g => new { State = g.Key, N = g.Count() }).ToListAsync(ct);
        int Of(string s) => counts.FirstOrDefault(c => c.State == s)?.N ?? 0;
        return new SaigeGasStatus(_opt.Enabled, _baseUrl.TrimEnd('/') + _opt.ResultPath, Of("PENDING"), Of("SENT"), Of("REJECTED"),
            _lastOkAt, _lastError, _lastErrorAt, InBackoff ? _backoffUntil : null);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
