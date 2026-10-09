using HD.Acs.App.Hubs;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 배터리 교체 장소 등록·조회 + 운영자 수동 교체 이동 Order 발행
/// [HD_AMR 배터리관리 §6, §9.3·§9.7].
///
/// 핵심 결정:
///  - 교체 장소는 ref.node(node_type=BATTERY_SWAP)로 **mapId당 1곳**(사양서 §6).
///  - 디스패치는 **운영자 수동 트리거**뿐(AUTO 자동 디스패치 폴백 없음 — 사양서 §9.6과 정합:
///    사람이 교체하므로 사람이 호출 경로가 본래 일관).
///  - 활성 run이 있으면 AbortRunAsync 호출(§5 "ACS가 잔여 회수" 패턴을 abort/resume으로 매핑).
///    cancelOrder instantAction은 발행하지 않는다 — 핫스왑 전제라 교체 후 운영자가 '이어하기'로 복귀.
///  - 교체 Order는 **미션 레코드를 만들지 않고** Vda5050MasterClient로 직접 발행 →
///    state 대조에서 mission 미매칭이 되지만 ACS는 교체 Order의 진행률을 추적하지 않는다(사양서 §9.3).
///    감사로그(BATTERY_SWAP_DISPATCH)와 알람(BATTERY_SWAP_DISPATCHED)으로 추적한다.
/// </summary>
public sealed class BatterySwapService
{
    private const string NodeType = "BATTERY_SWAP";

    private readonly AcsDbContext _db;
    private readonly Vda5050MasterClient _vda;
    private readonly InspectionDispatcher _dispatcher;
    private readonly MissionService _missions;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<BatterySwapService> _log;

    public BatterySwapService(AcsDbContext db, Vda5050MasterClient vda,
        InspectionDispatcher dispatcher, MissionService missions,
        IHubContext<MonitoringHub> hub, IConfiguration config,
        ILogger<BatterySwapService> log)
    {
        _db = db; _vda = vda; _dispatcher = dispatcher; _missions = missions;
        _hub = hub; _config = config; _log = log;
    }

    public sealed record SwapNodeDto(string NodeId, string MapId, string? Name,
        double X, double Y, double? Theta);

    public sealed record SwapRegisterRequest(string MapId, string Name,
        double X, double Y, double? Theta, string? NodeId, string? UserId);

    public sealed record SwapDispatchRequest(string? Reason, string? UserId);

    public sealed record SwapDispatchResult(string OrderId, string TargetNodeId, string MapId,
        string Reason, Guid? AbortedRunId);

    public async Task<List<SwapNodeDto>> ListAsync(string? mapId, CancellationToken ct)
    {
        var q = _db.Nodes.AsNoTracking().Where(n => n.NodeType == NodeType);
        if (!string.IsNullOrWhiteSpace(mapId)) q = q.Where(n => n.MapId == mapId);
        return await q.OrderBy(n => n.MapId).ThenBy(n => n.NodeId)
            .Select(n => new SwapNodeDto(n.NodeId, n.MapId, n.Name, n.X, n.Y, n.Theta))
            .ToListAsync(ct);
    }

    /// <summary>교체 장소 1곳 등록. mapId당 1곳만 허용(사양서 §6) — 이미 있으면 409.</summary>
    public async Task<SwapNodeDto> RegisterAsync(SwapRegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MapId))
            throw new BatterySwapException(400, "mapId는 필수입니다.");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BatterySwapException(400, "name은 필수입니다.");
        if (!double.IsFinite(req.X) || !double.IsFinite(req.Y))
            throw new BatterySwapException(400, "좌표는 유한한 숫자여야 합니다.");
        if (req.Theta is double th && !double.IsFinite(th))
            throw new BatterySwapException(400, "theta는 유한한 숫자여야 합니다.");

        if (!await _db.Maps.AsNoTracking().AnyAsync(m => m.MapId == req.MapId, ct))
            throw new BatterySwapException(404, $"map '{req.MapId}' 없음");

        if (await _db.Nodes.AsNoTracking().AnyAsync(n => n.MapId == req.MapId && n.NodeType == NodeType, ct))
            throw new BatterySwapException(409,
                $"층 '{req.MapId}'에 이미 교체 장소가 등록되어 있습니다 — 한 층에 1곳만 허용(HD_AMR 배터리관리 §6). 기존 노드를 지우고 다시 등록하세요.");

        var nodeId = string.IsNullOrWhiteSpace(req.NodeId)
            ? $"{req.MapId}-BAT-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}"
            : req.NodeId.Trim();
        if (await _db.Nodes.AsNoTracking().AnyAsync(n => n.NodeId == nodeId, ct))
            throw new BatterySwapException(409, $"nodeId '{nodeId}' 중복");

        _db.Nodes.Add(new NodeEntity
        {
            NodeId = nodeId, MapId = req.MapId, Name = req.Name, NodeType = NodeType,
            X = req.X, Y = req.Y, Theta = req.Theta,
            AllowedDevXy = _config.GetValue("Acs:Dispatch:AllowedDevXy", 0.08),
            AllowedDevTheta = _config.GetValue("Acs:Dispatch:AllowedDevTheta", 0.07),
        });
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "BATTERY_SWAP_NODE_UPSERT", Target = nodeId,
            Detail = System.Text.Json.JsonSerializer.Serialize(new { req.MapId, req.Name, req.X, req.Y, req.Theta }),
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("배터리 교체 장소 등록 — {NodeId} @ {MapId} ({X:F2},{Y:F2})", nodeId, req.MapId, req.X, req.Y);

        return new SwapNodeDto(nodeId, req.MapId, req.Name, req.X, req.Y, req.Theta);
    }

    public async Task DeleteAsync(string nodeId, string? userId, CancellationToken ct)
    {
        var node = await _db.Nodes.FirstOrDefaultAsync(n => n.NodeId == nodeId && n.NodeType == NodeType, ct);
        if (node is null)
            throw new BatterySwapException(404, $"교체 장소 '{nodeId}' 없음");

        _db.Nodes.Remove(node);
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "", Action = "BATTERY_SWAP_NODE_DELETE", Target = nodeId,
            Detail = System.Text.Json.JsonSerializer.Serialize(new { node.MapId, node.Name }),
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("배터리 교체 장소 삭제 — {NodeId} @ {MapId}", nodeId, node.MapId);
    }

    /// <summary>
    /// 운영자 수동 교체 디스패치. 로봇 현재 층의 교체 장소로 단일 노드 Order 1건을 발행한다.
    /// 활성 run이 있으면 함께 중단(§5의 "잔여 회수" 패턴 = abort/resume) — 교체 후 운영자가 '이어하기'로 재배차.
    /// </summary>
    public async Task<SwapDispatchResult> DispatchSwapAsync(string robotId, SwapDispatchRequest req, CancellationToken ct)
    {
        var robot = await _db.Robots.AsNoTracking().FirstOrDefaultAsync(r => r.RobotId == robotId, ct)
            ?? throw new BatterySwapException(404, $"robot '{robotId}' 없음");

        // 연결 확인 — EnsureRobotConnectedAsync는 미연결 시 RobotNotConnectedException + ROBOT_NOT_CONNECTED 알람.
        await _dispatcher.EnsureRobotConnectedAsync(robotId, "배터리 교체 이동", ct);

        var ctx = await _db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId, ct);
        if (ctx?.ReportedMapId is not { } mapId || string.IsNullOrWhiteSpace(mapId))
            throw new BatterySwapException(400, "로봇 보고 층(mapId)이 없습니다 — state 수신 후 다시 시도하세요.");

        var swap = await _db.Nodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.MapId == mapId && n.NodeType == NodeType, ct)
            ?? throw new BatterySwapException(400,
                $"현재 층 '{mapId}'에 배터리 교체 장소가 등록되어 있지 않습니다 — 계획 ▸ 배터리 교체 장소 탭에서 등록하세요.");

        // 활성 run이 있으면 abort(§5): 교체 후 '이어하기'로 재배차. 자동 resume은 하지 않는다(핫스왑 완료 판단은 운영자).
        Guid? abortedRunId = null;
        var active = await _db.ScenarioRuns.AsNoTracking()
            .Where(r => r.RobotId == robotId && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
            .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(ct);
        if (active is Guid runId)
        {
            try { await _missions.AbortRunAsync(runId, ct); abortedRunId = runId; }
            catch (RunStateException) { /* 이미 종결 — 무해 */ }
        }

        var reasonRaw = (req.Reason ?? "MANUAL").Trim().ToUpperInvariant();
        var reason = reasonRaw is "LOW" or "CRITICAL" or "MANUAL" ? reasonRaw : "MANUAL";

        var orderId = Guid.NewGuid().ToString();
        var actionId = Guid.NewGuid().ToString();
        var order = new Vda5050Order { OrderId = orderId, OrderUpdateId = 0 };
        order.Nodes.Add(new OrderNode
        {
            NodeId = swap.NodeId, SequenceId = 0, Released = true,
            NodePosition = new NodePosition
            {
                X = swap.X, Y = swap.Y, Theta = swap.Theta, MapId = swap.MapId,
                AllowedDeviationXY = swap.AllowedDevXy ?? _config.GetValue("Acs:Dispatch:AllowedDevXy", 0.08),
                AllowedDeviationTheta = swap.AllowedDevTheta ?? _config.GetValue("Acs:Dispatch:AllowedDevTheta", 0.07),
            },
            Actions = { new VdaAction
            {
                ActionType = "batterySwapMove", ActionId = actionId, BlockingType = "HARD",
                ActionParameters =
                {
                    new ActionParameter { Key = "targetNodeId", Value = swap.NodeId },
                    new ActionParameter { Key = "mapId",        Value = swap.MapId },
                    new ActionParameter { Key = "reason",       Value = reason },
                },
            } },
        });

        await _vda.PublishOrderAsync(new RobotRef(robotId, robot.Manufacturer, robot.SerialNumber), order, ct);

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "BATTERY_SWAP_DISPATCH", Target = robotId,
            Detail = System.Text.Json.JsonSerializer.Serialize(new
            { orderId, swap.NodeId, swap.MapId, reason, abortedRunId }),
        });
        await _db.SaveChangesAsync(ct);

        // 알람(INFO) — 발행 사실을 운영자 시각으로 알린다. UI 이벤트 로그가 흡수한다.
        await _dispatcher.RaiseAlarmAsync("BATTERY_SWAP_DISPATCHED", "INFO",
            $"배터리 교체 이동 발행 — {robotId} → {swap.MapId}/{swap.NodeId} ({reason})",
            robotId, null,
            new { orderId, targetNodeId = swap.NodeId, swap.MapId, reason, abortedRunId }, ct);

        await _hub.Clients.All.SendAsync("BatterySwapDispatched", new
        { robotId, orderId, targetNodeId = swap.NodeId, mapId = swap.MapId, reason, abortedRunId }, ct);

        _log.LogInformation("배터리 교체 이동 발행 — robot={Robot} node={Node} map={Map} reason={Reason} abortedRun={Run}",
            robotId, swap.NodeId, swap.MapId, reason, abortedRunId);

        return new SwapDispatchResult(orderId, swap.NodeId, swap.MapId, reason, abortedRunId);
    }
}

/// <summary>Service-layer 에러 — Program.cs가 StatusCode를 그대로 응답한다.</summary>
public sealed class BatterySwapException : Exception
{
    public int StatusCode { get; }
    public BatterySwapException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
