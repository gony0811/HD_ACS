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
        _vda.StateReceived += async (robot, state) =>
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RobotStateService>()
                .HandleStateAsync(robot, state, ct);
        };
        _vda.ConnectionReceived += async (robot, conn) =>
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
        };

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
