using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// HD_AMR 통신 단절 Fail-Safe(rev.0) ACS 측 반응 [docs/HD_AMR_통신단절_FailSafe_사양.md §6].
/// 판정·정지·재개 전 확인(§4.2)은 전부 AMR 로컬 — ACS는 단계를 복제하지 않고 errors[]에 반응만 한다.
///  · 재개: 운영자 확인 후 <c>stopPause</c> instantAction 1건 발행(자동 재개 금지 §4.1).
///  · 취소: run 중단 + 로봇이 PAUSE 중이면 <c>cancelOrder</c> — PAUSE AMR은 Order를 폐기하지 않고 쥐고 있으므로.
/// </summary>
public sealed class CommLossService
{
    public const string PauseErrorType = "communicationLostPause";

    private readonly AcsDbContext _db;
    private readonly Vda5050MasterClient _vda;
    private readonly InspectionDispatcher _dispatcher;
    private readonly MissionService _missions;
    private readonly RobotErrorTracker _tracker;
    private readonly ILogger<CommLossService> _log;

    public CommLossService(AcsDbContext db, Vda5050MasterClient vda, InspectionDispatcher dispatcher,
        MissionService missions, RobotErrorTracker tracker, ILogger<CommLossService> log)
    {
        _db = db; _vda = vda; _dispatcher = dispatcher; _missions = missions; _tracker = tracker; _log = log;
    }

    public sealed record ResumePauseRequest(string? UserId);
    public sealed record ResumePauseResult(string RobotId, string ActionType);
    public sealed record AbortResult(Guid RunId, string State, bool CancelOrderSent);

    /// <summary>
    /// 운영자 재개 → <c>stopPause</c>. 로봇이 통신 단절 일시정지를 보고 중일 때만 보낸다(그 외 409 — 오조작 방지).
    /// 재개 가능 여부(비상정지·배터리·측위/mapId·모드)는 AMR이 판정하며, 거부는 errors[] resumeRejected로 돌아온다.
    /// </summary>
    public async Task<ResumePauseResult> ResumePauseAsync(string robotId, ResumePauseRequest req, CancellationToken ct)
    {
        var robot = await _db.Robots.AsNoTracking().FirstOrDefaultAsync(r => r.RobotId == robotId, ct)
            ?? throw new CommLossException(404, $"robot '{robotId}' 없음");
        if (!_tracker.HasActiveError(robotId, PauseErrorType))
            throw new CommLossException(409,
                $"AMR {robotId}이(가) 통신 단절 일시정지({PauseErrorType})를 보고하고 있지 않습니다 — 재개할 대상이 없습니다.");

        // 미연결 시 RobotNotConnectedException + ROBOT_NOT_CONNECTED 알람(명령 차단)
        await _dispatcher.EnsureRobotConnectedAsync(robotId, "작업 재개(stopPause)", ct);

        await _vda.StopPauseAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber), ct);
        _db.AuditLogs.Add(new AuditLogEntity
            { UserId = req.UserId ?? "operator", Action = "COMM_LOSS_RESUME", Target = robotId });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("통신 단절 일시정지 재개 요청(stopPause) — robot={Robot} user={User}", robotId, req.UserId);
        return new ResumePauseResult(robotId, "stopPause");
    }

    /// <summary>
    /// run 중단. 해당 로봇이 통신 단절 일시정지 중이면 <c>cancelOrder</c>도 발행한다(사용자 결정, VDA 사양서 §4.5.3 개정 1.9).
    /// 발행 실패는 중단 자체를 되돌리지 않는다 — run은 이미 ABORTED, 응답의 cancelOrderSent로 알린다.
    /// </summary>
    public async Task<AbortResult> AbortRunAsync(Guid runId, CancellationToken ct)
    {
        var robotId = await _db.ScenarioRuns.AsNoTracking().Where(r => r.RunId == runId)
            .Select(r => r.RobotId).FirstOrDefaultAsync(ct);
        await _missions.AbortRunAsync(runId, ct);   // 없음 KeyNotFound / 종결 RunStateException 은 호출부가 매핑

        bool sent = false;
        if (robotId is not null && _tracker.HasActiveError(robotId, PauseErrorType))
        {
            var robot = await _db.Robots.AsNoTracking().FirstOrDefaultAsync(r => r.RobotId == robotId, ct);
            if (robot is not null)
            {
                try
                {
                    await _vda.CancelOrderAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber), ct);
                    sent = true;
                    _db.AuditLogs.Add(new AuditLogEntity
                        { UserId = "operator", Action = "COMM_LOSS_CANCEL_ORDER", Target = robotId });
                    await _db.SaveChangesAsync(ct);
                    _log.LogInformation("통신 단절 일시정지 중 run {Run} 중단 — cancelOrder 발행(robot={Robot})", runId, robotId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "run {Run} 중단됨 — 그러나 cancelOrder 발행 실패(robot={Robot}). MQTT 연결 확인 후 다시 중단하거나 현장 조치.",
                        runId, robotId);
                }
            }
        }
        return new AbortResult(runId, "ABORTED", sent);
    }
}

public sealed class CommLossException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
