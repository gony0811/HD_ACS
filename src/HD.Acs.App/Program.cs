using HD.Acs.App.Hubs;
using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Serilog;

// HD_ACS 서버 — 단일 프로세스 모놀리스 [ADR-011]
// REST API(명령/조회) + SignalR(실시간 푸시) + VDA 5050 마스터 [ADR-001/005]

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddDbContext<AcsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(sp => new Vda5050MasterClient(
    builder.Configuration["Acs:Mqtt:Host"] ?? "localhost",
    builder.Configuration.GetValue("Acs:Mqtt:Port", 1883)));

builder.Services.AddScoped<RobotStateService>();
builder.Services.AddScoped<MissionService>();
builder.Services.AddHostedService<VdaBridgeService>();
builder.Services.AddSignalR();

var app = builder.Build();

app.MapHub<MonitoringHub>("/hubs/monitoring");

// ── REST API (API-First: WPF/Web/태블릿 공용 [ADR-005]) ──────────────

app.MapGet("/api/robots", async (AcsDbContext db) =>
    Results.Ok(await db.Robots.AsNoTracking().ToListAsync()));

app.MapGet("/api/robots/{robotId}/context", async (string robotId, AcsDbContext db) =>
    await db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId)
        is { } ctx ? Results.Ok(ctx) : Results.NotFound());

app.MapGet("/api/scenarios", async (AcsDbContext db) =>
    Results.Ok(await db.Scenarios.AsNoTracking()
        .Select(s => new { s.ScenarioId, s.Name, s.Version, s.TankId, s.Status }).ToListAsync()));

app.MapPost("/api/runs", async (StartRunRequest req, MissionService missions) =>
    Results.Ok(new { runId = await missions.StartRunAsync(req.ScenarioId, req.RobotId) }));

app.MapPost("/api/runs/{runId:guid}/release-next", async (Guid runId, MissionService missions) =>
    Results.Ok(new { released = await missions.TryReleaseNextMissionAsync(runId) }));

app.MapGet("/api/runs/{runId:guid}", async (Guid runId, AcsDbContext db) =>
    await db.ScenarioRuns.AsNoTracking().Include(r => r.Missions.OrderBy(m => m.Seq))
        .FirstOrDefaultAsync(r => r.RunId == runId)
        is { } run ? Results.Ok(run) : Results.NotFound());

// 작업자 수동 층(존) 변경 [Q9] — Operator 권한 필요 (TODO: 인증 미들웨어)
app.MapPost("/api/robots/{robotId}/zone", async (string robotId, ZoneChangeRequest req, MissionService missions) =>
{
    await missions.ManualZoneChangeAsync(robotId, req.MapId, req.UserId, req.X, req.Y, req.Theta);
    return Results.Ok();
});

// 비상정지 — 기능적 정지 (안전 규격 정지는 로봇측 하드웨어 [ADR-007])
app.MapPost("/api/robots/{robotId}/emergency-stop",
    async (string robotId, EmergencyStopRequest req, AcsDbContext db, Vda5050MasterClient vda) =>
{
    var robot = await db.Robots.AsNoTracking().FirstAsync(r => r.RobotId == robotId);
    await vda.EmergencyStopAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber));
    db.AuditLogs.Add(new Data.Entities.AuditLogEntity
        { UserId = req.UserId, Action = "EMERGENCY_STOP", Target = robotId });
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.Run();

public sealed record StartRunRequest(Guid ScenarioId, string RobotId);
public sealed record ZoneChangeRequest(string MapId, string UserId, double X, double Y, double Theta);
public sealed record EmergencyStopRequest(string UserId);
