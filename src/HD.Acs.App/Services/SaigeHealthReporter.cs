using System.Text.Json;
using HD.Acs.Core.Integration;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>SAIGE 연동 설정 [Acs:Saige]. Enabled=false(기본)면 전송하지 않는다 — 폐쇄망 현장에서 주소 확정 후 켠다.</summary>
public sealed class SaigeOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string HealthPath { get; set; } = "/agent/v3/external/robot-health-check";
    public int IntervalSec { get; set; } = 5;            // 로봇 1대당 전송 주기 [§9.4]
    public int TimeoutSec { get; set; } = 3;
    public int MaxBackoffSec { get; set; } = 60;         // 503 백오프 상한 [§9.5]
    public int AlarmAfterFailures { get; set; } = 12;    // 연속 실패 임계 — 초과 시 운영 알람 1회
    public int InternalErrorRetries { get; set; } = 2;   // 500(50001) 제한적 재시도 횟수
}

/// <summary>
/// 로봇 상태 전송 ACS → SAIGE [SAIGE 연동 사양서 v2.6 §9]. POST {BaseUrl}/agent/v3/external/robot-health-check.
/// - 주기마다 DB의 **최신 스냅샷을 새로 만들어** 전송한다 — 큐를 쌓지 않으므로 SAIGE 장애가 길어져도
///   밀린 과거 값이 몰려 나가지 않는다 [§9.4]. 재시도도 항상 최신 스냅샷이 대상이다 [§9.5].
/// - 검사 진행(VDA 브릿지·디스패처)과 분리된 독립 호스티드 서비스 — 전송 실패가 검사에 영향을 주지 않는다.
/// - 위치는 도면 좌표(mm)여야 하므로 보고 층의 유효 T_W_D가 없으면 그 로봇은 전송을 보류한다
///   (원시 SLAM 좌표를 도면 좌표인 척 보내는 조용한 오표시 금지).
/// </summary>
public sealed class SaigeHealthReporter : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly RobotErrorTracker _errors;
    private readonly SaigeOptions _opt;
    private readonly ILogger<SaigeHealthReporter> _log;

    private int _consecutiveFailures;
    private bool _alarmRaised;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private readonly HashSet<string> _positionWarned = new();   // 위치 산출 불가 경고 edge (로봇별 1회)

    // 운영 확인용 상태 스냅샷 — 매 전송/보류/실패마다 통째로 교체(불변 레코드, volatile 참조 스왑 = 락 없이 읽기 안전).
    private volatile SaigeLinkStatus _status;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SaigeRobotLink> _robots = new();

    public const string HttpClientName = "saige";

    /// <summary>현재 연동 상태 — <c>GET /api/integrations/saige</c>. 정상 전송은 로그를 남기지 않으므로 이것이 "보내고 있다"의 근거다.</summary>
    public SaigeLinkStatus Status => _status;

    internal bool InBackoff => DateTimeOffset.UtcNow < _backoffUntil;
    internal int ConsecutiveFailures => _consecutiveFailures;

    public SaigeHealthReporter(IServiceScopeFactory scopes, IHttpClientFactory http,
        RobotErrorTracker errors, IConfiguration config, ILogger<SaigeHealthReporter> log)
    {
        _scopes = scopes; _http = http; _errors = errors; _log = log;
        _opt = config.GetSection("Acs:Saige").Get<SaigeOptions>() ?? new SaigeOptions();
        _status = SaigeLinkStatus.Initial(_opt);
    }

    private void Publish(Func<SaigeLinkStatus, SaigeLinkStatus> change) =>
        _status = change(_status) with { Robots = _robots.Values.OrderBy(r => r.RobotId).ToArray() };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("SAIGE 로봇 상태 전송 비활성 (Acs:Saige:Enabled=false).");
            return;
        }
        _log.LogInformation("SAIGE 로봇 상태 전송 시작: {Url}{Path} · {Sec}s 주기", _opt.BaseUrl, _opt.HealthPath, _opt.IntervalSec);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opt.IntervalSec)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (InBackoff) continue;   // 503 백오프 중 — 이번 주기 건너뜀
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "SAIGE 전송 주기 처리 실패(DB 등) — 다음 주기 재시도."); }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();

        var robots = await db.Robots.AsNoTracking().Select(r => r.RobotId).ToListAsync(ct);
        foreach (var robotId in robots)   // 다중 로봇 = 로봇마다 개별 요청 [§9.4]
        {
            var payload = await BuildSnapshotAsync(db, robotId, ct);
            if (payload is null) continue;
            var outcome = await SendAsync(payload, ct);
            _robots[robotId] = new SaigeRobotLink(robotId, DateTimeOffset.UtcNow, payload.Status,
                payload.Position.Level, payload.Position.X, payload.Position.Y, payload.Battery,
                outcome.Kind == SendKind.Ok ? "OK" : outcome.Detail ?? outcome.Kind.ToString(), HoldReason: null);
            await ApplyOutcomeAsync(db, robotId, outcome, ct);
            if (outcome.Kind is SendKind.Backoff) break;   // SAIGE 자체가 불능 — 나머지 로봇도 이번 주기 생략
        }
    }

    /// <summary>로봇 1대의 최신 스냅샷 [§9.1]. 도면 위치를 산출할 수 없으면 null(전송 보류).</summary>
    internal async Task<SaigeRobotHealth?> BuildSnapshotAsync(AcsDbContext db, string robotId, CancellationToken ct)
    {
        var ctx = await db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId, ct);
        if (ctx?.ReportedMapId is not { } mapId || ctx.ReportedX is not double mx || ctx.ReportedY is not double my)
            return Hold(robotId, "로봇 위치 보고 없음");
        if (SaigeUnits.LevelFromMapId(mapId) is not int level)
            return Hold(robotId, $"mapId '{mapId}'에서 층 번호를 유도할 수 없음");

        var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct);
        var cal = await db.MapCalibrations.AsNoTracking().Where(c => c.MapId == mapId)
            .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync(ct);
        if (map is null) return Hold(robotId, $"map '{mapId}' 미등록");
        Core.Geometry.DrawingTransform tWd;
        try { tWd = WeldInspectionPayload.ResolveTransform(map.Version, cal?.MapVersion, cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0); }
        catch (CalibrationInvalidException ex) { return Hold(robotId, ex.Message); }
        _positionWarned.Remove(robotId);

        // 최근 run 기준 — 수행 중 정차 / 중단(잔여 보유) 여부
        var run = await db.ScenarioRuns.AsNoTracking().Where(r => r.RobotId == robotId)
            .OrderByDescending(r => r.StartedAt).Select(r => new { r.RunId, r.State }).FirstOrDefaultAsync(ct);
        bool dispatched = run is not null && run.State == "RUNNING" &&
            await db.WorkItems.AsNoTracking().AnyAsync(w => w.RunId == run.RunId && w.Status == "DISPATCHED", ct);
        bool aborted = run is not null && run.State == "ABORTED" &&
            await db.WorkItems.AsNoTracking().AnyAsync(w => w.RunId == run.RunId &&
                (w.Status == "PENDING" || w.Status == "DISPATCHED"), ct);

        var status = RobotHealth.Derive(new RobotHealthInput(
            ctx.ConnectionState, _errors.HasActiveErrors(robotId), aborted, dispatched));
        var (x, y, yaw) = RobotHealth.ToDrawingPosition(tWd, mx, my, ctx.ReportedTheta);

        return new SaigeRobotHealth(
            RobotId: robotId,
            Status: status.ToString(),
            Position: new SaigeRobotPosition(level, x, y, yaw),
            Battery: (int)Math.Round(Math.Clamp(ctx.BatteryPct ?? 0, 0, 100)),
            Rssi: -1,   // 로봇 보고 항목에 통신 감도 없음 — 미측정 고정값 [§9.3]
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private SaigeRobotHealth? Hold(string robotId, string reason)
    {
        if (_positionWarned.Add(robotId))
            _log.LogWarning("SAIGE 전송 보류 — robot={Robot}: {Reason} (해소되면 자동 재개)", robotId, reason);
        var prev = _robots.TryGetValue(robotId, out var p) ? p : null;
        _robots[robotId] = new SaigeRobotLink(robotId, prev?.LastSentAt, prev?.LastStatus, prev?.Level, prev?.X, prev?.Y, prev?.Battery,
            prev?.LastResult, HoldReason: reason);
        Publish(s => s);
        return null;
    }

    private async Task<SendOutcome> SendAsync(SaigeRobotHealth payload, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // 본문을 미리 직렬화해 **Content-Length를 명시**한다. PostAsJsonAsync(JsonContent)는 길이를 모르는 스트림이라
                // Transfer-Encoding: chunked 로 나가는데, Content-Length 만 읽는 수신기에서는 본문이 빈 것으로 처리된다
                // (E2E에서 확인 — 2026-09-21). 수신기 구현에 기대지 않도록 고정 길이로 보낸다.
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, SaigeJson.Options));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var resp = await client.PostAsync(_opt.HealthPath, content, ct);
                if (resp.IsSuccessStatusCode) return new SendOutcome(SendKind.Ok, null);

                int http = (int)resp.StatusCode;
                int? code = await TryReadCodeAsync(resp, ct);
                string detail = $"HTTP {http}" + (code is int c ? $" / code {c}" : "");
                // [§9.5] 400=형식 오류(재시도 금지) · 503=기동 중/미응답(백오프) · 500=내부 오류(제한적 재시도)
                if (http == 400) return new SendOutcome(SendKind.Rejected, detail);
                if (http == 503) return new SendOutcome(SendKind.Backoff, detail);
                if (attempt < _opt.InternalErrorRetries) { await Task.Delay(300, ct); continue; }
                return new SendOutcome(SendKind.Failed, detail);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // 연결 불가·타임아웃 = SAIGE 미응답 → 50302와 동일하게 백오프
                return new SendOutcome(SendKind.Backoff, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static async Task<int?> TryReadCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task ApplyOutcomeAsync(AcsDbContext db, string robotId, SendOutcome o, CancellationToken ct)
    {
        switch (o.Kind)
        {
            case SendKind.Ok:
                if (_consecutiveFailures > 0)
                    _log.LogInformation("SAIGE 전송 복구 (연속 실패 {N}회 후).", _consecutiveFailures);
                _consecutiveFailures = 0; _alarmRaised = false; _backoffUntil = DateTimeOffset.MinValue;
                // 정상 전송은 Debug — 평소엔 조용, 추적 시 Serilog MinimumLevel 을 Debug 로 내리면 매 건 보인다.
                if (_robots.TryGetValue(robotId, out var r))
                    _log.LogDebug("SAIGE 전송 OK — robot={Robot} status={Status} L{Level} ({X},{Y})mm battery={Battery}%",
                        robotId, r.LastStatus, r.Level, r.X, r.Y, r.Battery);
                Publish(s => s with { LastOkAt = DateTimeOffset.UtcNow, TotalSent = s.TotalSent + 1, ConsecutiveFailures = 0, BackoffUntil = null, LastError = null });
                return;

            case SendKind.Rejected:
                // 형식 오류는 같은 값을 다시 보내도 같은 결과 — 폐기하고 로그·알람 [§9.5]
                _log.LogError("SAIGE가 로봇 상태를 거부({Detail}) — robot={Robot}, 재시도하지 않고 폐기.", o.Detail, robotId);
                await RaiseAlarmAsync(db, "SAIGE_BAD_REQUEST", robotId, "SAIGE 로봇 상태 형식 오류(40001)", o.Detail, ct);
                Publish(s => s with { TotalRejected = s.TotalRejected + 1, LastError = o.Detail, LastErrorAt = DateTimeOffset.UtcNow });
                return;

            case SendKind.Backoff:
            case SendKind.Failed:
                _consecutiveFailures++;
                if (o.Kind is SendKind.Backoff)
                {
                    // 5s → 10s → 20s … 상한 MaxBackoffSec
                    double sec = Math.Min(_opt.MaxBackoffSec, _opt.IntervalSec * Math.Pow(2, Math.Min(_consecutiveFailures, 10)));
                    _backoffUntil = DateTimeOffset.UtcNow.AddSeconds(sec);
                }
                _log.LogWarning("SAIGE 전송 실패 {N}회 연속 ({Detail}).", _consecutiveFailures, o.Detail);
                if (!_alarmRaised && _consecutiveFailures >= _opt.AlarmAfterFailures)
                {
                    _alarmRaised = true;   // 복구 전까지 1회만
                    await RaiseAlarmAsync(db, "SAIGE_UNREACHABLE", robotId, "SAIGE 로봇 상태 전송 불가", o.Detail, ct);
                }
                var until = _backoffUntil == DateTimeOffset.MinValue ? (DateTimeOffset?)null : _backoffUntil;
                Publish(s => s with { TotalFailed = s.TotalFailed + 1, ConsecutiveFailures = _consecutiveFailures, BackoffUntil = until,
                    LastError = o.Detail, LastErrorAt = DateTimeOffset.UtcNow, AlarmRaised = _alarmRaised });
                return;
        }
    }

    private async Task RaiseAlarmAsync(AcsDbContext db, string code, string robotId, string title, string? detail, CancellationToken ct)
    {
        try
        {
            db.Alarms.Add(new AlarmEntity
            {
                AlarmId = Guid.NewGuid(), AlarmCode = code, RobotId = robotId,
                Detail = JsonSerializer.Serialize(new { severity = "WARNING", title, detail }),
                RaisedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "알람 {Code} 기록 실패 — alarm.spec 시드 확인 필요.", code);
        }
    }

    private enum SendKind { Ok, Rejected, Backoff, Failed }
    private sealed record SendOutcome(SendKind Kind, string? Detail);
}

/// <summary>
/// SAIGE 연동 상태 스냅샷 [운영 확인용 — GET /api/integrations/saige]. 정상 전송은 로그에 남지 않으므로
/// "지금 보내고 있는가"는 <see cref="LastOkAt"/>·<see cref="TotalSent"/>가 근거다. Healthy = 활성 + 마지막 결과 OK + 백오프 아님.
/// </summary>
public sealed record SaigeLinkStatus(
    bool Enabled, string Endpoint, int IntervalSec,
    DateTimeOffset? LastOkAt, long TotalSent, long TotalFailed, long TotalRejected,
    int ConsecutiveFailures, DateTimeOffset? BackoffUntil, bool AlarmRaised,
    string? LastError, DateTimeOffset? LastErrorAt,
    IReadOnlyList<SaigeRobotLink> Robots)
{
    public bool Healthy => Enabled && LastOkAt is not null && ConsecutiveFailures == 0 && LastError is null;

    /// <summary>마지막 성공 이후 경과 초 — 주기(IntervalSec)의 몇 배인지로 "멈춤"을 판단할 수 있다.</summary>
    public double? SecondsSinceLastOk => LastOkAt is { } t ? Math.Round((DateTimeOffset.UtcNow - t).TotalSeconds, 1) : null;

    public static SaigeLinkStatus Initial(SaigeOptions o) => new(
        o.Enabled, o.BaseUrl.TrimEnd('/') + o.HealthPath, o.IntervalSec,
        null, 0, 0, 0, 0, null, false, null, null, Array.Empty<SaigeRobotLink>());
}

/// <summary>로봇별 마지막 전송 내용. HoldReason 이 있으면 지금은 전송 보류 중(도면 좌표 산출 불가 등).</summary>
public sealed record SaigeRobotLink(string RobotId, DateTimeOffset? LastSentAt, string? LastStatus,
    int? Level, int? X, int? Y, int? Battery, string? LastResult, string? HoldReason);

/// <summary>robot-health-check 요청 본문 [§9.1]. JSON 키는 camelCase.</summary>
public sealed record SaigeRobotHealth(
    string RobotId, string Status, SaigeRobotPosition Position, int Battery, int Rssi, long Timestamp);

/// <summary>도면 좌표 위치 — level 1-based, x·y mm 정수, yaw deg 소수 1자리.</summary>
public sealed record SaigeRobotPosition(int Level, int X, int Y, double Yaw);

internal static class SaigeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
