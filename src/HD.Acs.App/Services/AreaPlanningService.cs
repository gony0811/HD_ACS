using System.Text.Json;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 영역(Area) LAYER + 수동 검사 작업 → 스테이션/TASK 생성 [PHASE2 개정, 자동 슬라이싱 대체].
/// 영역 1개 = STATION 노드 1개 = anchorGroup 1개. 생성물 Position/Params 형태는 SeamPlanningService와
/// 동일하여 WP-3 payload 빌드·시뮬레이터 계약을 무변경 승계한다.
/// </summary>
public sealed class AreaPlanningService
{
    private readonly AcsDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AreaPlanningService> _log;

    private const double StationDevXy = 0.08;
    private const double StationDevTheta = 0.07;

    public AreaPlanningService(AcsDbContext db, IConfiguration config, ILogger<AreaPlanningService> log)
    {
        _db = db; _config = config; _log = log;
    }

    public sealed record GenerateResult(int Stations, int Tasks, IReadOnlyList<string> Skipped);

    public async Task<GenerateResult> GenerateAsync(
        Guid scenarioId, IReadOnlyList<Guid>? areaIds, string? userId, CancellationToken ct = default)
    {
        var scenario = await _db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.ScenarioId == scenarioId, ct)
            ?? throw new InvalidOperationException($"scenario '{scenarioId}' 없음");

        var areaQuery = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks.OrderBy(t => t.Seq))
            .Where(a => a.TankId == scenario.TankId);
        if (areaIds is { Count: > 0 })
            areaQuery = areaQuery.Where(a => areaIds.Contains(a.AreaId));
        var areas = (await areaQuery.ToListAsync(ct))
            .OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToList();

        var skipped = new List<string>();
        double standoffMm = _config.GetValue("Acs:Area:StandoffMm", 400.0);
        double workingMm = _config.GetValue("Acs:Area:WorkingDistanceMm", standoffMm);

        // 유효 영역: 작업 ≥1 (작업 0개만 skipped). 정차각은 seam 기하에서 자동 산출 [정차각 자동화]
        //   정차 위치 = station_x/y ?? 영역 중앙, 정차각 = station_theta ?? atan2(정차→seam 중심)
        //   degenerate(정차 위치≈seam 중심)는 조용한 스킵이 아니라 명시적 실패(reasons)
        var valid = new List<(InspectionAreaEntity Area, double Sx, double Sy, double Theta)>();
        var thetaMissing = new List<string>();
        foreach (var a in areas)
        {
            if (a.Tasks.Count == 0) { skipped.Add($"area {a.Name}: 검사 작업 없음"); continue; }
            var (cx, cy) = AreaGeometry.AreaCenter(a.MinX, a.MinY, a.MaxX, a.MaxY);
            double sx = a.StationX ?? cx, sy = a.StationY ?? cy;
            var targets = a.Tasks
                .SelectMany(t => new[] { ParseVec3(t.StartDrawing), ParseVec3(t.EndDrawing) })
                .Where(p => p is not null).Select(p => p!).ToList();
            double? theta = a.StationTheta ?? AreaGeometry.FacingYawToward(sx, sy, targets);
            if (theta is null)
            { thetaMissing.Add($"area {a.Name}: 정차각 자동 산출 불가 (정차 위치와 seam 중심이 일치) — station_theta 지정 또는 영역을 벽쪽으로 확장"); continue; }
            valid.Add((a, sx, sy, theta.Value));
        }

        // 층별 유효 T_W_D 사전 검증 (§2.5 승계). 정차각·T_W_D 결함은 모두 모아 한 번에 400 (조용한 생성 금지)
        var levels = valid.Select(v => v.Area.Level).Distinct().ToList();
        var calibByLevel = new Dictionary<int, (string MapId, DrawingTransform T)>();
        var missing = new List<string>();
        foreach (var level in levels)
        {
            var map = await _db.Maps.AsNoTracking()
                .FirstOrDefaultAsync(m => m.TankId == scenario.TankId && m.Level == level && m.IsActive, ct);
            if (map is null) { missing.Add($"L{level}: 활성 맵 없음"); continue; }
            var cal = await _db.MapCalibrations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.MapId == map.MapId && c.MapVersion == map.Version, ct);
            if (cal is null) { missing.Add($"L{level}({map.MapId} v{map.Version}): 유효 T_W_D 없음"); continue; }
            calibByLevel[level] = (map.MapId, new DrawingTransform(cal.Tx, cal.Ty, cal.YawRad));
        }
        if (missing.Count > 0 || thetaMissing.Count > 0)
            throw new SeamPlanningService.CalibrationMissingException(missing.Concat(thetaMissing).ToList());

        // 재실행 안전
        var existingPoints = await _db.InspectionPoints.Where(p => p.ScenarioId == scenarioId).ToListAsync(ct);
        if (existingPoints.Count > 0) _db.InspectionPoints.RemoveRange(existingPoints);

        // Phase 1: STATION 노드 선확정 (edge/point FK 삽입 순서 보장)
        var stationPos = new Dictionary<Guid, (string NodeId, string MapId, double Mx, double My,
            double SdX, double SdY, double SdTheta)>();
        foreach (var (a, sx, sy, theta) in valid)
        {
            var (mapId, tWd) = calibByLevel[a.Level];
            var (mx, my) = tWd.DrawingToMap(sx, sy);
            double mTheta = tWd.DrawingYawToMap(theta);
            string nodeId = $"{a.TankId}-L{a.Level}-{a.WallCode}-{a.Name}";

            var node = await _db.Nodes.FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
            if (node is null) { node = new NodeEntity { NodeId = nodeId }; _db.Nodes.Add(node); }
            node.MapId = mapId; node.Name = nodeId; node.X = mx; node.Y = my; node.Theta = mTheta;
            node.NodeType = "STATION"; node.AllowedDevXy = StationDevXy; node.AllowedDevTheta = StationDevTheta;
            stationPos[a.AreaId] = (nodeId, mapId, mx, my, sx, sy, theta);
        }
        await _db.SaveChangesAsync(ct);

        // Phase 2: 엣지 연결 + InspectionPoint/Task 생성
        int pointSeq = 0, taskCount = 0;
        foreach (var (a, _, _, _) in valid)
        {
            var sp = stationPos[a.AreaId];
            await ConnectNearestAsync(sp.NodeId, sp.MapId, sp.Mx, sp.My, ct);

            var point = new InspectionPointEntity
            {
                PointId = Guid.NewGuid(), ScenarioId = scenarioId, Seq = pointSeq++, NodeId = sp.NodeId
            };
            foreach (var t in a.Tasks)
            {
                var start = ParseVec3(t.StartDrawing) ?? new double[3];
                var end = ParseVec3(t.EndDrawing) ?? new double[3];
                var position = new
                {
                    tank = a.TankId, level = a.Level, wall_code = a.WallCode,
                    seamStartDrawing = start,
                    seamEndDrawing = end,
                    stationDrawing = new { x = sp.SdX, y = sp.SdY, theta = sp.SdTheta },
                    areaBounds = new { minX = a.MinX, minY = a.MinY, maxX = a.MaxX, maxY = a.MaxY }
                };
                var pars = new
                {
                    seamType = t.SeamType, sectionDxfId = t.SectionDxfId, inspectionProfileId = t.ProfileId,
                    standoffMm, workingDistanceMm = workingMm,
                    anchorGroupId = sp.NodeId, seqInGroup = t.Seq
                };
                point.Tasks.Add(new InspectionTaskEntity
                {
                    TaskId = Guid.NewGuid(), PointId = point.PointId, Seq = t.Seq,
                    ActionType = "startWeldInspection",
                    JobRef = $"JOB-{sp.NodeId}-{t.Seq}",
                    Position = JsonSerializer.Serialize(position),
                    Params = JsonSerializer.Serialize(pars)
                });
                taskCount++;
            }
            _db.InspectionPoints.Add(point);
        }

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "system", Action = "AREA_GENERATE", Target = scenarioId.ToString(),
            Detail = JsonSerializer.Serialize(new { stations = valid.Count, tasks = taskCount, skipped })
        });
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("generate-from-areas: scenario={Scenario} stations={Stations} tasks={Tasks} skipped={Skipped}",
            scenarioId, valid.Count, taskCount, skipped.Count);
        return new GenerateResult(valid.Count, taskCount, skipped);
    }

    // ── 조회(UI 렌더) ──
    public sealed record AreaView(Guid AreaId, string TankId, int Level, string WallCode, string Name,
        double MinX, double MinY, double MaxX, double MaxY,
        double StationX, double StationY, double StationTheta, bool IsOverride, int SortOrder, int TaskCount);
    public sealed record AreaTaskView(Guid TaskId, int Seq, string? Name, double[] SeamStart, double[] SeamEnd,
        string SeamType, string SectionDxfId, string ProfileId);

    public async Task<IReadOnlyList<AreaView>> GetAreasAsync(string tankId, int? level, string? wallCode,
        CancellationToken ct = default)
    {
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (level is not null) q = q.Where(a => a.Level == level);
        if (wallCode is not null) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.ToListAsync(ct);
        return areas
            .OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a =>
            {
                // 읽기용 정차 pose — 위치=오버라이드/중앙, 각도=station_theta ?? seam 자동(불명 시 0) [정차각 자동화]
                var (cx, cy) = AreaGeometry.AreaCenter(a.MinX, a.MinY, a.MaxX, a.MaxY);
                double sx = a.StationX ?? cx, sy = a.StationY ?? cy;
                var targets = a.Tasks
                    .SelectMany(t => new[] { ParseVec3(t.StartDrawing), ParseVec3(t.EndDrawing) })
                    .Where(p => p is not null).Select(p => p!).ToList();
                double st = a.StationTheta ?? AreaGeometry.FacingYawToward(sx, sy, targets) ?? 0.0;
                bool over = a.StationX is not null || a.StationY is not null || a.StationTheta is not null;
                return new AreaView(a.AreaId, a.TankId, a.Level, a.WallCode, a.Name,
                    a.MinX, a.MinY, a.MaxX, a.MaxY, sx, sy, st, over, a.SortOrder, a.Tasks.Count);
            }).ToList();
    }

    public async Task<IReadOnlyList<AreaTaskView>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default)
    {
        var tasks = await _db.AreaTasks.AsNoTracking().Where(t => t.AreaId == areaId)
            .OrderBy(t => t.Seq).ToListAsync(ct);
        return tasks.Select(t => new AreaTaskView(t.TaskId, t.Seq, t.Name,
            ParseVec3(t.StartDrawing) ?? new double[3], ParseVec3(t.EndDrawing) ?? new double[3],
            t.SeamType, t.SectionDxfId, t.ProfileId)).ToList();
    }

    /// <summary>같은 맵의 기존 비-STATION 노드 중 최근접에 양방향 TRAVEL 엣지 (get-or-create).</summary>
    private async Task ConnectNearestAsync(string stationNodeId, string mapId, double mx, double my, CancellationToken ct)
    {
        var nearest = await _db.Nodes.AsNoTracking()
            .Where(n => n.MapId == mapId && n.NodeId != stationNodeId && n.NodeType != "STATION")
            .OrderBy(n => (n.X - mx) * (n.X - mx) + (n.Y - my) * (n.Y - my))
            .FirstOrDefaultAsync(ct);
        if (nearest is null)
        {
            _log.LogWarning("STATION {Node}: 연결할 주행 노드 없음 (맵 {Map})", stationNodeId, mapId);
            return;
        }
        string edgeId = $"{stationNodeId}--{nearest.NodeId}";
        if (!await _db.Edges.AnyAsync(e => e.EdgeId == edgeId, ct))
            _db.Edges.Add(new EdgeEntity
            {
                EdgeId = edgeId, MapId = mapId, StartNodeId = stationNodeId, EndNodeId = nearest.NodeId,
                Bidirectional = true, EdgeType = "TRAVEL"
            });
    }

    private static double[]? ParseVec3(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<double[]>(json);
            if (v is { Length: >= 2 }) return new[] { v[0], v[1], v.Length >= 3 ? v[2] : 0 };
        }
        catch (JsonException) { }
        return null;
    }
}
