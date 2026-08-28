using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Abstractions;
using HD.Acs.Core.Domain;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 시나리오 실행/미션 릴리즈 — 층 단위 미션 분할 [GRAPH_DATA_MODEL 8.4].
/// Order는 전체 Base 선릴리즈 [ADR-002], 릴리즈 가드: 미션 층 == 로봇 보고 층 [Q9].
/// </summary>
public sealed class MissionService
{
    private readonly AcsDbContext _db;
    private readonly Vda5050MasterClient _vda;
    private readonly InspectionDispatcher _dispatcher;
    private readonly OrderBuilder _orderBuilder = new();
    private readonly ILogger<MissionService> _log;

    public MissionService(AcsDbContext db, Vda5050MasterClient vda, InspectionDispatcher dispatcher, ILogger<MissionService> log)
    {
        _db = db; _vda = vda; _dispatcher = dispatcher; _log = log;
    }

    /// <summary>
    /// 시나리오 → Run 생성. 선창 영역을 작업 큐로 전개(층별 미션 포함)한 뒤 첫 정차를 greedy 최근접 배차.
    /// 검사 순서는 고정 시퀀스가 아니라 "현재 층 미검사 중 로봇 최근접"으로 동적 결정된다.
    /// </summary>
    public async Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default)
    {
        var scenario = await _db.Scenarios.AsNoTracking().FirstAsync(s => s.ScenarioId == scenarioId, ct);

        var run = new ScenarioRunEntity
        {
            RunId = Guid.NewGuid(),
            ScenarioId = scenario.ScenarioId,
            ScenarioVer = scenario.Version,
            RobotId = robotId,
            State = "RUNNING",
            StartedAt = DateTimeOffset.UtcNow
        };
        _db.ScenarioRuns.Add(run);
        await _dispatcher.BuildQueueAsync(run, scenario.TankId, ct);   // 영역→작업 큐 + 층별 미션
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchNextAsync(run.RunId, ct);            // 첫 정차 배차(로봇 층 일치 시)
        return run.RunId;
    }

    /// <summary>
    /// 다음 CREATED 미션 릴리즈. 층 검증 게이트: 로봇 보고 mapId == 미션 mapId 일 때만.
    /// 불일치 시 Run을 WAITING_FLOOR_TRANSFER로 두고 작업자 수동 절차를 기다린다 [Q9].
    /// </summary>
    public async Task<bool> TryReleaseNextMissionAsync(Guid runId, CancellationToken ct = default)
    {
        // 영역 기반 run(greedy 배차)은 디스패처가 층 게이트·배차를 담당 — 수동 층 변경 후 재개도 이 경로.
        if (await _db.WorkItems.AnyAsync(w => w.RunId == runId, ct))
        {
            await _dispatcher.DispatchNextAsync(runId, ct);
            return true;
        }

        var run = await _db.ScenarioRuns.Include(r => r.Missions.OrderBy(m => m.Seq))
            .FirstAsync(r => r.RunId == runId, ct);
        var next = run.Missions.FirstOrDefault(m => m.State == nameof(MissionState.Created));
        if (next == null)
        {
            run.State = "COMPLETED"; run.EndedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return false;
        }

        var ctx = await _db.RobotContexts.FindAsync(new object[] { run.RobotId }, ct);
        if (ctx?.ReportedMapId != next.MapId)
        {
            run.State = "WAITING_FLOOR_TRANSFER";     // 릴리즈 가드 — 수동 층 전환 대기
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Run {Run}: 층 전환 대기 (로봇={Reported}, 미션={Required})",
                runId, ctx?.ReportedMapId, next.MapId);
            return false;
        }

        await ReleaseMissionAsync(next, ct);
        run.State = "RUNNING";
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ReleaseMissionAsync(MissionEntity mission, CancellationToken ct)
    {
        var robot = await _db.Robots.AsNoTrackingWithIdentityResolution()
            .FirstAsync(r => r.RobotId == mission.RobotId, ct);
        var run = await _db.ScenarioRuns.AsNoTracking().FirstAsync(r => r.RunId == mission.RunId, ct);

        // 이 미션(층)에 속한 검사 지점만 추출
        var points = await (
            from p in _db.InspectionPoints.AsNoTracking().Include(p => p.Tasks.OrderBy(t => t.Seq))
            join n in _db.Nodes.AsNoTracking() on p.NodeId equals n.NodeId
            where p.ScenarioId == run.ScenarioId && n.MapId == mission.MapId
            orderby p.Seq
            select p).ToListAsync(ct);

        var graph = await GraphLoader.LoadAsync(_db, mission.MapId, ct);
        var ctx = await _db.RobotContexts.AsNoTracking().FirstAsync(c => c.RobotId == mission.RobotId, ct);
        var startNodeId = FindNearestNode(graph, ctx) ?? points[0].NodeId;

        // startWeldInspection 이 있으면 릴리즈 시점 유효 T_W_D(맵버전 일치)를 선해결 — 없거나 버전 불일치면
        // 릴리즈 거부 [WP-3 §4.2/§2.5]. param_schema 는 발행 직전 검증용으로 1회 로드.
        DrawingTransform? weldTransform = null;
        string? weldSchema = null;
        if (points.Any(p => p.Tasks.Any(t => t.ActionType == "startWeldInspection")))
        {
            var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mission.MapId, ct)
                      ?? throw new CalibrationInvalidException($"map '{mission.MapId}' 없음 — 릴리즈 불가.");
            var cal = await _db.MapCalibrations.AsNoTracking()
                .Where(c => c.MapId == mission.MapId)
                .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync(ct);
            weldTransform = WeldInspectionPayload.ResolveTransform(
                map.Version, cal?.MapVersion, cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0);
            weldSchema = (await _db.ActionCatalog.AsNoTracking()
                .FirstOrDefaultAsync(a => a.ActionType == "startWeldInspection", ct))?.ParamSchema;
        }

        // PlannedPoint 변환 — actionId를 여기서 발급·보존 (state 대조 키)
        var planned = new List<PlannedPoint>();
        var actionRows = new List<OrderActionEntity>();
        foreach (var p in points)
        {
            var actions = new List<PlannedAction>();
            foreach (var t in p.Tasks)
            {
                var actionId = Guid.NewGuid();
                var parameters = new Dictionary<string, object?>();
                string? historyPos = t.Position;   // OrderAction.Params 이력 [ADR-004]

                if (t.ActionType == "startWeldInspection")
                {
                    // 도면 좌표 → 유효 T_W_D 적용한 맵 좌표 actionParameters + 발행 직전 스키마 검증 [§6 계약]
                    var d = ParseWeldDrawing(t.Position);
                    var actionParams = WeldInspectionPayload.BuildActionParameters(weldTransform!, d);

                    var violations = WeldInspectionPayload.ValidateSchema(weldSchema, actionParams);
                    if (violations.Count > 0)
                    {
                        _log.LogWarning("릴리즈 중단 — mission {Mission} / {Wall} payload 스키마 위반: {V}",
                            mission.MissionId, d.WallCode, string.Join("; ", violations));
                        throw new WeldPayloadSchemaException(violations);
                    }

                    foreach (var kv in actionParams) parameters[kv.Key] = kv.Value?.DeepClone();   // flat 계약 키 전개
                    historyPos = actionParams.ToJsonString();   // 실제 발행한 맵 좌표 보존
                }
                else
                {
                    if (t.JobRef != null) parameters["jobRef"] = t.JobRef;
                    if (t.Position != null) parameters["position"] = JsonSerializer.Deserialize<object>(t.Position);
                    if (t.Params != null) parameters["params"] = JsonSerializer.Deserialize<object>(t.Params);
                }

                actions.Add(new PlannedAction(actionId, t.ActionType, "HARD", parameters));
                actionRows.Add(new OrderActionEntity
                {
                    ActionId = actionId, MissionId = mission.MissionId, TaskId = t.TaskId,
                    ActionType = t.ActionType, BlockingType = "HARD",
                    Params = historyPos
                });
            }
            planned.Add(new PlannedPoint(p.NodeId, actions));
        }

        var order = _orderBuilder.Build(mission.OrderId, mission.OrderUpdateId, startNodeId, planned, graph);

        // Order 스냅샷 저장 (짝수=노드, 홀수=엣지)
        foreach (var n in order.Nodes)
        {
            _db.OrderNodes.Add(new OrderNodeEntity
            {
                MissionId = mission.MissionId, SequenceId = n.SequenceId, NodeId = n.NodeId,
                X = n.NodePosition!.X, Y = n.NodePosition.Y, Theta = n.NodePosition.Theta
            });
            foreach (var a in n.Actions)
            {
                var row = actionRows.First(r => r.ActionId == Guid.Parse(a.ActionId));
                row.NodeSequenceId = n.SequenceId;
            }
        }
        foreach (var e in order.Edges)
            _db.OrderEdges.Add(new OrderEdgeEntity
            {
                MissionId = mission.MissionId, SequenceId = e.SequenceId,
                EdgeId = e.EdgeId, StartNodeId = e.StartNodeId, EndNodeId = e.EndNodeId
            });
        _db.OrderActions.AddRange(actionRows);

        await _vda.PublishOrderAsync(new RobotRef(robot.RobotId, robot.Manufacturer, robot.SerialNumber), order, ct);

        mission.State = nameof(MissionState.Released);
        mission.StartedAt = DateTimeOffset.UtcNow;
        _db.TransitionLogs.Add(new TransitionLogEntity
        {
            MissionId = mission.MissionId,
            FromState = nameof(MissionState.Created), ToState = nameof(MissionState.Released),
            Trigger = nameof(MissionTrigger.Release)
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 수동 지점 이동 [2D 평면도 우클릭 "여기로 이동"] — 도면 좌표로 단일 노드 Order 발행.
    /// 층 게이트: 로봇 보고 mapId == 대상 mapId 일 때만 이동(다른 층이면 FloorMismatchException, 이동 금지).
    /// 도면→맵 변환: 대상 맵의 유효 T_W_D(맵버전 일치)가 있으면 적용, 없으면 항등(도면≈맵 placeholder — 3D 마커와 동일).
    /// 경로 계획·자세는 HD_AMR 책임 — ACS는 목표 지점만 전달한다.
    /// </summary>
    public async Task<(double MapX, double MapY)> ManualGotoAsync(
        string robotId, string mapId, double drawingX, double drawingY, double? theta, string userId,
        CancellationToken ct = default)
    {
        var robot = await _db.Robots.AsNoTracking().FirstOrDefaultAsync(r => r.RobotId == robotId, ct)
                    ?? throw new InvalidOperationException($"robot '{robotId}' 없음.");
        var ctx = await _db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId, ct);

        // ── 층 게이트: 로봇이 대상 층을 보고 중일 때만 이동 허용 (다른 층이면 이동 금지)
        var reported = ctx?.ReportedMapId;
        if (!string.Equals(reported, mapId, StringComparison.OrdinalIgnoreCase))
            throw new FloorMismatchException(reported, mapId);

        // ── 이동 불가 구역 게이트: 대상 지점(도면 좌표)이 등록된 NOGO 구역 안이면 이동 거부
        var (tankOfMap, levelOfMap) = ParseMapId(mapId);
        if (levelOfMap is int lvl)
        {
            // NOGO(운영 회피) + HAZARD(낙상 등 필수 회피) 모두 목표점 게이트로 차단
            var zones = await _db.MapAnnotations.AsNoTracking()
                .Where(a => a.TankId == tankOfMap && a.Level == lvl && (a.Kind == "NOGO" || a.Kind == "HAZARD"))
                .ToListAsync(ct);
            foreach (var z in zones)
            {
                var poly = JsonSerializer.Deserialize<double[][]>(z.Points);
                if (poly is { Length: >= 3 } && AreaGeometry.PointInPolygon(drawingX, drawingY, poly))
                    throw new NoGoZoneException(z.Kind == "HAZARD" ? $"낙상 위험 구역 '{z.Name}'" : z.Name);
            }
        }

        // ── 도면 좌표 → 맵 좌표 (유효 캘리브레이션 있으면 적용)
        double mapX = drawingX, mapY = drawingY;
        var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct);
        if (map is not null)
        {
            var cal = await _db.MapCalibrations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.MapId == mapId && c.MapVersion == map.Version, ct);
            if (cal is not null)
                (mapX, mapY) = new DrawingTransform(cal.Tx, cal.Ty, cal.YawRad).DrawingToMap(drawingX, drawingY);
        }

        double th = theta ?? ctx?.ReportedTheta ?? 0;

        // ── 단일 노드 Order (액션 없음 = 순수 이동). 경로 계획은 HD_AMR.
        var order = new Vda5050Order
        {
            OrderId = $"GOTO-{Guid.NewGuid():N}",
            OrderUpdateId = 0,
            Nodes =
            {
                new OrderNode
                {
                    NodeId = "goto-target",
                    SequenceId = 0,
                    Released = true,
                    NodePosition = new NodePosition { X = mapX, Y = mapY, Theta = th, MapId = mapId }
                }
            }
        };
        await _vda.PublishOrderAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber), order, ct);

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId, Action = "MANUAL_GOTO", Target = robotId,
            Detail = JsonSerializer.Serialize(new
            {
                mapId, drawing = new { x = drawingX, y = drawingY }, map = new { x = mapX, y = mapY }, theta = th
            })
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("수동 이동 — {Robot} → ({X:F2},{Y:F2}) @ {Map}", robotId, mapX, mapY, mapId);
        return (mapX, mapY);
    }

    /// <summary>작업자 수동 층(존) 변경 [Q9] — 감사 로그 + initPosition 전송</summary>
    public async Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        double x, double y, double theta, CancellationToken ct = default)
    {
        var ctx = await _db.RobotContexts.FindAsync(new object[] { robotId }, ct)
                  ?? _db.RobotContexts.Add(new RobotContextEntity { RobotId = robotId }).Entity;
        ctx.ManualMapId = mapId;
        ctx.ManualUpdatedBy = userId;
        ctx.ManualUpdatedAt = DateTimeOffset.UtcNow;

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId, Action = "MANUAL_ZONE_CHANGE", Target = robotId,
            Detail = JsonSerializer.Serialize(new { mapId, x, y, theta })
        });
        await _db.SaveChangesAsync(ct);

        var robot = await _db.Robots.AsNoTracking().FirstAsync(r => r.RobotId == robotId, ct);
        await _vda.InitPositionAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber),
            mapId, x, y, theta, ct);
        // 이후 로봇 state의 agvPosition.mapId 확인 → TryReleaseNextMissionAsync가 게이트 통과
    }

    /// <summary>Task.Position(도면 jsonb) → WeldDrawingData. seam 벡터·wall_code 추출. [SPEC v2: 법선 제거]</summary>
    private static WeldDrawingData ParseWeldDrawing(string? positionJson)
    {
        if (positionJson is null)
            throw new WeldPayloadSchemaException(new[] { "Task.Position 이 비어 있음(도면 좌표 없음)." });
        var pos = JsonNode.Parse(positionJson)!.AsObject();
        static double[] Vec(JsonObject o, string key) =>
            o[key]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
        return new WeldDrawingData(
            pos["tank"]!.GetValue<string>(),
            pos["level"]!.GetValue<int>(),
            pos["wall_code"]!.GetValue<string>(),
            Vec(pos, "seamStartDrawing"),
            Vec(pos, "seamEndDrawing"));
    }

    /// <summary>mapId "{tank}-L{level}" → (tank, level). 형식 불일치면 level=null.</summary>
    private static (string Tank, int? Level) ParseMapId(string mapId)
    {
        int i = mapId.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        if (i > 0 && int.TryParse(mapId[(i + 2)..], out var lv)) return (mapId[..i], lv);
        return (mapId, null);
    }

    private static string? FindNearestNode(Core.Graph.MapGraph graph, RobotContextEntity ctx)
    {
        if (ctx.ReportedX is not double rx || ctx.ReportedY is not double ry) return null;
        return graph.Nodes.Values
            .OrderBy(n => Math.Pow(n.X - rx, 2) + Math.Pow(n.Y - ry, 2))
            .FirstOrDefault()?.NodeId;
    }
}

/// <summary>수동 이동 층 게이트 위반 — 로봇 보고 층 ≠ 대상 층(이동 금지). 엔드포인트가 409로 매핑.</summary>
public sealed class FloorMismatchException : Exception
{
    public string? ReportedMapId { get; }
    public string RequestedMapId { get; }
    public FloorMismatchException(string? reported, string requested)
        : base($"로봇이 대상 층에 있지 않아 이동할 수 없습니다. (로봇 현재 층: {reported ?? "미보고"} ≠ 대상 층: {requested})")
    {
        ReportedMapId = reported;
        RequestedMapId = requested;
    }
}

/// <summary>수동 이동 대상 지점이 등록된 이동 불가(NOGO) 구역 내부 — 이동 금지. 엔드포인트가 409로 매핑.</summary>
public sealed class NoGoZoneException(string zoneName)
    : Exception($"이동 불가 구역 '{zoneName}' 안이라 이동할 수 없습니다.")
{
    public string ZoneName { get; } = zoneName;
}
