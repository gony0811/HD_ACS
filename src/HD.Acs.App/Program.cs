using System.Text.Json;
using HD.Acs.App.Hubs;
using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.AspNetCore.Diagnostics;
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
builder.Services.AddScoped<AreaPlanningService>();
builder.Services.AddHostedService<VdaBridgeService>();
builder.Services.AddSignalR();

var app = builder.Build();

// 전역 예외 핸들러 — 처리되지 않은 예외를 { error } JSON(500)으로 변환해 UI에 원인을 노출.
// DB 저장 오류(DbUpdateException)는 스키마 드리프트가 흔한 원인이므로 힌트를 덧붙인다.
// (typed Results.BadRequest/Conflict 는 예외가 아니므로 영향 없음)
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    var msg = ex is DbUpdateException
        ? $"DB 저장 실패 — 스키마가 최신인지 확인하세요 (db/schema.sql · db/migrations). 원인: {ex.InnerException?.Message ?? ex.Message}"
        : (ex?.Message ?? "서버 내부 오류");
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}));

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

// ── 벽면(Wall) LAYER [정차각 자동화] — 벽면 레지스트리 + HD_AMR 티칭 키. 정차각은 seam 기하에서 자동 산출 ──
app.MapPost("/api/walls", async (CreateWallRequest req, AcsDbContext db) =>
{
    var wall = await db.Walls.FirstOrDefaultAsync(w =>
        w.TankId == req.TankId && w.Level == req.Level && w.WallCode == req.WallCode);
    if (wall is null)
    {
        wall = new HD.Acs.Data.Entities.WallEntity { TankId = req.TankId, Level = req.Level, WallCode = req.WallCode };
        db.Walls.Add(wall);
    }
    wall.Description = req.Description;
    await db.SaveChangesAsync();
    return Results.Ok(new { tankId = wall.TankId, level = wall.Level, wallCode = wall.WallCode });
});

app.MapGet("/api/walls", async (string? tankId, int? level, AcsDbContext db) =>
{
    var q = db.Walls.AsNoTracking().Where(w => w.TankId == (tankId ?? "CT1"));
    if (level is not null) q = q.Where(w => w.Level == level);
    return Results.Ok(await q.OrderBy(w => w.Level).ThenBy(w => w.WallCode)
        .Select(w => new { w.TankId, w.Level, w.WallCode, w.Description }).ToListAsync());
});

app.MapDelete("/api/walls/{tankId}/{level:int}/{wallCode}", async (string tankId, int level, string wallCode, AcsDbContext db) =>
{
    var wall = await db.Walls.FirstOrDefaultAsync(w => w.TankId == tankId && w.Level == level && w.WallCode == wallCode);
    if (wall is null) return Results.NotFound();
    bool used = await db.InspectionAreas.AsNoTracking()
        .AnyAsync(a => a.TankId == tankId && a.Level == level && a.WallCode == wallCode);
    if (used)
        return Results.Conflict(new { error = $"벽면 {tankId}/L{level}/{wallCode}을(를) 참조하는 영역이 있어 삭제할 수 없습니다." });
    db.Walls.Remove(wall);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ── 영역(Area) LAYER + 수동 검사 작업 [PHASE2 개정] — 자동 슬라이싱 대체 ──
app.MapPost("/api/areas", async (CreateAreaRequest req, AcsDbContext db) =>
{
    if (req.MinX >= req.MaxX || req.MinY >= req.MaxY)
        return Results.BadRequest(new { error = "영역 경계가 올바르지 않습니다 (minX<maxX, minY<maxY)." });
    // 정차각은 소속 벽면에서 상속 — 등록된 벽면(ref.wall)이어야 함 [SPEC v2]
    bool wallExists = await db.Walls.AsNoTracking()
        .AnyAsync(w => w.TankId == req.TankId && w.Level == req.Level && w.WallCode == req.WallCode);
    if (!wallExists)
        return Results.BadRequest(new { error =
            $"등록된 벽면이 아닙니다: {req.TankId}/L{req.Level}/{req.WallCode}. 먼저 벽면(정차각)을 등록하세요." });
    // 동일 (tank, level, wall) 내 영역 이름 중복 금지 [벽면 내 유일] — 층 간 벽면 중복은 허용
    bool dup = await db.InspectionAreas.AsNoTracking().AnyAsync(x =>
        x.TankId == req.TankId && x.Level == req.Level &&
        x.WallCode == req.WallCode && x.Name == req.Name);
    if (dup)
        return Results.Conflict(new { error =
            $"동일 벽면(L{req.Level}/{req.WallCode}) 내에 영역 '{req.Name}'이(가) 이미 있습니다." });
    var area = new HD.Acs.Data.Entities.InspectionAreaEntity
    {
        AreaId = Guid.NewGuid(), TankId = req.TankId, Level = req.Level, WallCode = req.WallCode, Name = req.Name,
        MinX = req.MinX, MinY = req.MinY, MaxX = req.MaxX, MaxY = req.MaxY,
        StationX = req.StationX, StationY = req.StationY, StationTheta = req.StationTheta,
        SortOrder = req.SortOrder ?? 0, CreatedBy = req.UserId
    };
    db.InspectionAreas.Add(area);
    await db.SaveChangesAsync();
    return Results.Ok(new { areaId = area.AreaId });
});

app.MapGet("/api/areas", async (string? tankId, int? level, string? wallCode, AreaPlanningService area) =>
    Results.Ok(await area.GetAreasAsync(tankId ?? "CT1", level, wallCode)));

app.MapDelete("/api/areas/{areaId:guid}", async (Guid areaId, AcsDbContext db) =>
{
    var a = await db.InspectionAreas.FindAsync(areaId);
    if (a is null) return Results.NotFound();
    db.InspectionAreas.Remove(a);   // area_task CASCADE
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/areas/{areaId:guid}/tasks", async (Guid areaId, CreateAreaTaskRequest req, AcsDbContext db) =>
{
    var a = await db.InspectionAreas.AsNoTracking().FirstOrDefaultAsync(x => x.AreaId == areaId);
    if (a is null) return Results.NotFound(new { error = $"area '{areaId}' 없음" });
    bool InBounds(double[] p) => p.Length >= 2 &&
        HD.Acs.Core.Planning.AreaGeometry.InBounds(p[0], p[1], a.MinX, a.MinY, a.MaxX, a.MaxY);
    if (!InBounds(req.SeamStart) || !InBounds(req.SeamEnd))
        return Results.BadRequest(new { error = "시작/끝점이 영역 경계를 벗어났습니다." });
    int seq = req.Seq ?? ((await db.AreaTasks.Where(t => t.AreaId == areaId).MaxAsync(t => (int?)t.Seq) ?? 0) + 1);
    var task = new HD.Acs.Data.Entities.AreaTaskEntity
    {
        TaskId = Guid.NewGuid(), AreaId = areaId, Seq = seq, Name = req.Name,
        StartDrawing = JsonSerializer.Serialize(req.SeamStart), EndDrawing = JsonSerializer.Serialize(req.SeamEnd),
        SeamType = req.SeamType ?? "LINE", SectionDxfId = req.SectionDxfId ?? "", ProfileId = req.ProfileId ?? "",
        CreatedBy = req.UserId
    };
    db.AreaTasks.Add(task);
    await db.SaveChangesAsync();
    return Results.Ok(new { taskId = task.TaskId, seq });
});

app.MapGet("/api/areas/{areaId:guid}/tasks", async (Guid areaId, AreaPlanningService area) =>
    Results.Ok(await area.GetAreaTasksAsync(areaId)));

app.MapDelete("/api/area-tasks/{taskId:guid}", async (Guid taskId, AcsDbContext db) =>
{
    var t = await db.AreaTasks.FindAsync(taskId);
    if (t is null) return Results.NotFound();
    db.AreaTasks.Remove(t);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// 영역/작업 → 스테이션/TASK 생성. 유효 T_W_D 없으면 400.
app.MapPost("/api/scenarios/{scenarioId:guid}/generate-from-areas",
    async (Guid scenarioId, GenerateFromAreasRequest? req, AreaPlanningService area) =>
{
    try
    {
        var r = await area.GenerateAsync(scenarioId, req?.AreaIds, req?.UserId);
        return Results.Ok(new { stations = r.Stations, tasks = r.Tasks, skipped = r.Skipped });
    }
    catch (SeamPlanningService.CalibrationMissingException ex)
    { return Results.BadRequest(new { error = ex.Message, reasons = ex.Reasons }); }
    catch (InvalidOperationException ex)
    { return Results.NotFound(new { error = ex.Message }); }
});

// 도면 seam → 스테이션/TASK 자동 생성 [PHASE2 WP-2, DORMANT — 운영 워크플로우 제외]. 유효 T_W_D 없으면 400.
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

// 최소 시나리오 생성 [PHASE2 WP-5b]
app.MapPost("/api/scenarios", async (CreateScenarioRequest req, AcsDbContext db) =>
{
    var s = new HD.Acs.Data.Entities.ScenarioEntity
    {
        ScenarioId = Guid.NewGuid(), Name = req.Name, Version = 1,
        TankId = req.TankId, Policy = "{}", Status = "DRAFT"
    };
    db.Scenarios.Add(s);
    await db.SaveChangesAsync();
    return Results.Ok(new { scenarioId = s.ScenarioId });
});

// WeldSeam 등록/목록/삭제 [PHASE2 WP-5b] — 사람이 등록하는 유일한 입력(도면 좌표)
app.MapPost("/api/seams", async (CreateSeamRequest req, AcsDbContext db) =>
{
    var seam = new HD.Acs.Data.Entities.WeldSeamEntity
    {
        SeamId = Guid.NewGuid(), TankId = req.TankId, Level = req.Level, WallCode = req.WallCode,
        SeamType = req.SeamType ?? "LINE",
        PathDrawing = JsonSerializer.Serialize(req.PathDrawing),
        NormalDrawing = JsonSerializer.Serialize(req.NormalDrawing),
        SectionDxfId = req.SectionDxfId ?? "", ProfileId = req.ProfileId ?? "",
        CreatedBy = req.UserId
    };
    db.WeldSeams.Add(seam);
    await db.SaveChangesAsync();
    return Results.Ok(new { seamId = seam.SeamId });
});

app.MapGet("/api/seams", async (string? tankId, int? level, AcsDbContext db) =>
{
    var q = db.WeldSeams.AsNoTracking().AsQueryable();
    if (tankId is not null) q = q.Where(w => w.TankId == tankId);
    if (level is not null) q = q.Where(w => w.Level == level);
    return Results.Ok(await q.OrderBy(w => w.WallCode)
        .Select(w => new { w.SeamId, w.TankId, w.Level, w.WallCode, w.SeamType, w.SectionDxfId, w.ProfileId })
        .ToListAsync());
});

app.MapDelete("/api/seams/{seamId:guid}", async (Guid seamId, AcsDbContext db) =>
{
    var seam = await db.WeldSeams.FindAsync(seamId);
    if (seam is null) return Results.NotFound();
    db.WeldSeams.Remove(seam);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// 생성된 스테이션/TASK 조회 (전개도 렌더 데이터 소스) [PHASE2 WP-5b]
app.MapGet("/api/scenarios/{scenarioId:guid}/stations", async (Guid scenarioId, SeamPlanningService planning) =>
    Results.Ok(await planning.GetStationsAsync(scenarioId)));

app.MapPost("/api/runs", async (StartRunRequest req, MissionService missions) =>
{
    try { return Results.Ok(new { runId = await missions.StartRunAsync(req.ScenarioId, req.RobotId) }); }
    catch (Exception ex) when (ex is CalibrationInvalidException or WeldPayloadSchemaException)
    { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/runs/{runId:guid}/release-next", async (Guid runId, MissionService missions) =>
{
    try { return Results.Ok(new { released = await missions.TryReleaseNextMissionAsync(runId) }); }
    catch (Exception ex) when (ex is CalibrationInvalidException or WeldPayloadSchemaException)
    { return Results.BadRequest(new { error = ex.Message }); }
});

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
public sealed record CreateScenarioRequest(string Name, string TankId);
public sealed record CreateSeamRequest(string TankId, int Level, string WallCode, string? SeamType,
    double[][] PathDrawing, double[] NormalDrawing, string? SectionDxfId, string? ProfileId, string? UserId);
public sealed record CreateWallRequest(string TankId, int Level, string WallCode, string? Description, string? UserId);
public sealed record CreateAreaRequest(string TankId, int Level, string WallCode, string Name,
    double MinX, double MinY, double MaxX, double MaxY,
    double? StationX, double? StationY, double? StationTheta, int? SortOrder, string? UserId);
public sealed record CreateAreaTaskRequest(int? Seq, string? Name, double[] SeamStart, double[] SeamEnd,
    string? SeamType, string? SectionDxfId, string? ProfileId, string? UserId);
public sealed record GenerateFromAreasRequest(Guid[]? AreaIds, string? UserId);
