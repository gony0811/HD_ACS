using System.Text.Json;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// WeldSeam → 스테이션/TASK 자동 생성 [PHASE2 WP-2, SPEC §3.3].
/// SeamSlicer(도면 좌표 순수계산) → 유효 T_W_D 적용(도면 pose → 맵 pose) → ref.node(STATION)+엣지
/// + InspectionPoint/Task 생성. 유효 T_W_D 없는 층은 명시적 실패(§2.5, 조용한 기본값 금지).
/// </summary>
public sealed class SeamPlanningService
{
    private readonly AcsDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<SeamPlanningService> _log;

    // ref.node(STATION) 기본 허용편차 [SPEC §3.3]
    private const double StationDevXy = 0.08;
    private const double StationDevTheta = 0.07;

    public SeamPlanningService(AcsDbContext db, IConfiguration config, ILogger<SeamPlanningService> log)
    {
        _db = db; _config = config; _log = log;
    }

    public sealed record GenerateResult(int Stations, int Tasks, IReadOnlyList<string> Skipped);

    /// <summary>유효 T_W_D 누락 등 릴리즈 불가 사유 — 400 매핑용.</summary>
    public sealed class CalibrationMissingException(IReadOnlyList<string> reasons)
        : Exception("유효 T_W_D 없음: " + string.Join("; ", reasons))
    {
        public IReadOnlyList<string> Reasons { get; } = reasons;
    }

    public async Task<GenerateResult> GenerateAsync(
        Guid scenarioId, IReadOnlyList<Guid>? seamIds, string? userId, CancellationToken ct = default)
    {
        var scenario = await _db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.ScenarioId == scenarioId, ct)
            ?? throw new InvalidOperationException($"scenario '{scenarioId}' 없음");

        // 대상 seam 로드 (지정 없으면 시나리오 tank 전체)
        var seamQuery = _db.WeldSeams.AsNoTracking().Where(w => w.TankId == scenario.TankId);
        if (seamIds is { Count: > 0 })
            seamQuery = seamQuery.Where(w => seamIds.Contains(w.SeamId));
        var seamRows = await seamQuery.ToListAsync(ct);

        var cfg = LoadSlicerConfig();
        var skipped = new List<string>();

        // WeldSeamEntity → SeamInput (jsonb 파싱). 파싱 실패/기하 부족은 skipped.
        var inputs = new List<SeamInput>();
        foreach (var w in seamRows)
        {
            var path = ParsePath(w.PathDrawing);
            var normal = ParseVec3(w.NormalDrawing);
            if (path.Count < 2 || normal is null)
            {
                skipped.Add($"seam {w.SeamId}: 경로 2점 미만 또는 법선 파싱 실패");
                continue;
            }
            inputs.Add(new SeamInput(w.SeamId.ToString(), w.TankId, w.Level, w.WallCode, w.SeamType,
                path, normal.Value, w.SectionDxfId, w.ProfileId));
        }

        var stations = SeamSlicer.Slice(inputs, cfg);

        // 결과에 등장하는 층별 유효 T_W_D 사전 검증 — 하나라도 없으면 아무것도 만들지 않고 실패(§2.5)
        var levels = stations.Select(s => s.Tasks[0].Level).Distinct().ToList();
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
        if (missing.Count > 0)
            throw new CalibrationMissingException(missing);

        // 재실행 안전: 이 시나리오의 기존 검사지점(+TASK cascade) 제거 후 재생성
        var existingPoints = await _db.InspectionPoints.Where(p => p.ScenarioId == scenarioId).ToListAsync(ct);
        if (existingPoints.Count > 0) _db.InspectionPoints.RemoveRange(existingPoints);

        // Phase 1: STATION 노드 get-or-create → 먼저 저장. edge.start_node_id / inspection_point.node_id
        // FK는 EF 모델에 관계가 없어(DB에만 존재) 삽입 순서가 보장되지 않으므로 노드를 선확정한다.
        var stationPos = new Dictionary<string, (string MapId, double Mx, double My)>();
        foreach (var st in stations)
        {
            var level = st.Tasks[0].Level;
            var (mapId, tWd) = calibByLevel[level];
            var (mx, my) = tWd.DrawingToMap(st.StationDrawing.X, st.StationDrawing.Y);
            double mTheta = tWd.DrawingYawToMap(st.StationDrawing.Theta);

            var node = await _db.Nodes.FirstOrDefaultAsync(n => n.NodeId == st.AnchorGroupId, ct);
            if (node is null)
            {
                node = new NodeEntity { NodeId = st.AnchorGroupId };
                _db.Nodes.Add(node);
            }
            node.MapId = mapId; node.Name = st.AnchorGroupId; node.X = mx; node.Y = my; node.Theta = mTheta;
            node.NodeType = "STATION"; node.AllowedDevXy = StationDevXy; node.AllowedDevTheta = StationDevTheta;
            stationPos[st.AnchorGroupId] = (mapId, mx, my);
        }
        await _db.SaveChangesAsync(ct);   // 노드 확정 (+ 기존 검사지점 삭제 반영)

        // Phase 2: 최근접 주행 노드와 TRAVEL 엣지 연결 + InspectionPoint/Task 생성
        double workingMm = _config.GetValue("Acs:Slicer:WorkingDistanceMm", cfg.StandoffM * 1000.0);
        int pointSeq = 0, taskCount = 0;

        foreach (var st in stations)
        {
            var (mapId, mx, my) = stationPos[st.AnchorGroupId];
            await ConnectNearestAsync(st.AnchorGroupId, mapId, mx, my, ct);

            var point = new InspectionPointEntity
            {
                PointId = Guid.NewGuid(), ScenarioId = scenarioId, Seq = pointSeq++, NodeId = st.AnchorGroupId
            };
            foreach (var t in st.Tasks)
            {
                var position = new
                {
                    tank = t.TankId, level = t.Level, wall_code = t.WallCode,
                    seamStartDrawing = new[] { t.SeamStartDrawing.X, t.SeamStartDrawing.Y, t.SeamStartDrawing.Z },
                    seamEndDrawing = new[] { t.SeamEndDrawing.X, t.SeamEndDrawing.Y, t.SeamEndDrawing.Z },
                    wallNormalDrawing = new[] { t.WallNormalDrawing.X, t.WallNormalDrawing.Y, t.WallNormalDrawing.Z },
                    stationDrawing = new { x = st.StationDrawing.X, y = st.StationDrawing.Y, theta = st.StationDrawing.Theta }
                };
                var pars = new
                {
                    seamType = t.SeamType, sectionDxfId = t.SectionDxfId, inspectionProfileId = t.ProfileId,
                    standoffMm = cfg.StandoffM * 1000.0, workingDistanceMm = workingMm,
                    anchorGroupId = t.AnchorGroupId, seqInGroup = t.SeqInGroup
                };
                point.Tasks.Add(new InspectionTaskEntity
                {
                    TaskId = Guid.NewGuid(), PointId = point.PointId, Seq = t.SeqInGroup,
                    ActionType = "startWeldInspection",
                    JobRef = $"JOB-{t.AnchorGroupId}-{t.SeqInGroup}",
                    Position = JsonSerializer.Serialize(position),
                    Params = JsonSerializer.Serialize(pars)
                });
                taskCount++;
            }
            _db.InspectionPoints.Add(point);
        }

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "system", Action = "SEAM_GENERATE", Target = scenarioId.ToString(),
            Detail = JsonSerializer.Serialize(new { stations = stations.Count, tasks = taskCount, skipped })
        });
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("generate-from-seams: scenario={Scenario} stations={Stations} tasks={Tasks} skipped={Skipped}",
            scenarioId, stations.Count, taskCount, skipped.Count);
        return new GenerateResult(stations.Count, taskCount, skipped);
    }

    /// <summary>같은 맵의 기존 비-STATION 노드 중 최근접에 양방향 TRAVEL 엣지 생성 (get-or-create).</summary>
    private async Task ConnectNearestAsync(string stationNodeId, string mapId, double mx, double my, CancellationToken ct)
    {
        var nearest = await _db.Nodes.AsNoTracking()
            .Where(n => n.MapId == mapId && n.NodeId != stationNodeId && n.NodeType != "STATION")
            .OrderBy(n => (n.X - mx) * (n.X - mx) + (n.Y - my) * (n.Y - my))
            .FirstOrDefaultAsync(ct);
        if (nearest is null)
        {
            _log.LogWarning("STATION {Node}: 연결할 주행 노드가 없어 엣지 미생성 (맵 {Map})", stationNodeId, mapId);
            return;
        }
        string edgeId = $"{stationNodeId}--{nearest.NodeId}";
        if (!await _db.Edges.AnyAsync(e => e.EdgeId == edgeId, ct))
            _db.Edges.Add(new EdgeEntity
            {
                EdgeId = edgeId, MapId = mapId,
                StartNodeId = stationNodeId, EndNodeId = nearest.NodeId,
                Bidirectional = true, EdgeType = "TRAVEL"
            });
    }

    private SlicerConfig LoadSlicerConfig() => new(
        CobotReachM: _config.GetValue("Acs:Slicer:CobotReachM", 1.0),
        OverlapM: _config.GetValue("Acs:Slicer:OverlapM", 0.2),
        StandoffM: _config.GetValue("Acs:Slicer:StandoffM", 0.4),
        StationThetaOffset: _config.GetValue("Acs:Slicer:StationThetaOffset", 0.0),
        MergeDistM: _config.GetValue("Acs:Slicer:MergeDistM", 0.3));

    private static List<(double X, double Y, double Z)> ParsePath(string json)
    {
        var result = new List<(double, double, double)>();
        try
        {
            var arr = JsonSerializer.Deserialize<List<double[]>>(json);
            if (arr is null) return result;
            foreach (var p in arr)
                if (p.Length >= 2) result.Add((p[0], p[1], p.Length >= 3 ? p[2] : 0));
        }
        catch (JsonException) { /* skipped 처리 */ }
        return result;
    }

    private static (double X, double Y, double Z)? ParseVec3(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<double[]>(json);
            if (v is { Length: >= 2 }) return (v[0], v[1], v.Length >= 3 ? v[2] : 0);
        }
        catch (JsonException) { }
        return null;
    }
}
