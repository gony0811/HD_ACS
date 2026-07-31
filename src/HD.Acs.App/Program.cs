using System.Text.Json;
using HD.Acs.App.Hubs;
using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Serilog;

// HD_ACS 서버 — 단일 프로세스 모놀리스 [ADR-011]
// REST API(명령/조회) + SignalR(실시간 푸시) + VDA 5050 마스터 [ADR-001/005]

var builder = WebApplication.CreateBuilder(args);

// Kestrel 리스닝 포트 = Acs:Api:ListenPort (기본 5100). UI(REST/SignalR)가 http://localhost:5100 로 붙는다.
// 폐쇄망 OS 서비스 배포에서도 이 설정으로 고정 [ADR-011].
builder.WebHost.UseUrls($"http://localhost:{builder.Configuration.GetValue("Acs:Api:ListenPort", 5100)}");

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddDbContext<AcsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());   // 속성(PascalCase) → 컬럼(snake_case) 매핑 [db/schema.sql 네이밍 C안]

builder.Services.AddSingleton(sp => new Vda5050MasterClient(
    builder.Configuration["Acs:Mqtt:Host"] ?? "localhost",
    builder.Configuration.GetValue("Acs:Mqtt:Port", 1883)));

builder.Services.AddScoped<RobotStateService>();
builder.Services.AddScoped<MissionService>();
builder.Services.AddScoped<SeamPlanningService>();
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

// 도면 seam → 스테이션/TASK 자동 생성 [PHASE2 WP-2]. 유효 T_W_D 없으면 400.
app.MapPost("/api/scenarios/{scenarioId:guid}/generate-from-seams",
    async (Guid scenarioId, GenerateFromSeamsRequest? req, SeamPlanningService planning) =>
{
    try
    {
        var r = await planning.GenerateAsync(scenarioId, req?.SeamIds, req?.UserId);
        return Results.Ok(new { stations = r.Stations, tasks = r.Tasks, skipped = r.Skipped });
    }
    catch (SeamPlanningService.CalibrationMissingException ex)
    {
        return Results.BadRequest(new { error = ex.Message, reasons = ex.Reasons });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

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
    db.AuditLogs.Add(new HD.Acs.Data.Entities.AuditLogEntity
        { UserId = req.UserId, Action = "EMERGENCY_STOP", Target = robotId });
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ── 도면→맵 캘리브레이션 (T_W_D) [PHASE2 WP-1] ──────────────────────────
// 맵버전 바인딩: 대응쌍/변환은 현재 ref.map.version에 귀속. 맵 재생성 시 자동 무효 [§2.5].

// 기준점 캡처 — 해당 층(mapId)을 보고 중인 로봇의 최신 ReportedX/Y를 도면 좌표와 대응
app.MapPost("/api/maps/{mapId}/calibration/points",
    async (string mapId, CalibrationPointRequest req, AcsDbContext db) =>
{
    var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId);
    if (map is null) return Results.NotFound(new { error = $"map '{mapId}' 없음" });

    // 해당 층을 보고 중인 로봇 컨텍스트 (최신 보고)
    var ctx = await db.RobotContexts.AsNoTracking()
        .Where(c => c.ReportedMapId == mapId)
        .OrderByDescending(c => c.ReportedAt)
        .FirstOrDefaultAsync();
    if (ctx is null || ctx.ReportedX is not double rx || ctx.ReportedY is not double ry)
        return Results.Conflict(new { error = $"'{mapId}' 층을 보고 중인 로봇 위치가 없습니다. (로봇 ReportedMapId≠mapId)" });

    double dx = req.Unit == "mm" ? req.DrawingX / 1000.0 : req.DrawingX;
    double dy = req.Unit == "mm" ? req.DrawingY / 1000.0 : req.DrawingY;

    var point = new MapCalibrationPointEntity
    {
        Id = Guid.NewGuid(), MapId = mapId, MapVersion = map.Version,
        DrawingXM = dx, DrawingYM = dy, MapX = rx, MapY = ry, CapturedBy = req.UserId
    };
    db.MapCalibrationPoints.Add(point);
    db.AuditLogs.Add(new AuditLogEntity
    {
        UserId = req.UserId, Action = "CALIBRATION_CAPTURE", Target = mapId,
        Detail = JsonSerializer.Serialize(new { mapId, map.Version, drawing = new { dx, dy }, map_pos = new { rx, ry } })
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { point.Id, point.MapVersion, point.DrawingXM, point.DrawingYM, point.MapX, point.MapY });
});

// 현재 맵버전의 대응쌍 목록
app.MapGet("/api/maps/{mapId}/calibration/points", async (string mapId, AcsDbContext db) =>
{
    var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId);
    if (map is null) return Results.NotFound();
    var points = await db.MapCalibrationPoints.AsNoTracking()
        .Where(p => p.MapId == mapId && p.MapVersion == map.Version)
        .OrderBy(p => p.CapturedAt).ToListAsync();
    return Results.Ok(points);
});

app.MapDelete("/api/maps/{mapId}/calibration/points/{id:guid}", async (string mapId, Guid id, AcsDbContext db) =>
{
    var point = await db.MapCalibrationPoints.FirstOrDefaultAsync(p => p.Id == id && p.MapId == mapId);
    if (point is null) return Results.NotFound();
    db.MapCalibrationPoints.Remove(point);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// 최소자승 계산·저장 — RMS가 임계값 초과여도 저장하되 warning 반환
app.MapPost("/api/maps/{mapId}/calibration/solve",
    async (string mapId, AcsDbContext db, IConfiguration config) =>
{
    var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId);
    if (map is null) return Results.NotFound(new { error = $"map '{mapId}' 없음" });

    var points = await db.MapCalibrationPoints.AsNoTracking()
        .Where(p => p.MapId == mapId && p.MapVersion == map.Version).ToListAsync();
    if (points.Count < 2)
        return Results.BadRequest(new { error = "대응쌍이 2개 미만입니다. 최소 2점(권장 3점) 캡처 후 계산하세요." });

    var pairs = points
        .Select(p => (((double X, double Y))(p.DrawingXM, p.DrawingYM), ((double X, double Y))(p.MapX, p.MapY)))
        .ToList();
    var (t, rms, maxResidual) = DrawingTransform.Solve(pairs);

    var cal = await db.MapCalibrations.FindAsync(mapId, map.Version)
              ?? db.MapCalibrations.Add(new MapCalibrationEntity { MapId = mapId, MapVersion = map.Version }).Entity;
    cal.Tx = t.Tx; cal.Ty = t.Ty; cal.YawRad = t.YawRad;
    cal.RmsM = rms; cal.PointCount = points.Count; cal.RegisteredAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    double rmsWarn = config.GetValue("Acs:Calibration:RmsWarnM", 0.05);
    string? warning = rms > rmsWarn
        ? $"등록 잔차 RMS {rms:F4}m 가 임계값 {rmsWarn:F3}m 를 초과합니다. 기준점 재캡처를 권장합니다."
        : null;
    return Results.Ok(new { tx = t.Tx, ty = t.Ty, yawRad = t.YawRad, rmsM = rms, maxResidualM = maxResidual, pointCount = points.Count, warning });
});

// 현재 유효 T_W_D — 맵버전 불일치(구버전 캘리브레이션)면 404 [§2.5]
app.MapGet("/api/maps/{mapId}/calibration", async (string mapId, AcsDbContext db) =>
{
    var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId);
    if (map is null) return Results.NotFound();
    var cal = await db.MapCalibrations.AsNoTracking()
        .FirstOrDefaultAsync(c => c.MapId == mapId && c.MapVersion == map.Version);
    return cal is null ? Results.NotFound() : Results.Ok(cal);
});

app.Run();

public sealed record StartRunRequest(Guid ScenarioId, string RobotId);
public sealed record ZoneChangeRequest(string MapId, string UserId, double X, double Y, double Theta);
public sealed record EmergencyStopRequest(string UserId);
public sealed record CalibrationPointRequest(double DrawingX, double DrawingY, string Unit, string UserId);
public sealed record GenerateFromSeamsRequest(Guid[]? SeamIds, string? UserId);
