using HD.Acs.Data;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 기동 시 MQTT 연결 + ref.robot의 활성 로봇 구독, 수신 이벤트를 스코프 서비스로 위임.
/// </summary>
public sealed class VdaBridgeService : BackgroundService
{
    private readonly Vda5050MasterClient _vda;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<VdaBridgeService> _log;

    public VdaBridgeService(Vda5050MasterClient vda, IServiceScopeFactory scopes, ILogger<VdaBridgeService> log)
    {
        _vda = vda; _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 핸들러 예외는 메시지 단위로 격리 — 전파 시 MQTT 수신 체인이 죽어 이후 state가 전부 유실된다
        _vda.StateReceived += async (robot, state) =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RobotStateService>()
                    .HandleStateAsync(robot, state, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "state 처리 실패 (robot={Robot}, order={Order}) — 메시지 스킵", robot.RobotId, state.OrderId);
            }
        };
        _vda.ConnectionReceived += async (robot, conn) =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider;
                await svc.GetRequiredService<RobotStateService>().HandleConnectionAsync(robot, conn, ct);

                // 재접속 동기화 후 층 전환 대기 중이던 Run의 릴리즈 재시도 [ADR-002, Q9]
                if (conn.ConnectionState == "ONLINE")
                {
                    var db = svc.GetRequiredService<AcsDbContext>();
                    var waiting = await db.ScenarioRuns
                        .Where(r => r.RobotId == robot.RobotId && r.State == "WAITING_FLOOR_TRANSFER")
                        .Select(r => r.RunId).ToListAsync(ct);
                    var missions = svc.GetRequiredService<MissionService>();
                    foreach (var runId in waiting)
                        await missions.TryReleaseNextMissionAsync(runId, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "connection 처리 실패 (robot={Robot}, state={State}) — 메시지 스킵", robot.RobotId, conn.ConnectionState);
            }
        };

        // connection_state는 마지막 수신값을 보관하는 캐시이므로 서버 재기동 뒤에도
        // 이전 ONLINE 값이 DB에 남아 있을 수 있다. 실제 연결 메시지를 새로 받기 전에는
        // 연결된 것으로 표시하지 않도록 MQTT 연결/구독보다 먼저 초기화한다.
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            var resetCount = await db.RobotContexts
                .Where(ctx => ctx.ConnectionState != null && ctx.ConnectionState != "OFFLINE")
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ctx => ctx.ConnectionState, "OFFLINE"), ct);

            if (resetCount > 0)
                _log.LogInformation("서버 기동 시 로봇 연결 상태 {Count}건을 OFFLINE으로 초기화", resetCount);
        }

        // 연결 재시도 루프 (브로커는 별도 OS 서비스 — 앱보다 늦게 뜰 수 있음 [ADR-011])
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _vda.ConnectAsync(ct);
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("MQTT 연결 실패, 5초 후 재시도: {Msg}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            var robots = await db.Robots.AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
            foreach (var r in robots)
                await _vda.RegisterRobotAsync(new RobotRef(r.RobotId, r.Manufacturer, r.SerialNumber), ct);
            _log.LogInformation("VDA 5050 마스터 기동 — 로봇 {Count}대 구독", robots.Count);
        }
    }
}
