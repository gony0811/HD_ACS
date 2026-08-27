using System.Text.Json;
using System.Text.Json.Nodes;
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
/// 층별 greedy 최근접 동적 배차 [검사 순서 = 미검사 영역 큐 + 유휴 로봇 최근접 할당].
/// 단일 로봇 우선. 큐는 현재 층 한정 — 층 소진 시 다음 층 미션으로 넘겨 수동 층 전환 게이트를 태운다.
/// HD_ACS의 유일한 로봇측 인터페이스는 VDA 5050 하나(단일 정차 Order 발행). 검사 실행은 HD_AMR 책임.
/// </summary>
public sealed class InspectionDispatcher
{
    private readonly AcsDbContext _db;
    private readonly Vda5050MasterClient _vda;
    private readonly IInspectionOrderingPolicy _policy;
    private readonly ILogger<InspectionDispatcher> _log;
    private readonly int _maxRetries;

    public InspectionDispatcher(AcsDbContext db, Vda5050MasterClient vda,
        IInspectionOrderingPolicy policy, IConfiguration config, ILogger<InspectionDispatcher> log)
    {
        _db = db; _vda = vda; _policy = policy; _log = log;
        _maxRetries = config.GetValue("Acs:Dispatch:MaxRetries", 2);
    }

    // ── Phase 0: 선창 영역 → 작업 큐 전개 + 층별 미션 생성 ──────────────────────────
    /// <summary>
    /// run의 선창(tank)에 속한 모든 영역을 작업항목으로 전개한다("미검사 영역을 모두 큐에").
    /// 각 영역 = 정차 1곳(맵 프레임) + 그 영역 작업(용접선)의 startWeldInspection 액션들.
    /// 유효 T_W_D(맵버전 일치) 없는 층의 영역은 배차 불가라 제외(경고). 층마다 미션 1개 생성.
    /// </summary>
    public async Task BuildQueueAsync(ScenarioRunEntity run, string tankId, CancellationToken ct)
    {
        var areas = await _db.InspectionAreas.AsNoTracking()
            .Include(a => a.Tasks.OrderBy(t => t.Seq))
            .Where(a => a.TankId == tankId)
            .OrderBy(a => a.Level).ThenBy(a => a.SortOrder)
            .ToListAsync(ct);
        if (areas.Count == 0)
        {
            _log.LogWarning("Run {Run}: 선창 {Tank}에 등록된 영역이 없어 작업 큐가 비었습니다.", run.RunId, tankId);
            return;
        }

        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId)
            .ToDictionaryAsync(w => w.WallCode, ct);

        // 층별 유효 T_W_D 캐시
        var tWdByLevel = new Dictionary<int, (string MapId, DrawingTransform T)>();
        var levels = areas.Select(a => a.Level).Distinct();
        foreach (var level in levels)
        {
            var mapId = $"{tankId}-L{level}";
            var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct);
            if (map is null) { _log.LogWarning("Run {Run}: map {Map} 없음 — 층 {Lv} 제외.", run.RunId, mapId, level); continue; }
            var cal = await _db.MapCalibrations.AsNoTracking().Where(c => c.MapId == mapId)
                .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync(ct);
            try
            {
                var t = WeldInspectionPayload.ResolveTransform(map.Version, cal?.MapVersion,
                    cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0);
                tWdByLevel[level] = (mapId, t);
            }
            catch (CalibrationInvalidException ex)
            {
                _log.LogWarning("Run {Run}: 층 {Lv} T_W_D 무효 — 제외 ({Msg})", run.RunId, level, ex.Message);
            }
        }

        var seq = 0;
        var missionMaps = new HashSet<string>();
        foreach (var area in areas)
        {
            if (!tWdByLevel.TryGetValue(area.Level, out var lv)) continue;   // 층 T_W_D 없음 → 배차 불가
            if (!walls.TryGetValue(area.WallCode, out var wall)) continue;

            var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
            var corners = Json<double[][]>(area.Corners);
            var (uc, vc) = AreaGeometry.Centroid(corners);
            var stationDrawing = pose.LocalToDrawing(uc, vc);

            // 정차 맵좌표: 오버라이드(맵 프레임 가정) 우선, 없으면 centroid→도면→T_W_D
            double mx, my;
            if (area.StationX is double sx && area.StationY is double sy) { mx = sx; my = sy; }
            else (mx, my) = lv.T.DrawingToMap(stationDrawing[0], stationDrawing[1]);
            double? mTheta = area.StationTheta
                ?? (wall.FacingYaw is double fy ? lv.T.DrawingYawToMap(fy) : (double?)null);

            // 액션 payload(정차의 용접선들) 사전 구성 — 발행 시 actionId만 새로 발급
            var actionsJson = new JsonArray();
            foreach (var t in area.Tasks)
            {
                var startD = pose.LocalToDrawing(t.StartU, t.StartV);
                var endD = pose.LocalToDrawing(t.EndU, t.EndV);
                var d = new WeldDrawingData(tankId, area.Level, area.WallCode, startD, endD);
                var actionParams = WeldInspectionPayload.BuildActionParameters(lv.T, d);   // wallId·seamStart/End·orientation·patternType
                actionsJson.Add(new JsonObject
                {
                    ["actionType"] = "startWeldInspection",
                    ["taskId"] = t.TaskId.ToString(),   // 내부 대조용(AMR 미전송)
                    ["params"] = actionParams,          // §6 계약 actionParameters(flat)
                });
            }

            _db.WorkItems.Add(new WorkItemEntity
            {
                WorkItemId = Guid.NewGuid(), RunId = run.RunId, AreaId = area.AreaId,
                MapId = lv.MapId, X = mx, Y = my, Theta = mTheta, Seq = area.SortOrder,
                Status = "PENDING", Actions = actionsJson.ToJsonString(),
            });
            missionMaps.Add(lv.MapId);
        }

        // 층별 미션 1개 (수동 층 전환 게이트·상태 재사용). 층 번호 순.
        foreach (var mapId in missionMaps.OrderBy(m => m))
            run.Missions.Add(new MissionEntity
            {
                MissionId = Guid.NewGuid(), RunId = run.RunId, Seq = seq++,
                MapId = mapId, RobotId = run.RobotId,
                OrderId = Guid.NewGuid().ToString(), State = nameof(MissionState.Created),
            });
    }

    // ── Phase 2: greedy 최근접 1건 배차 (유휴 시 호출) ────────────────────────────
    /// <summary>
    /// 현재 층의 미검사 작업 중 로봇 최근접 1건을 단일 정차 Order로 발행. 후보 없으면 층 완료 처리
    /// (다른 층 남으면 WAITING_FLOOR_TRANSFER, 전부 소진이면 run COMPLETED).
    /// </summary>
    public async Task DispatchNextAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.ScenarioRuns.Include(r => r.Missions).FirstAsync(r => r.RunId == runId, ct);
        var ctx = await _db.RobotContexts.FindAsync(new object[] { run.RobotId }, ct);
        var floor = ctx?.ReportedMapId;
        if (floor is null) { _log.LogInformation("Run {Run}: 로봇 보고 층 미확인 — 배차 보류.", runId); return; }

        var pending = await _db.WorkItems
            .Where(w => w.RunId == runId && w.MapId == floor && w.Status == "PENDING")
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            // 현재 층 소진 → 그 층 미션 완료
            var doneMission = run.Missions.FirstOrDefault(m => m.MapId == floor && m.State != nameof(MissionState.Completed));
            if (doneMission is not null)
            {
                doneMission.State = nameof(MissionState.Completed);
                doneMission.EndedAt = DateTimeOffset.UtcNow;
            }
            var anyLeft = await _db.WorkItems.AnyAsync(w => w.RunId == runId && w.Status == "PENDING", ct);
            run.State = anyLeft ? "WAITING_FLOOR_TRANSFER" : "COMPLETED";
            if (!anyLeft) run.EndedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Run {Run}: 층 {Floor} 검사 완료 → {State}", runId, floor, run.State);
            return;
        }

        // greedy 최근접 선택
        var candidates = pending.Select(w => new DispatchCandidate(w.WorkItemId, w.X, w.Y)).ToList();
        var pick = _policy.SelectNext(ctx!.ReportedX ?? 0, ctx.ReportedY ?? 0, candidates);
        if (pick is null) return;
        var wi = pending.First(w => w.WorkItemId == pick.Value.WorkItemId);
        var mission = run.Missions.First(m => m.MapId == floor);

        await PublishStopAsync(run, mission, wi, ct);
    }

    private async Task PublishStopAsync(ScenarioRunEntity run, MissionEntity mission, WorkItemEntity wi, CancellationToken ct)
    {
        var robot = await _db.Robots.AsNoTracking().FirstAsync(r => r.RobotId == run.RobotId, ct);
        var orderId = Guid.NewGuid().ToString();

        var node = new OrderNode
        {
            NodeId = wi.WorkItemId.ToString(), SequenceId = 0, Released = true,
            NodePosition = new NodePosition { X = wi.X, Y = wi.Y, Theta = wi.Theta, MapId = wi.MapId },
        };
        var order = new Vda5050Order { OrderId = orderId, OrderUpdateId = 0 };
        order.Nodes.Add(node);

        var actionsJson = wi.Actions is null ? new JsonArray() : JsonNode.Parse(wi.Actions)!.AsArray();
        foreach (var an in actionsJson)
        {
            var o = an!.AsObject();
            var actionId = Guid.NewGuid();
            var vda = new VdaAction
            {
                ActionType = o["actionType"]!.GetValue<string>(), ActionId = actionId.ToString(), BlockingType = "HARD",
            };
            // §6 계약: params(flat)의 각 키를 actionParameter로 전개(wallId·seamStart/End·orientation·patternType)
            var prms = o["params"]?.AsObject();
            if (prms is not null)
                foreach (var kv in prms)
                    vda.ActionParameters.Add(new ActionParameter { Key = kv.Key, Value = kv.Value?.DeepClone() });
            node.Actions.Add(vda);

            Guid? taskId = Guid.TryParse(o["taskId"]?.GetValue<string>(), out var tid) ? tid : null;
            _db.OrderActions.Add(new OrderActionEntity
            {
                ActionId = actionId, MissionId = mission.MissionId, WorkItemId = wi.WorkItemId,
                TaskId = taskId, NodeSequenceId = 0, ActionType = vda.ActionType, BlockingType = "HARD",
                Params = o["params"]?.ToJsonString(), Status = "WAITING",
            });
        }

        _db.OrderNodes.Add(new OrderNodeEntity
        {
            MissionId = mission.MissionId, SequenceId = 0, NodeId = node.NodeId,
            X = wi.X, Y = wi.Y, Theta = wi.Theta,
        });

        // work_item DISPATCHED, mission.OrderId를 이번 정차 orderId로 갱신(state 대조), 미션/런 상태
        var tracked = await _db.WorkItems.FirstAsync(w => w.WorkItemId == wi.WorkItemId, ct);
        tracked.Status = "DISPATCHED"; tracked.OrderId = orderId; tracked.UpdatedAt = DateTimeOffset.UtcNow;
        mission.OrderId = orderId;
        mission.State = nameof(MissionState.Released);
        mission.StartedAt ??= DateTimeOffset.UtcNow;
        run.State = "RUNNING";

        await _vda.PublishOrderAsync(new RobotRef(robot.RobotId, robot.Manufacturer, robot.SerialNumber), order, ct);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Run {Run}: 정차 배차 wi={Wi} @({X:F2},{Y:F2}) map={Map}", run.RunId, wi.WorkItemId, wi.X, wi.Y, wi.MapId);
    }

    // ── 정차 완료/실패 처리 후 다음 배차 (RobotStateService 완료 훅에서 호출) ──────────
    /// <summary>현재 정차(mission.OrderId)의 액션 결과로 work_item을 DONE/FAILED(재큐잉/스킵) 처리하고 다음을 배차.</summary>
    public async Task HandleStopOutcomeAsync(MissionEntity mission, CancellationToken ct)
    {
        var wi = await _db.WorkItems
            .FirstOrDefaultAsync(w => w.OrderId == mission.OrderId && w.Status == "DISPATCHED", ct);
        if (wi is not null)
        {
            var acts = await _db.OrderActions.Where(a => a.WorkItemId == wi.WorkItemId).ToListAsync(ct);
            bool anyFailed = acts.Any(a => a.Status == "FAILED");
            if (!anyFailed)
            {
                wi.Status = "DONE";
            }
            else
            {
                wi.Attempts++;
                if (wi.Attempts < _maxRetries)
                {
                    wi.Status = "PENDING";   // 재큐잉 — 다음 라운드 재배차
                    _log.LogWarning("Run {Run}: wi={Wi} 실패 — 재시도 {N}/{Max}", mission.RunId, wi.WorkItemId, wi.Attempts, _maxRetries);
                }
                else
                {
                    wi.Status = "SKIPPED";
                    _log.LogWarning("Run {Run}: wi={Wi} 재시도 초과 — 스킵.", mission.RunId, wi.WorkItemId);
                    _db.Alarms.Add(new AlarmEntity
                    {
                        AlarmId = Guid.NewGuid(), AlarmCode = "INSPECTION_SKIPPED", RobotId = mission.RobotId,
                        MissionId = mission.MissionId,
                        Detail = JsonSerializer.Serialize(new
                        {
                            severity = "WARNING", title = "검사 작업 스킵(재시도 초과)",
                            workItemId = wi.WorkItemId, wi.Attempts
                        }),
                        RaisedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        await DispatchNextAsync(mission.RunId, ct);
    }

    private static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s)!;
}
