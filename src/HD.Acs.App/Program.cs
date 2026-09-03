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

// content root를 exe 위치로 고정 — 기본값(현재 작업 디렉터리)이면 다른 폴더에서 exe 직접 실행 시
// appsettings.json을 못 읽어 ConnectionString 미초기화로 기동 실패한다. OS 서비스 배포(ADR-011,
// cwd=System32)에도 필수.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Kestrel 리스닝 포트 = Acs:Api:ListenPort (기본 5199). UI(REST/SignalR)가 http://localhost:5199 로 붙는다.
// 5100은 NAMUGA 계열 배포 제품(CS01_P 등)이 쓰는 관례 포트라 개발/현장 PC 공존을 위해 회피.
// 폐쇄망 OS 서비스 배포에서도 이 설정으로 고정 [ADR-011].
builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Acs:Api:ListenPort", 5199)}");

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddDbContext<AcsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());   // 속성(PascalCase) → 컬럼(snake_case) 매핑 [db/schema.sql 네이밍 C안]

builder.Services.AddSingleton(sp => new Vda5050MasterClient(
    builder.Configuration["Acs:Mqtt:Host"] ?? "localhost",
    builder.Configuration.GetValue("Acs:Mqtt:Port", 1883)));

builder.Services.AddSingleton<HD.Acs.Core.Planning.IInspectionOrderingPolicy, HD.Acs.Core.Planning.GreedyNearestPolicy>();
builder.Services.AddScoped<InspectionDispatcher>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<RobotStateService>();
builder.Services.AddScoped<MissionService>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<InspectionDispatcher>();
builder.Services.AddSingleton<IInspectionOrderingPolicy, GreedyNearestPolicy>();  // 순수 정책(무상태)
builder.Services.AddScoped<SeamPlanningService>();
builder.Services.AddScoped<TankGeometryService>();
builder.Services.AddHostedService<VdaBridgeService>();
builder.Services.AddSignalR();

var app = builder.Build();

// ── 기동 시드: ref.map 층 4행 (CT1-L1~L4) ─────────────────────────────
// mapId는 UI TankLayout.Floors·VDA nodePosition.mapId와 동일 문자열이어야 한다.
// schema.sql에도 동일 시드가 있으나, 현장 서버처럼 DB에 직접 접근할 수 없는 배포에서는
// 이 기동 시드가 유일한 적용 경로다. 기존 행은 보존(맵버전 증가·비활성 맵 훼손 금지 [§2.5]).
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        const string seedTankId = "CT1";
        var existingMapIds = await db.Maps.AsNoTracking()
            .Where(m => m.TankId == seedTankId).Select(m => m.MapId).ToListAsync();
        for (int level = 1; level <= 4; level++)
        {
            var mapId = $"{seedTankId}-L{level}";
            if (!existingMapIds.Contains(mapId))
                db.Maps.Add(new MapEntity { MapId = mapId, TankId = seedTankId, Level = level, Name = $"{seedTankId} L{level}" });
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync();
            app.Logger.LogInformation("ref.map 기동 시드 적용: {TankId} L1~L4", seedTankId);
        }
    }
    catch (Exception ex)
    {
        // DB 미기동 등으로 시드 실패해도 앱은 뜬다(두절 내성 ADR-002) — 캘리브레이션은 맵 행 생길 때까지 404.
        app.Logger.LogWarning(ex, "ref.map 기동 시드 실패 — DB 연결 확인 필요");
    }
}

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
        .Select(s => new
        {
            s.ScenarioId, s.Name, s.Version, s.TankId, s.Status,
            // 부분 검사 계획: 연결 영역 수 (0 = 선창 전체 검사)
            AreaCount = db.ScenarioAreas.Count(sa => sa.ScenarioId == s.ScenarioId),
        }).ToListAsync()));

// ── 부분 검사 계획: 시나리오 검사 대상 영역 연결 (연결 0건 = 선창 전체) ──
app.MapGet("/api/scenarios/{scenarioId:guid}/areas", async (Guid scenarioId, AcsDbContext db) =>
{
    if (!await db.Scenarios.AsNoTracking().AnyAsync(s => s.ScenarioId == scenarioId))
        return Results.NotFound(new { error = $"시나리오 '{scenarioId}' 없음" });
    return Results.Ok(await (
        from sa in db.ScenarioAreas.AsNoTracking()
        join a in db.InspectionAreas.AsNoTracking() on sa.AreaId equals a.AreaId
        where sa.ScenarioId == scenarioId
        orderby sa.SortOrder, a.WallCode, a.Name
        select new { sa.AreaId, a.WallCode, a.Level, a.Name, sa.SortOrder }).ToListAsync());
});

// 전체 교체(빈 배열 = 연결 해제 = 전체 검사). 미존재/타 선창 영역은 400.
app.MapPut("/api/scenarios/{scenarioId:guid}/areas", async (Guid scenarioId, SetScenarioAreasRequest req, AcsDbContext db) =>
{
    var scenario = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.ScenarioId == scenarioId);
    if (scenario is null) return Results.NotFound(new { error = $"시나리오 '{scenarioId}' 없음" });

    var ids = (req.AreaIds ?? Array.Empty<Guid>()).Distinct().ToArray();
    if (ids.Length > 0)
    {
        var valid = await db.InspectionAreas.AsNoTracking()
            .Where(a => ids.Contains(a.AreaId) && a.TankId == scenario.TankId)
            .Select(a => a.AreaId).ToListAsync();
        var invalid = ids.Except(valid).ToArray();
        if (invalid.Length > 0)
            return Results.BadRequest(new
            { error = $"존재하지 않거나 시나리오 선창({scenario.TankId})이 아닌 영역 {invalid.Length}건: {string.Join(", ", invalid.Take(3))}…" });
    }

    var existing = await db.ScenarioAreas.Where(sa => sa.ScenarioId == scenarioId).ToListAsync();
    db.ScenarioAreas.RemoveRange(existing);
    for (int i = 0; i < ids.Length; i++)
        db.ScenarioAreas.Add(new HD.Acs.Data.Entities.ScenarioAreaEntity
        { ScenarioId = scenarioId, AreaId = ids[i], SortOrder = i });
    await db.SaveChangesAsync();
    return Results.Ok(new { scenarioId, areaCount = ids.Length });
});

// ── 선창 파라메트릭 정의 + 면 자동생성 [SPEC v3 §2/§3] ──
app.MapPost("/api/tanks/{tankId}/geometry", async (string tankId, CreateTankGeometryRequest req, TankGeometryService svc) =>
{
    var geom = new HD.Acs.Core.Planning.TankGeometry(
        req.LengthL, req.WFloor, req.ThetaLowDeg * Math.PI / 180.0, req.HLow,
        req.HWall, req.ThetaUpDeg * Math.PI / 180.0, req.HUp,
        req.LevelZ ?? Array.Empty<double>(), req.OriginOx ?? 0, req.OriginOy ?? 0,
        req.ReachZMin, req.ReachZMax);
    try
    {
        int n = await svc.RegisterAsync(tankId, geom, req.CheckHTotal, req.CheckBeam, req.CheckWCeil, req.UserId);
        return Results.Ok(new { tankId, wallsGenerated = n });
    }
    catch (TankGeometryService.GeometryInvalidException ex)
    { return Results.BadRequest(new { error = ex.Message, reasons = ex.Reasons }); }
});

app.MapGet("/api/tanks/{tankId}/geometry", async (string tankId, TankGeometryService svc) =>
    await svc.GetGeometryAsync(tankId) is { } g ? Results.Ok(g) : Results.NotFound());

app.MapGet("/api/tanks/{tankId}/walls", async (string tankId, int? level, TankGeometryService svc) =>
    Results.Ok(await svc.GetWallsAsync(tankId, level)));

// ── 영역·검사 작업 [SPEC v3 §4] — 벽면-로컬 (u,v) 등록 ──
app.MapPost("/api/areas", async (CreateAreaRequest req, AcsDbContext db) =>
{
    // 코너: 요청의 Corners(임의 4점) 우선, 없으면 UMin/VMin/UMax/VMax 사각형 폴백(하위호환).
    var corners = (req.Corners is { Length: >= 3 }
        ? req.Corners
        : new[] { new[] { req.UMin, req.VMin }, new[] { req.UMax, req.VMin }, new[] { req.UMax, req.VMax }, new[] { req.UMin, req.VMax } })
        .Where(p => p is { Length: >= 2 }).Select(p => new[] { p[0], p[1] }).ToList();
    if (corners.Count < 3)
        return Results.BadRequest(new { error = "영역 코너가 3점 미만입니다." });

    var wall = await db.Walls.AsNoTracking().FirstOrDefaultAsync(w => w.TankId == req.TankId && w.WallCode == req.WallCode);
    if (wall is null)
        return Results.NotFound(new { error = $"면이 없습니다: {req.TankId}/{req.WallCode} (선창 파라미터를 먼저 등록하세요)." });
    // 전 코너가 면 범위 내인지 검사(대각 2점만 검사하면 회전 사각형이 면 밖으로 나가도 통과됨).
    if (corners.Any(p => !HD.Acs.Core.Planning.AreaGeometry.InBounds(p[0], p[1], 0, 0, wall.ULen, wall.VLen)))
        return Results.BadRequest(new { error = $"영역 코너가 면 범위를 벗어났습니다 (면 {req.WallCode}: u∈[0,{wall.ULen:0.###}], v∈[0,{wall.VLen:0.###}])." });
    var (uMin, vMin, uMax, vMax) = HD.Acs.Core.Planning.AreaGeometry.Bbox(corners);
    if (uMax - uMin < 1e-6 || vMax - vMin < 1e-6)
        return Results.BadRequest(new { error = "영역이 퇴화(면적 0)했습니다 — 유효한 사각형 4점을 입력하세요." });

    // ── 층 자동 유도 [SPEC v3.1 §5-A] — 요청의 Level은 무시하고 영역 z범위(코너 v의 min/max)로 유도한다 ──
    var g = await db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == req.TankId);
    if (g is null)
        return Results.BadRequest(new { error = $"선창 지오메트리가 없습니다: {req.TankId} (파라미터를 먼저 등록하세요)." });
    var geom = new HD.Acs.Core.Planning.TankGeometry(
        g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
        System.Text.Json.JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? Array.Empty<double>(),
        g.OriginOx, g.OriginOy, g.ReachZMin, g.ReachZMax);
    var vAxis = System.Text.Json.JsonSerializer.Deserialize<double[]>(wall.VAxis)!;
    var origin = System.Text.Json.JsonSerializer.Deserialize<double[]>(wall.Origin)!;
    var (zLo, zHi) = HD.Acs.Core.Planning.LevelBands.AreaZRange(origin[2], vAxis[2], vMin, vMax);
    int? derivedLevel = HD.Acs.Core.Planning.LevelBands.Derive(zLo, zHi, geom.LevelBandList(), out var reason);
    if (derivedLevel is null)
        return Results.BadRequest(new { error = $"층 유도 실패 (면 {req.WallCode}): {reason}", reason });

    if (req.StationStandoffM is < 0)
        return Results.BadRequest(new { error = "정차 이격(stationStandoffM)은 0 이상이어야 합니다." });
    bool dup = await db.InspectionAreas.AsNoTracking()
        .AnyAsync(a => a.TankId == req.TankId && a.WallCode == req.WallCode && a.Name == req.Name);
    if (dup)
        return Results.Conflict(new { error = $"면 {req.WallCode} 내에 영역 '{req.Name}'이(가) 이미 있습니다." });
    var area = new HD.Acs.Data.Entities.InspectionAreaEntity
    {
        AreaId = Guid.NewGuid(), TankId = req.TankId, WallCode = req.WallCode, Level = derivedLevel.Value, Name = req.Name,
        Corners = System.Text.Json.JsonSerializer.Serialize(corners),
        UMin = uMin, VMin = vMin, UMax = uMax, VMax = vMax,
        StationX = req.StationX, StationY = req.StationY, StationTheta = req.StationTheta,
        StationStandoffM = req.StationStandoffM,
        SortOrder = req.SortOrder ?? 0, CreatedBy = req.UserId
    };
    db.InspectionAreas.Add(area);
    await db.SaveChangesAsync();
    return Results.Ok(new { areaId = area.AreaId, level = derivedLevel.Value });
});

app.MapGet("/api/areas", async (string? tankId, string? wallCode, int? level, AcsDbContext db) =>
{
    var q = db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == (tankId ?? "CT1"));
    if (wallCode is not null) q = q.Where(a => a.WallCode == wallCode);
    if (level is not null) q = q.Where(a => a.Level == level);
    var areas = await q.OrderBy(a => a.WallCode).ThenBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync();
    return Results.Ok(areas.Select(a => new
    {
        a.AreaId, a.TankId, a.WallCode, a.Level, a.Name,
        corners = System.Text.Json.JsonSerializer.Deserialize<double[][]>(a.Corners),
        a.UMin, a.VMin, a.UMax, a.VMax, a.StationX, a.StationY, a.StationTheta, a.StationStandoffM, a.SortOrder,
        TaskCount = a.Tasks.Count
    }));
});

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
    // seamType은 현행 계약상 LINE 한정 — POLYLINE은 AMR이 액션 FAILED 처리(AMR 회신 §5.1).
    // 영역 작업은 2점 기하뿐이라 꺾인 용접선은 세그먼트별 LINE으로 등록한다(같은 영역=정렬 공유).
    if (req.SeamType is { } st && !string.Equals(st, "LINE", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new
        { error = $"seamType '{st}'은 지원하지 않습니다 — 현 계약은 LINE 한정(AMR 회신 §5.1). 꺾인 용접선은 세그먼트별 LINE으로 나눠 등록하세요." });
    var poly = System.Text.Json.JsonSerializer.Deserialize<double[][]>(a.Corners) ?? Array.Empty<double[]>();
    bool In(double u, double v) => HD.Acs.Core.Planning.AreaGeometry.PointInPolygon(u, v, poly);
    if (!In(req.StartU, req.StartV) || !In(req.EndU, req.EndV))
        return Results.BadRequest(new { error = "용접선 시작/끝점이 영역(사각형) 내부가 아닙니다." });
    int seq = req.Seq ?? ((await db.AreaTasks.Where(t => t.AreaId == areaId).MaxAsync(t => (int?)t.Seq) ?? 0) + 1);
    var task = new HD.Acs.Data.Entities.AreaTaskEntity
    {
        TaskId = Guid.NewGuid(), AreaId = areaId, Seq = seq, Name = req.Name, SeamType = req.SeamType ?? "LINE",
        StartU = req.StartU, StartV = req.StartV, EndU = req.EndU, EndV = req.EndV,
        SectionDxfId = req.SectionDxfId ?? "", ProfileId = req.ProfileId ?? "", CreatedBy = req.UserId
    };
    db.AreaTasks.Add(task);
    await db.SaveChangesAsync();
    return Results.Ok(new { taskId = task.TaskId, seq });
});

app.MapGet("/api/areas/{areaId:guid}/tasks", async (Guid areaId, AcsDbContext db) =>
{
    var tasks = await db.AreaTasks.AsNoTracking().Where(t => t.AreaId == areaId).OrderBy(t => t.Seq).ToListAsync();
    return Results.Ok(tasks.Select(t => new
    {
        t.TaskId, t.Seq, t.Name, t.SeamType, t.StartU, t.StartV, t.EndU, t.EndV, t.SectionDxfId, t.ProfileId
    }));
});

app.MapDelete("/api/area-tasks/{taskId:guid}", async (Guid taskId, AcsDbContext db) =>
{
    var t = await db.AreaTasks.FindAsync(taskId);
    if (t is null) return Results.NotFound();
    db.AreaTasks.Remove(t);
    await db.SaveChangesAsync();
    return Results.Ok();
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

// 시나리오 삭제 — 하드 삭제 + 가드. 참조하는 실행(run)이 있으면 409로 차단(run.scenario_run은 FK 미설정이라
// 코드 가드 필수). 없으면 삭제(ref.inspection_point/task는 FK ON DELETE CASCADE).
app.MapDelete("/api/scenarios/{scenarioId:guid}", async (Guid scenarioId, AcsDbContext db) =>
{
    var scenario = await db.Scenarios.FindAsync(scenarioId);
    if (scenario is null) return Results.NotFound();
    if (await db.ScenarioRuns.AnyAsync(r => r.ScenarioId == scenarioId))
        return Results.Conflict(new { error = "이 시나리오를 참조하는 실행 이력(run)이 있어 삭제할 수 없습니다." });
    db.Scenarios.Remove(scenario);
    await db.SaveChangesAsync();
    return Results.Ok();
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
    catch (RunConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
});

// run 중단 — 상태만 ABORTED (진행 중 Order 미회수, 즉시 정지는 비상정지). 완료 이력 보존 → resume 가능.
app.MapPost("/api/runs/{runId:guid}/abort", async (Guid runId, MissionService missions) =>
{
    try { await missions.AbortRunAsync(runId); return Results.Ok(new { runId, state = "ABORTED" }); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (RunStateException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// run 재개 — DONE/SKIPPED 보존, DISPATCHED→PENDING 리셋 후 잔여만 재배차 [INSPECTION_SCENARIO §3.1]
app.MapPost("/api/runs/{runId:guid}/resume", async (Guid runId, MissionService missions) =>
{
    try { await missions.ResumeRunAsync(runId); return Results.Ok(new { runId, state = "RUNNING" }); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (RunStateException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (RunConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex) when (ex is CalibrationInvalidException or WeldPayloadSchemaException)
    { return Results.BadRequest(new { error = ex.Message }); }
});

// 로봇의 가장 최근 재개 가능 run (미종결 작업 보유) — UI "이어하기" 사전 확인용
app.MapGet("/api/runs/resumable", async (string robotId, MissionService missions) =>
    await missions.FindResumableRunAsync(robotId) is { } r ? Results.Ok(r) : Results.NotFound());

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

// 검사 작업 큐 조회 (greedy 배차 상태 — 운영 화면 층 진행/오버레이 소스)
app.MapGet("/api/runs/{runId:guid}/work-items", async (Guid runId, AcsDbContext db) =>
    Results.Ok(await db.WorkItems.AsNoTracking().Where(w => w.RunId == runId)
        .Join(db.InspectionAreas.AsNoTracking(), w => w.AreaId, a => a.AreaId,
            (w, a) => new { w, AreaName = a.Name, a.Level })
        .OrderBy(x => x.w.MapId).ThenBy(x => x.w.Seq)
        .Select(x => new
        {
            x.w.WorkItemId, x.w.AreaId, x.AreaName, x.Level, x.w.MapId, x.w.Seq,
            x.w.X, x.w.Y, x.w.Theta, x.w.Status, x.w.Attempts,
        })
        .ToListAsync()));

// 용접라인(액션) 단위 상태 조회 — 작업 현황 드릴다운 초기 로드. 실시간은 SignalR "TaskActionProgress".
// 재시도로 동일 TaskId 액션이 여럿이면 CreatedAt 오름차순 — 클라이언트가 나중 것으로 덮어써 최신 반영.
app.MapGet("/api/runs/{runId:guid}/task-actions", async (Guid runId, AcsDbContext db) =>
    Results.Ok(await (
        from a in db.OrderActions.AsNoTracking()
        join w in db.WorkItems.AsNoTracking() on a.WorkItemId equals (Guid?)w.WorkItemId
        where w.RunId == runId
        join t0 in db.AreaTasks.AsNoTracking() on a.TaskId equals (Guid?)t0.TaskId into tg
        from t in tg.DefaultIfEmpty()
        orderby a.CreatedAt
        select new
        {
            a.ActionId, a.WorkItemId, a.TaskId,
            TaskSeq = (int?)t.Seq, TaskName = t.Name,
            a.Status, a.Result, a.CreatedAt,
        }).ToListAsync()));

// TASK 단위 진행률 (완료/전체·%) — 운영 화면 초기 로드·새로고침용 pull. 실시간은 SignalR "RunProgress".
app.MapGet("/api/runs/{runId:guid}/progress", async (Guid runId, ProgressService progress, AcsDbContext db) =>
    await db.ScenarioRuns.AsNoTracking().AnyAsync(r => r.RunId == runId)
        ? Results.Ok(await progress.ComputeRunProgressAsync(runId))
        : Results.NotFound());

// 작업자 수동 층(존) 변경 [Q9] — Operator 권한 필요 (TODO: 인증 미들웨어)
app.MapPost("/api/robots/{robotId}/zone", async (string robotId, ZoneChangeRequest req, MissionService missions) =>
{
    await missions.ManualZoneChangeAsync(robotId, req.MapId, req.UserId, req.X, req.Y, req.Theta);
    return Results.Ok();
});

// 비상정지 — 기능적 정지 (안전 규격 정지는 로봇측 하드웨어 [ADR-007])
app.MapPost("/api/robots/{robotId}/emergency-stop",
    async (string robotId, EmergencyStopRequest req, AcsDbContext db, Vda5050MasterClient vda, MissionService missions) =>
{
    var robot = await db.Robots.AsNoTracking().FirstAsync(r => r.RobotId == robotId);
    await vda.EmergencyStopAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber));
    db.AuditLogs.Add(new HD.Acs.Data.Entities.AuditLogEntity
        { UserId = req.UserId, Action = "EMERGENCY_STOP", Target = robotId });
    await db.SaveChangesAsync();

    // 활성 run 자동 중단 — AMR은 비상정지 시 진행 액션을 FAILED로 보고하므로(AMR 회신 §3.4),
    // run을 중단하지 않으면 실패 정책이 정지 중인 로봇에 재배차를 시도한다. 재개는 "이어하기"(resume).
    Guid? abortedRunId = null;
    var active = await db.ScenarioRuns.AsNoTracking()
        .Where(r => r.RobotId == robotId && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
        .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync();
    if (active is Guid runId)
    {
        await missions.AbortRunAsync(runId);
        abortedRunId = runId;
    }
    return Results.Ok(new { robotId, abortedRunId });
});

// 수동 이동(goto) — 이동 테스트용. 도면 좌표를 T_W_D로 맵 좌표로 변환해 **액션 없는 단일 노드 Order** 발행.
// 진행 중 run이 있으면 409(신규 orderId가 진행 중 정차를 폐기시키므로), 로봇 보고 층과 다르면 409.
app.MapPost("/api/robots/{robotId}/goto",
    async (string robotId, GotoRequest req, AcsDbContext db, Vda5050MasterClient vda, IConfiguration config) =>
{
    var robot = await db.Robots.AsNoTracking().FirstOrDefaultAsync(r => r.RobotId == robotId);
    if (robot is null) return Results.NotFound(new { error = $"robot '{robotId}' 없음" });

    var activeRun = await db.ScenarioRuns.AsNoTracking()
        .Where(r => r.RobotId == robotId && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
        .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync();
    if (activeRun is not null)
        return Results.Conflict(new { error = $"진행 중인 run({activeRun})이 있습니다 — 수동 이동은 중단(abort) 후 사용하세요." });

    var ctx = await db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId);
    if (ctx?.ReportedMapId is not { } mapId)
        return Results.Conflict(new { error = "로봇 보고 층(mapId)이 없습니다 — 로봇 연결 상태를 확인하세요." });
    if (!mapId.EndsWith($"-L{req.Level}", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = $"로봇이 다른 층({mapId})에 있습니다 — L{req.Level} 이동은 수동 층 변경 후 가능합니다." });

    var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId);
    if (map is null) return Results.NotFound(new { error = $"map '{mapId}' 없음" });
    var cal = await db.MapCalibrations.AsNoTracking().Where(c => c.MapId == mapId)
        .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync();
    DrawingTransform t;
    try { t = WeldInspectionPayload.ResolveTransform(map.Version, cal?.MapVersion, cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0); }
    catch (CalibrationInvalidException ex) { return Results.BadRequest(new { error = ex.Message }); }

    var (mx, my) = t.DrawingToMap(req.XDrawing, req.YDrawing);
    var orderId = Guid.NewGuid().ToString();
    var order = new HD.Acs.Vda5050.Messages.Vda5050Order { OrderId = orderId, OrderUpdateId = 0 };
    order.Nodes.Add(new HD.Acs.Vda5050.Messages.OrderNode
    {
        NodeId = $"GOTO-{orderId[..8]}", SequenceId = 0, Released = true,
        NodePosition = new HD.Acs.Vda5050.Messages.NodePosition
        {
            X = mx, Y = my, MapId = mapId,   // theta 미지정 — 도착 방향 자유
            AllowedDeviationXY = config.GetValue("Acs:Dispatch:AllowedDevXy", 0.08),
            AllowedDeviationTheta = config.GetValue("Acs:Dispatch:AllowedDevTheta", 0.07),
        },
    });
    await vda.PublishOrderAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber), order);
    db.AuditLogs.Add(new HD.Acs.Data.Entities.AuditLogEntity
        { UserId = req.UserId ?? "", Action = "MANUAL_GOTO", Target = $"{robotId} → {mapId} ({mx:F2},{my:F2})" });
    await db.SaveChangesAsync();
    return Results.Ok(new { orderId, mapId, mapX = mx, mapY = my });
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
public sealed record GotoRequest(int Level, double XDrawing, double YDrawing, string? UserId);
public sealed record CalibrationPointRequest(double DrawingX, double DrawingY, string Unit, string UserId);
public sealed record GenerateFromSeamsRequest(Guid[]? SeamIds, string? UserId);
public sealed record CreateScenarioRequest(string Name, string TankId);
public sealed record SetScenarioAreasRequest(Guid[]? AreaIds);
public sealed record CreateSeamRequest(string TankId, int Level, string WallCode, string? SeamType,
    double[][] PathDrawing, double[] NormalDrawing, string? SectionDxfId, string? ProfileId, string? UserId);
// 선창 파라미터 등록 [SPEC v3 §2/§8]. 각도는 deg 입력(서버가 rad 변환).
public sealed record CreateTankGeometryRequest(
    double LengthL, double WFloor, double ThetaLowDeg, double HLow,
    double HWall, double ThetaUpDeg, double HUp,
    double[]? LevelZ, double? OriginOx, double? OriginOy,
    double? CheckHTotal, double? CheckBeam, double? CheckWCeil, string? UserId,
    double? ReachZMin = null, double? ReachZMax = null);   // v3.1 §5-A 도달 밴드 보정(선택)
// 영역·작업 등록 [SPEC v3 §4] — 벽면-로컬 (u,v). Level은 v3.1에서 무시(서버가 §5-A로 유도).
// Corners = 임의 4점 사각형[[u,v]…]. 미지정 시 UMin/VMin/UMax/VMax 로 사각형 폴백(하위호환).
public sealed record CreateAreaRequest(string TankId, string WallCode, int Level, string Name,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta, int? SortOrder, string? UserId,
    double[][]? Corners = null, double? StationStandoffM = null);
public sealed record CreateAreaTaskRequest(int? Seq, string? Name, string? SeamType,
    double StartU, double StartV, double EndU, double EndV, string? SectionDxfId, string? ProfileId, string? UserId);
