using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>가스 센서 상태 — 가스검사 AMR 이 state.information `gasSensor` 로 보고한 최신 값.</summary>
public sealed record GasSensorInfo(string State, int ErrorCode, bool? Calibrated, DateTimeOffset At);

/// <summary>임계값 적용 확인 — AMR 이 state.information `gasThreshold` 에 actionId 와 함께 싣는다.</summary>
public sealed record GasThresholdAck(string ActionId, bool Accepted, string? Error);

/// <summary>
/// 가스검사 AMR 이 state.information 으로 올리는 값의 최신본(메모리) — 센서 상태와 임계값 적용 확인
/// [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.3·§5.4]. 싱글턴: state 처리(쓰기)와 세이지 상태 전송·임계값 API(읽기)가 같이 쓴다.
/// </summary>
public sealed class GasLinkTracker
{
    private readonly ConcurrentDictionary<string, GasSensorInfo> _sensors = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GasThresholdAck>> _waiters = new();

    /// <summary>state 1건의 information 을 반영한다. 가스 항목이 없으면(용접검사 AMR) 아무것도 하지 않는다.</summary>
    public void Update(string robotId, IEnumerable<VdaInformation> information)
    {
        foreach (var info in information)
        {
            var refs = (info.InfoReferences ?? new()).GroupBy(r => r.ReferenceKey)
                .ToDictionary(g => g.Key, g => g.Last().ReferenceValue, StringComparer.OrdinalIgnoreCase);
            if (info.InfoType == "gasSensor" && refs.TryGetValue("state", out var state) && !string.IsNullOrEmpty(state))
            {
                int code = refs.TryGetValue("errorCode", out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                bool? cal = refs.TryGetValue("calibrated", out var k) && bool.TryParse(k, out var b) ? b : null;
                _sensors[robotId] = new GasSensorInfo(state, code, cal, DateTimeOffset.UtcNow);
            }
            else if (info.InfoType == "gasThreshold" && refs.TryGetValue("lastActionId", out var actionId) && !string.IsNullOrEmpty(actionId)
                     && _waiters.TryRemove(actionId, out var waiter))
            {
                bool accepted = refs.TryGetValue("lastAccepted", out var a) && bool.TryParse(a, out var ok) && ok;
                waiter.TrySetResult(new GasThresholdAck(actionId, accepted, refs.GetValueOrDefault("lastError")));
            }
        }
    }

    /// <summary>최근 <paramref name="maxAge"/> 안에 보고된 센서 상태. 없거나 오래됐으면 null(세이지 전송에서 sensor 생략).</summary>
    public GasSensorInfo? Sensor(string robotId, TimeSpan maxAge) =>
        _sensors.TryGetValue(robotId, out var s) && DateTimeOffset.UtcNow - s.At <= maxAge ? s : null;

    /// <summary>등록해 둔 확인(<see cref="Expect"/> 의 반환값)을 기다린다. 시간 안에 안 오면 null.</summary>
    public async Task<GasThresholdAck?> WaitAckAsync(Task<GasThresholdAck> pending, string actionId, TimeSpan timeout, CancellationToken ct)
    {
        try { return await pending.WaitAsync(timeout, ct); }
        catch (TimeoutException) { return null; }
        finally { _waiters.TryRemove(actionId, out _); }
    }

    /// <summary>임계값 지령(actionId)의 적용 확인 대기를 등록한다 — **지령을 보내기 전에** 부를 것(응답이 먼저 처리돼도 놓치지 않게).</summary>
    public Task<GasThresholdAck> Expect(string actionId) =>
        _waiters.GetOrAdd(actionId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
}

/// <summary>SAIGE → ACS 임계값 요청 본문 [세이지 포밍가스 연동 규격 초안 v0.1 §3]. 누락 검증을 위해 모두 nullable 로 받는다.</summary>
public sealed record GasThresholdRequest(string? TankId, double? Value, string? Unit, string? RequestedBy, long? RequestedAt);

/// <summary>성공 응답 — 받은 값을 그대로 돌려준다 [§3].</summary>
public sealed record GasThresholdResponse(string TankId, double Value, string Unit, long Timestamp);

/// <summary>값 검증 실패 — 400 [§3]. AMR 이 값을 거부한 경우(단위 불일치 등)도 여기에 해당.</summary>
public sealed class GasThresholdInvalidException(string message) : Exception(message);

/// <summary>AMR 에 전달하지 못함 — 503 [§3] (AMR 미연결·응답 없음).</summary>
public sealed class GasThresholdUndeliveredException(string message) : Exception(message);

/// <summary>
/// 임계값 전달 SAIGE → ACS → AMR [세이지 포밍가스 연동 규격 초안 v0.1 §3].
/// 세이지가 검사 시작 때와 검사 도중 변경 때 보낸다. ACS 는 값을 저장하지 않고 그 선창의 AMR 로 instantAction
/// `setGasThreshold` 를 내려보낸 뒤, AMR 이 state.information 으로 적용을 확인해 줄 때까지 기다렸다가 답한다
/// (세이지는 200 을 받은 값만 적용된 것으로 표시하므로, 보냈다는 것만으로 200 을 주지 않는다).
/// </summary>
public sealed class GasThresholdService
{
    private readonly AcsDbContext _db;
    private readonly Vda5050MasterClient _vda;
    private readonly GasLinkTracker _link;
    private readonly TimeSpan _ackTimeout;
    private readonly ILogger<GasThresholdService> _log;

    public GasThresholdService(AcsDbContext db, Vda5050MasterClient vda, GasLinkTracker link, IConfiguration config,
        ILogger<GasThresholdService> log)
    {
        _db = db; _vda = vda; _link = link; _log = log;
        _ackTimeout = TimeSpan.FromSeconds(config.GetValue("Acs:Saige:Gas:ThresholdAckTimeoutSec", 5.0));
    }

    /// <summary>요청 값 검증 [§3 필수 항목]. 문제 없으면 null.</summary>
    public static string? Validate(GasThresholdRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.TankId)) return "tankId 는 필수입니다.";
        if (r.Value is not double v || !double.IsFinite(v) || v <= 0) return "value 는 0 보다 큰 숫자여야 합니다.";
        if (string.IsNullOrWhiteSpace(r.Unit)) return "unit 은 필수입니다.";
        if (string.IsNullOrWhiteSpace(r.RequestedBy)) return "requestedBy 는 필수입니다.";
        if (r.RequestedAt is not long at || at <= 0) return "requestedAt(epoch ms)은 필수입니다.";
        return null;
    }

    /// <summary>그 선창에 있는 연결된 로봇 — 보고 층이 그 선창의 맵인 로봇. 보고 층이 없으면 등록 로봇이 1대일 때 그 로봇.</summary>
    internal async Task<List<RobotEntity>> TargetRobotsAsync(string tankId, CancellationToken ct)
    {
        var maps = await _db.Maps.AsNoTracking().Where(m => m.TankId == tankId).Select(m => m.MapId).ToListAsync(ct);
        var online = await _db.RobotContexts.AsNoTracking().Where(c => c.ConnectionState == "ONLINE").ToListAsync(ct);
        var ids = online.Where(c => c.ReportedMapId != null && maps.Contains(c.ReportedMapId)).Select(c => c.RobotId).ToList();
        if (ids.Count == 0 && online.Count == 1 && online[0].ReportedMapId is null) ids.Add(online[0].RobotId);
        return await _db.Robots.AsNoTracking().Where(r => ids.Contains(r.RobotId)).ToListAsync(ct);
    }

    public async Task<GasThresholdResponse> ApplyAsync(GasThresholdRequest req, CancellationToken ct)
    {
        if (Validate(req) is { } problem) throw new GasThresholdInvalidException(problem);
        string tankId = req.TankId!.Trim(), unit = req.Unit!.Trim(), by = req.RequestedBy!.Trim();
        double value = req.Value!.Value;
        long at = req.RequestedAt!.Value;
        if (!await _db.TankGeometries.AsNoTracking().AnyAsync(g => g.TankId == tankId, ct))
            throw new GasThresholdInvalidException($"선창 '{tankId}' 가 없습니다.");

        var robots = await TargetRobotsAsync(tankId, ct);
        if (robots.Count == 0)
            return await FailAsync(new GasThresholdUndeliveredException($"선창 {tankId} 에 연결된 AMR 이 없습니다."), tankId, value, unit, by, at, ct);

        foreach (var robot in robots)
        {
            var actionId = Guid.NewGuid().ToString();
            var pending = _link.Expect(actionId);   // 응답이 발행보다 먼저 처리되는 경합 방지 — 보내기 전에 대기 등록
            try
            {
                await _vda.PublishInstantActionsAsync(new RobotRef(robot.RobotId, robot.Manufacturer, robot.SerialNumber),
                    BuildInstantAction(actionId, tankId, value, unit, by, at), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await FailAsync(new GasThresholdUndeliveredException($"AMR {robot.RobotId} 로 보내지 못했습니다: {ex.Message}"),
                    tankId, value, unit, by, at, ct);
            }
            var ack = await _link.WaitAckAsync(pending, actionId, _ackTimeout, ct);
            if (ack is null)
                return await FailAsync(new GasThresholdUndeliveredException(
                    $"AMR {robot.RobotId} 가 {_ackTimeout.TotalSeconds:0}초 안에 임계값 적용을 확인하지 않았습니다."), tankId, value, unit, by, at, ct);
            if (!ack.Accepted)
                return await FailAsync(new GasThresholdInvalidException(
                    $"AMR {robot.RobotId} 가 임계값을 거부했습니다: {ack.Error ?? "사유 없음"}"), tankId, value, unit, by, at, ct);
        }

        // 임계값 변경은 양측이 로그로 남긴다 — 요청자·요청 시각·tankId·값 [§3 동작 규칙]
        await AuditAsync("GAS_THRESHOLD_SET", tankId, value, unit, by, at, string.Join(",", robots.Select(r => r.RobotId)), null, ct);
        _log.LogInformation("가스 임계값 적용 — tank={Tank} {Value} {Unit} (요청자 {By}, 로봇 {Robots})",
            tankId, value, unit, by, string.Join(",", robots.Select(r => r.RobotId)));
        return new GasThresholdResponse(tankId, value, unit, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private async Task<GasThresholdResponse> FailAsync(Exception ex, string tankId, double value, string unit, string by, long at, CancellationToken ct)
    {
        _log.LogWarning("가스 임계값 전달 실패 — tank={Tank} {Value} {Unit} (요청자 {By}): {Reason}", tankId, value, unit, by, ex.Message);
        await AuditAsync("GAS_THRESHOLD_FAILED", tankId, value, unit, by, at, null, ex.Message, ct);
        throw ex;
    }

    private async Task AuditAsync(string action, string tankId, double value, string unit, string by, long at, string? robots, string? error,
        CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = by, Action = action, Target = tankId,
            Detail = JsonSerializer.Serialize(new { value, unit, requestedBy = by, requestedAt = at, robots, error }),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>instantAction `setGasThreshold` — 세이지 요청과 같은 필드를 그대로 내려준다 [HD_AMR 가스 디텍터 사양 §5.3]. 순수.</summary>
    public static Vda5050InstantActions BuildInstantAction(string actionId, string tankId, double value, string unit, string requestedBy, long requestedAt)
    {
        var action = new VdaAction { ActionType = "setGasThreshold", ActionId = actionId, BlockingType = "NONE" };
        action.ActionParameters.Add(new ActionParameter { Key = "tankId", Value = tankId });
        action.ActionParameters.Add(new ActionParameter { Key = "value", Value = value });
        action.ActionParameters.Add(new ActionParameter { Key = "unit", Value = unit });
        action.ActionParameters.Add(new ActionParameter { Key = "requestedBy", Value = requestedBy });
        action.ActionParameters.Add(new ActionParameter { Key = "requestedAt", Value = requestedAt });
        return new Vda5050InstantActions { Actions = { action } };
    }
}
