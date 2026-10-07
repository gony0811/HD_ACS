using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Domain;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Integration;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.App.Hubs;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<InspectionDispatcher> _log;
    private readonly double _standoffMm;
    private readonly double _workingDistanceMm;

    public InspectionDispatcher(AcsDbContext db, Vda5050MasterClient vda,
        IInspectionOrderingPolicy policy, IHubContext<MonitoringHub> hub,
        IConfiguration config, ILogger<InspectionDispatcher> log)
    {
        _db = db; _vda = vda; _policy = policy; _hub = hub; _log = log;
        _standoffMm = config.GetValue("Acs:Area:StandoffMm", 400.0);
        _workingDistanceMm = config.GetValue("Acs:Area:WorkingDistanceMm", _standoffMm);
        _stationStandoffM = config.GetValue("Acs:Area:StationStandoffM", 0.8);
        _allowedDevXy = config.GetValue("Acs:Dispatch:AllowedDevXy", 0.08);
        _allowedDevTheta = config.GetValue("Acs:Dispatch:AllowedDevTheta", 0.07);
        _robotStaleAfter = TimeSpan.FromSeconds(config.GetValue("Acs:Dispatch:RobotStateStaleSec", 10.0));
    }

    /// <summary>로봇 state 수신이 이 시간 이상 끊기면 연결 안 됨으로 본다(state 주기 ~2 s). 설정 Acs:Dispatch:RobotStateStaleSec.</summary>
    private readonly TimeSpan _robotStaleAfter;

    /// <summary>
    /// 명령(미션 시작·이어하기) 전 AMR 연결 확인 — connection ONLINE이고 최근 state를 받고 있어야 한다.
    /// 아니면 ROBOT_NOT_CONNECTED 알람을 기록·푸시하고 RobotNotConnectedException(명령 차단).
    /// 받을 로봇이 없는데 Order를 보내면 아무 일도 일어나지 않아 운영자가 "무반응"으로 오인한다(2026-10-02 현장).
    /// </summary>
    public async Task EnsureRobotConnectedAsync(string robotId, string command, CancellationToken ct)
    {
        var ctx = await _db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId, ct);
        var now = DateTimeOffset.UtcNow;
        string? reason =
            ctx is null ? "로봇 상태를 한 번도 받지 못함"
            : ctx.ConnectionState != "ONLINE" ? $"연결 상태 {ctx.ConnectionState ?? "알 수 없음"}"
            : ctx.ReportedAt is null ? "로봇 상태를 한 번도 받지 못함"
            : now - ctx.ReportedAt.Value > _robotStaleAfter
                ? $"마지막 상태 수신 {(int)(now - ctx.ReportedAt.Value).TotalSeconds}초 전"
            : null;
        if (reason is null) return;

        var title = $"AMR 미연결 — {command} 차단 ({robotId}: {reason})";
        await RaiseAlarmAsync("ROBOT_NOT_CONNECTED", "WARNING", title, robotId, null, new
        {
            command, robotId, reason, ctx?.ConnectionState, lastStateAt = ctx?.ReportedAt,
            action = "HD_AMR 실행·MQTT 연결을 확인한 뒤 다시 시도하세요.",
        }, ct);
        _log.LogWarning("{Command} 차단 — AMR {Robot} 미연결: {Reason}", command, robotId, reason);
        throw new RobotNotConnectedException($"AMR {robotId}이(가) 연결되어 있지 않습니다({reason}) — {command}을(를) 보내지 않았습니다. HD_AMR 연결을 확인하세요.");
    }

    /// <summary>알람 기록(alarm.alarm) + 실시간 푸시. code는 alarm.spec에 있어야 한다(AlarmSpecSeed).</summary>
    public async Task RaiseAlarmAsync(string code, string severity, string title, string? robotId, Guid? missionId,
        object detail, CancellationToken ct)
    {
        var a = new AlarmEntity
        {
            AlarmId = Guid.NewGuid(), AlarmCode = code, RobotId = robotId, MissionId = missionId,
            Detail = JsonSerializer.Serialize(new { severity, title, detail }), RaisedAt = DateTimeOffset.UtcNow,
        };
        _db.Alarms.Add(a);
        await _db.SaveChangesAsync(ct);
        await PushAlarmAsync(a, severity, title, ct);
    }


    private readonly double _stationStandoffM;
    private readonly double _allowedDevXy;
    private readonly double _allowedDevTheta;

    /// <summary>
    /// 시나리오 검사 대상 영역(작업 포함). 연결 영역이 있으면 그것만, 없으면 선창 전체(하위호환) [부분 검사 계획].
    /// 순서 = 층 → 연결 sort_order → 영역 sort_order. 큐 전개와 계획 정차점 조회가 공유한다.
    /// </summary>
    private async Task<(List<InspectionAreaEntity> Areas, int LinkedCount, int Total)> LoadScenarioAreasAsync(
        Guid scenarioId, string tankId, CancellationToken ct)
    {
        var areas = await _db.InspectionAreas.AsNoTracking()
            .Include(a => a.Tasks.OrderBy(t => t.Seq))
            .Where(a => a.TankId == tankId)
            .OrderBy(a => a.Level).ThenBy(a => a.SortOrder)
            .ToListAsync(ct);
        var total = areas.Count;
        var linked = await _db.ScenarioAreas.AsNoTracking()
            .Where(sa => sa.ScenarioId == scenarioId)
            .ToDictionaryAsync(sa => sa.AreaId, sa => sa.SortOrder, ct);
        if (linked.Count > 0)
            areas = areas.Where(a => linked.ContainsKey(a.AreaId))
                .OrderBy(a => a.Level).ThenBy(a => linked[a.AreaId]).ThenBy(a => a.SortOrder)
                .ToList();
        return (areas, linked.Count, total);
    }

    /// <summary>
    /// 영역의 정차점(도면 프레임) — 수동 오버라이드 우선, 없으면 영역 중심 + 내부향 법선 수평성분 × 이격
    /// (B/T는 수평성분 없음 → 중심 폴백). 방향 = station_theta ?? 면 facing_yaw. 큐 전개와 계획 정차점 조회의 단일 정본.
    /// </summary>
    public static (double X, double Y, double? Yaw) StationDrawingOf(
        InspectionAreaEntity area, WallEntity wall, double defaultStandoffM)
    {
        var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
        var (uc, vc) = AreaGeometry.Centroid(Json<double[][]>(area.Corners));
        var standoffM = area.StationStandoffM ?? defaultStandoffM;
        var d = AreaGeometry.StationDrawing(pose, uc, vc, Json<double[]>(wall.Normal), standoffM);
        return (area.StationX ?? d[0], area.StationY ?? d[1], area.StationTheta ?? wall.FacingYaw);
    }

    /// <summary>
    /// 시나리오의 계획 정차점 목록(도면 프레임) — 운영 화면이 로봇 현재 위치와 비교해 정차점을 사전 평가한다.
    /// 배차와 같은 식(StationDrawingOf)·같은 영역 선택을 쓰며, 도착 판정 허용 오차도 함께 돌려준다.
    /// 면 미등록 영역은 정차점을 산출할 수 없어 제외. 시나리오가 없으면 null.
    /// </summary>
    public async Task<PlannedStations?> GetPlannedStationsAsync(Guid scenarioId, CancellationToken ct)
    {
        var sc = await _db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.ScenarioId == scenarioId, ct);
        if (sc is null) return null;
        var (areas, _, _) = await LoadScenarioAreasAsync(scenarioId, sc.TankId, ct);
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == sc.TankId)
            .ToDictionaryAsync(w => w.WallCode, ct);
        var list = new List<PlannedStation>();
        foreach (var a in areas)
        {
            if (!walls.TryGetValue(a.WallCode, out var wall)) continue;
            var (x, y, yaw) = StationDrawingOf(a, wall, _stationStandoffM);
            var u = Json<double[]>(wall.UAxis);
            var n = Json<double[]>(wall.Normal);
            list.Add(new PlannedStation(a.AreaId, a.Name, a.WallCode, a.Level, $"{sc.TankId}-L{a.Level}",
                x, y, yaw, a.StationStandoffM ?? _stationStandoffM,
                a.StationX is not null || a.StationY is not null,
                Horizontal(u), Horizontal(n)));
        }
        return new PlannedStations(scenarioId, sc.TankId, _allowedDevXy, _allowedDevTheta, list);

        static double[]? Horizontal(double[] v)
        {
            var h = Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
            return h < 1e-9 ? null : new[] { v[0] / h, v[1] / h };
        }
    }

    // ── Phase 0: 선창 영역 → 작업 큐 전개 + 층별 미션 생성 ──────────────────────────
    /// <summary>
    /// run의 선창(tank)에 속한 모든 영역을 작업항목으로 전개한다("미검사 영역을 모두 큐에").
    /// 각 영역 = 정차 1곳(맵 프레임) + 그 영역 작업(용접선)의 startWeldInspection 액션들.
    /// 유효 T_W_D(맵버전 일치) 없는 층의 영역은 배차 불가라 제외(경고). 층마다 미션 1개 생성.
    /// </summary>
    public async Task BuildQueueAsync(ScenarioRunEntity run, string tankId, CancellationToken ct)
    {
        var (areas, linkedCount, total) = await LoadScenarioAreasAsync(run.ScenarioId, tankId, ct);
        if (linkedCount > 0)
            _log.LogInformation("Run {Run}: 부분 검사 계획 — 시나리오 대상 {N}개 영역 전개 (선창 전체 {Total}개 중).",
                run.RunId, areas.Count, total);
        else
            _log.LogInformation("Run {Run}: 시나리오에 연결된 영역 없음 — 선창 전체 {Total}개 영역 전개.", run.RunId, areas.Count);

        if (areas.Count == 0)
        {
            _log.LogWarning("Run {Run}: 선창 {Tank}에 전개할 영역이 없어 작업 큐가 비었습니다.", run.RunId, tankId);
            return;
        }

        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId)
            .ToDictionaryAsync(w => w.WallCode, ct);

        // 발행 전 자체 검증용 param_schema [VDA5050_INTERFACE_SPEC §8.2]
        var weldSchema = await WeldSchemaAsync(ct);

        var tWdByLevel = await LoadTransformsAsync(tankId, areas.Select(a => a.Level).Distinct(), run.RunId, ct);

        var seq = 0;
        var missionMaps = new HashSet<string>();
        int totalTasks = 0, excludedTasks = 0;   // 진행률 분모/제외 수 — 이 시점에 고정 [SAIGE §6.1/§6.4]
        foreach (var area in areas)
        {
            // 층 T_W_D 없음(또는 면 미등록) → 로봇 좌표 산출 불가로 큐에서 제외. 분모에 넣지 않고 별도 보고한다.
            if (!tWdByLevel.TryGetValue(area.Level, out var lv) || !walls.TryGetValue(area.WallCode, out var wall))
            {
                excludedTasks += area.Tasks.Count;
                continue;
            }
            totalTasks += area.Tasks.Count;

            var stop = BuildStop(run.RunId, tankId, area, wall, lv.T, weldSchema);
            _db.WorkItems.Add(new WorkItemEntity
            {
                WorkItemId = Guid.NewGuid(), RunId = run.RunId, AreaId = area.AreaId,
                MapId = lv.MapId, X = stop.X, Y = stop.Y, Theta = stop.Theta, Seq = area.SortOrder,
                Status = "PENDING", Actions = stop.Actions,
            });
            missionMaps.Add(lv.MapId);
        }

        run.TotalTasks = totalTasks;
        run.ExcludedTasks = excludedTasks;
        if (excludedTasks > 0)
            _log.LogWarning("Run {Run}: TASK {Excluded}건이 검사 큐에서 제외됨(층 T_W_D 부재 등) — 진행률 분모 {Total}건에 미포함.",
                run.RunId, excludedTasks, totalTasks);

        // 층별 미션 1개 (수동 층 전환 게이트·상태 재사용). 층 번호 순.
        foreach (var mapId in missionMaps.OrderBy(m => m))
            run.Missions.Add(new MissionEntity
            {
                MissionId = Guid.NewGuid(), RunId = run.RunId, Seq = seq++,
                MapId = mapId, RobotId = run.RobotId,
                OrderId = Guid.NewGuid().ToString(), State = nameof(MissionState.Created),
            });
    }

    /// <summary>
    /// 영역 1개 → 정차 1곳(맵 프레임 x/y/θ) + 그 영역 작업들의 startWeldInspection 액션 payload(json 배열).
    /// 큐 전개와 재개(resume) 시 잔여 정차 재계산이 공유하는 단일 정본 — 현재 계획·면·T_W_D 기준.
    /// </summary>
    private (double X, double Y, double? Theta, string Actions, int TaskCount) BuildStop(
        Guid runId, string tankId, InspectionAreaEntity area, WallEntity wall, DrawingTransform tWd, string? weldSchema)
    {
        var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
        // station_x/y/theta는 벽면·AREA와 같은 도면 프레임이다. 수동 오버라이드도
        // VDA nodePosition에 넣기 전에 반드시 T_W_D를 적용해야 한다.
        var (stationDx, stationDy, stationYaw) = StationDrawingOf(area, wall, _stationStandoffM);
        var (mx, my) = tWd.DrawingToMap(stationDx, stationDy);
        double? mTheta = stationYaw is double sy ? tWd.DrawingYawToMap(sy) : null;

        // 액션 payload(정차의 용접선들) 사전 구성 — 발행 시 actionId만 새로 발급.
        // 영역 1개 = anchorGroup 1개, seqInGroup = task.Seq [SPEC_AREA §7 / VDA5050_INTERFACE_SPEC §8.1]
        var anchorGroupId = $"{tankId}-L{area.Level}-{area.WallCode}-{area.Name}";
        var actionsJson = new JsonArray();
        foreach (var t in area.Tasks.OrderBy(x => x.Seq))
        {
            if (string.IsNullOrWhiteSpace(t.SectionDxfId) || string.IsNullOrWhiteSpace(t.ProfileId))
                throw new InvalidOperationException(
                    $"TASK {t.TaskId} 검사 참조 누락: sectionDxfId/profileId를 등록한 뒤 RUN을 시작하세요.");
            var startD = pose.LocalToDrawing(t.StartU, t.StartV);
            var endD = pose.LocalToDrawing(t.EndU, t.EndV);
            var d = new WeldDrawingData(tankId, area.Level, area.WallCode, startD, endD, t.StartU, t.StartV);
            var worldPos = WeldInspectionPayload.BuildPosition(tWd, d);
            var jobRef = $"JOB-{anchorGroupId}-{t.Seq}";
            var taskParams = new JsonObject
            {
                ["seamType"] = t.SeamType,
                ["sectionDxfId"] = t.SectionDxfId,
                ["inspectionProfileId"] = t.ProfileId,
                ["standoffMm"] = _standoffMm,
                ["workingDistanceMm"] = _workingDistanceMm,
                ["anchorGroupId"] = anchorGroupId,
                ["seqInGroup"] = t.Seq,
                // 용접선 1구간의 영구 식별자 — AMR이 모든 CAPTURE_REQ에 실어 SAIGE productId가 된다
                // [VDA §8.1 / SAIGE §2.5]. attempt는 발행 시점에 발급(PublishStopAsync).
                ["taskId"] = t.TaskId.ToString(),
            };

            // CROSS3/4 교차 가지 끝점 → AMR 면-로컬 mm·규약 순서 [VDA §8.5.1, N13]. ACS 저장은 전개도 프레임이라
            // 반드시 면 법선에서 AMR 프레임을 재구성해 변환한다(좌현·마구리 거울 흡수 — CrossGeometry).
            if (!string.IsNullOrWhiteSpace(t.Points))
            {
                var amrPts = CrossGeometry.BuildAmrPoints(t.SeamType, t.StartU, t.StartV,
                    Json<double[][]>(t.Points), pose.U, pose.V, Json<double[]>(wall.Normal));
                if (amrPts is not null)
                {
                    var arr = new JsonArray();
                    foreach (var p in amrPts) arr.Add(new JsonArray((JsonNode)p[0], p[1]));
                    taskParams["points"] = arr;
                }
            }

            var actionParams = WeldInspectionPayload.BuildActionParameters(jobRef, worldPos, taskParams);
            var violations = WeldInspectionPayload.ValidateSchema(weldSchema, actionParams);
            if (violations.Count > 0)
            {
                _log.LogWarning("Run {Run}: {Job} payload 스키마 위반 — run 시작 중단: {V}",
                    runId, jobRef, string.Join("; ", violations));
                throw new WeldPayloadSchemaException(violations);
            }

            actionsJson.Add(new JsonObject
            {
                ["actionType"] = "startWeldInspection",
                ["taskId"] = t.TaskId.ToString(),
                ["jobRef"] = jobRef,
                ["position"] = worldPos,
                ["params"] = taskParams.DeepClone(),
            });
        }

        return (mx, my, mTheta, actionsJson.ToJsonString(), area.Tasks.Count);
    }

    /// <summary>
    /// 재개(resume) 시 잔여 정차 재계산 — run 시작 때 저장한 정차점·용접선 좌표 스냅샷 대신 **현재 계획**(영역·작업·면)과
    /// **현재 T_W_D**로 다시 만든다. 계획을 고친 뒤 이어하기를 해도 옛 좌표로 움직이지 않게 하기 위함.
    /// 영역 삭제·작업 0건·면 미등록·층 T_W_D 없음이면 갈 곳이 없으므로 그 정차는 SKIPPED. 층이 바뀌어 미션이 없으면 추가한다.
    /// 진행률 분모(total_tasks)는 작업 수 증감만큼 보정. 저장은 호출자가 한다.
    /// </summary>
    public async Task<(int Refreshed, int Skipped)> RefreshOpenItemsAsync(
        ScenarioRunEntity run, IReadOnlyList<WorkItemEntity> items, CancellationToken ct)
    {
        if (items.Count == 0) return (0, 0);
        var areaIds = items.Select(w => w.AreaId).Distinct().ToList();
        var areas = await _db.InspectionAreas.AsNoTracking()
            .Include(a => a.Tasks)
            .Where(a => areaIds.Contains(a.AreaId))
            .ToDictionaryAsync(a => a.AreaId, ct);
        var tankId = await _db.Scenarios.AsNoTracking().Where(sc => sc.ScenarioId == run.ScenarioId)
                         .Select(sc => sc.TankId).FirstOrDefaultAsync(ct)
                     ?? areas.Values.FirstOrDefault()?.TankId ?? "";
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId)
            .ToDictionaryAsync(w => w.WallCode, ct);
        var tWdByLevel = await LoadTransformsAsync(tankId, areas.Values.Select(a => a.Level).Distinct(), run.RunId, ct);
        var weldSchema = await WeldSchemaAsync(ct);

        int refreshed = 0, skipped = 0;
        foreach (var wi in items)
        {
            int oldTasks = 0;
            try { oldTasks = JsonNode.Parse(wi.Actions ?? "[]")?.AsArray().Count ?? 0; } catch { /* 손상 — 0으로 */ }

            string? reason = null;
            (string MapId, DrawingTransform T) lv = default;
            WallEntity? wall = null;
            if (!areas.TryGetValue(wi.AreaId, out var area)) reason = "영역이 삭제됨";
            else if (area.Tasks.Count == 0) reason = "영역에 검사 작업이 없음";
            else if (!walls.TryGetValue(area.WallCode, out wall)) reason = $"면 {area.WallCode} 미등록";
            else if (!tWdByLevel.TryGetValue(area.Level, out lv)) reason = $"층 L{area.Level} 캘리브레이션(T_W_D) 없음";

            if (reason is not null)
            {
                wi.Status = "SKIPPED";
                wi.UpdatedAt = DateTimeOffset.UtcNow;
                skipped++;
                if (run.TotalTasks is int tt) run.TotalTasks = Math.Max(0, tt - oldTasks);
                _log.LogWarning("Run {Run}: 재개 — wi={Wi} 정차 재계산 불가({Reason}) → SKIPPED.", run.RunId, wi.WorkItemId, reason);
                continue;
            }

            var stop = BuildStop(run.RunId, tankId, area!, wall!, lv.T, weldSchema);
            _log.LogInformation("Run {Run}: 재개 — wi={Wi} 정차 재계산 ({OX:F2},{OY:F2}) → ({NX:F2},{NY:F2}) map={Map}",
                run.RunId, wi.WorkItemId, wi.X, wi.Y, stop.X, stop.Y, lv.MapId);
            wi.MapId = lv.MapId; wi.X = stop.X; wi.Y = stop.Y; wi.Theta = stop.Theta;
            wi.Seq = area!.SortOrder; wi.Actions = stop.Actions; wi.UpdatedAt = DateTimeOffset.UtcNow;
            if (run.TotalTasks is int t0) run.TotalTasks = t0 + stop.TaskCount - oldTasks;
            if (!run.Missions.Any(m => m.MapId == lv.MapId))
                run.Missions.Add(new MissionEntity
                {
                    MissionId = Guid.NewGuid(), RunId = run.RunId, Seq = run.Missions.Count,
                    MapId = lv.MapId, RobotId = run.RobotId,
                    OrderId = Guid.NewGuid().ToString(), State = nameof(MissionState.Created),
                });
            refreshed++;
        }
        return (refreshed, skipped);
    }

    /// <summary>층별 유효 T_W_D(맵 버전 일치). 맵 없음·무효 층은 빠진다(경고 로그).</summary>
    private async Task<Dictionary<int, (string MapId, DrawingTransform T)>> LoadTransformsAsync(
        string tankId, IEnumerable<int> levels, Guid runId, CancellationToken ct)
    {
        var tWdByLevel = new Dictionary<int, (string MapId, DrawingTransform T)>();
        foreach (var level in levels)
        {
            var mapId = $"{tankId}-L{level}";
            var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct);
            if (map is null) { _log.LogWarning("Run {Run}: map {Map} 없음 — 층 {Lv} 제외.", runId, mapId, level); continue; }
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
                _log.LogWarning("Run {Run}: 층 {Lv} T_W_D 무효 — 제외 ({Msg})", runId, level, ex.Message);
            }
        }
        return tWdByLevel;
    }

    private async Task<string?> WeldSchemaAsync(CancellationToken ct) =>
        (await _db.ActionCatalog.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ActionType == "startWeldInspection", ct))?.ParamSchema;

    // ── Phase 2: greedy 최근접 1건 배차 (유휴 시 호출) ────────────────────────────
    /// <summary>
    /// 현재 층의 미검사 작업 중 로봇 최근접 1건을 단일 정차 Order로 발행. 후보 없으면 층 완료 처리
    /// (다른 층 남으면 WAITING_FLOOR_TRANSFER, 전부 소진이면 run COMPLETED).
    /// </summary>
    public async Task DispatchNextAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.ScenarioRuns.Include(r => r.Missions).FirstAsync(r => r.RunId == runId, ct);
        // 중단/완료된 run은 후속 배차·완료 전이 금지 — abort 후 진행 중이던 정차가 완주되어도
        // ABORTED가 COMPLETED로 덮이지 않는다(정차 결과 기록은 완료 훅에서 이미 반영됨, resume 대비 보존).
        if (run.State is "ABORTED" or "COMPLETED")
        {
            _log.LogInformation("Run {Run}: 상태 {State} — 배차하지 않음.", runId, run.State);
            return;
        }
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
            await PushRunStateAsync(run.RunId, run.State, ct);
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
            NodePosition = new NodePosition
            {
                X = wi.X, Y = wi.Y, Theta = wi.Theta, MapId = wi.MapId,
                AllowedDeviationXY = _allowedDevXy, AllowedDeviationTheta = _allowedDevTheta,   // [SPEC §4.2]
            },
        };
        var order = new Vda5050Order { OrderId = orderId, OrderUpdateId = 0 };
        order.Nodes.Add(node);

        var actionsJson = wi.Actions is null ? new JsonArray() : JsonNode.Parse(wi.Actions)!.AsArray();

        // attempt 발급 [VDA §8.1 / SAIGE §2.5] — taskId별 **누적** 시도 번호(1부터). run과 무관하게 증가하므로
        // 재검사 run에서도 (taskId, attempt, captureSeq)가 유일하다. 근거 = 이 TASK에 지금까지 발행된 액션 수.
        var stopTaskIds = actionsJson
            .Select(a => Guid.TryParse(a!["taskId"]?.GetValue<string>(), out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();
        var issuedByTask = await _db.OrderActions.AsNoTracking()
            .Where(a => a.TaskId != null && stopTaskIds.Contains(a.TaskId.Value))
            .GroupBy(a => a.TaskId!.Value).Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        foreach (var an in actionsJson)
        {
            var o = an!.AsObject();
            var actionId = Guid.NewGuid();
            Guid? taskId = Guid.TryParse(o["taskId"]?.GetValue<string>(), out var tid) ? tid : null;
            int attempt = TaskAttempt.Next(taskId is Guid t0 && issuedByTask.TryGetValue(t0, out var n) ? n : 0);
            if (attempt == TaskAttempt.Max)
                _log.LogWarning("Run {Run}: task {Task} attempt가 상한 {Max}에 도달 — 이후 시도는 같은 번호로 발행됩니다.",
                    run.RunId, taskId, TaskAttempt.Max);
            var prm = o["params"]?.DeepClone() as JsonObject ?? new JsonObject();
            prm["attempt"] = attempt;
            var vda = new VdaAction
            {
                ActionType = o["actionType"]!.GetValue<string>(), ActionId = actionId.ToString(), BlockingType = "HARD",
            };
            vda.ActionParameters.Add(new ActionParameter { Key = "jobRef", Value = o["jobRef"]?.GetValue<string>() ?? "" });
            vda.ActionParameters.Add(new ActionParameter { Key = "position", Value = o["position"]?.DeepClone() });
            vda.ActionParameters.Add(new ActionParameter { Key = "params", Value = prm });
            node.Actions.Add(vda);

            _db.OrderActions.Add(new OrderActionEntity
            {
                ActionId = actionId, MissionId = mission.MissionId, WorkItemId = wi.WorkItemId,
                TaskId = taskId, NodeSequenceId = 0, ActionType = vda.ActionType, BlockingType = "HARD",
                Params = o["position"]?.ToJsonString(), Status = "WAITING", Attempts = attempt,
            });
        }

        // 미션의 order_node는 "현재 정차" 1건만 유지 — 이전 정차 노드 제거 (PK (mission,seq) 충돌 방지)
        var staleNodes = await _db.OrderNodes.Where(n => n.MissionId == mission.MissionId).ToListAsync(ct);
        _db.OrderNodes.RemoveRange(staleNodes);
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
        bool runStateChanged = run.State != "RUNNING";
        run.State = "RUNNING";

        // 저장을 발행보다 먼저 — 저장 실패 시 로봇에 미기록 Order가 나가는 실행-기록 불일치 방지
        await _db.SaveChangesAsync(ct);
        await _vda.PublishOrderAsync(new RobotRef(robot.RobotId, robot.Manufacturer, robot.SerialNumber), order, ct);
        if (runStateChanged) await PushRunStateAsync(run.RunId, run.State, ct);
        await PushWorkItemAsync(run.RunId, tracked, ct);
        _log.LogInformation("Run {Run}: 정차 배차 wi={Wi} @({X:F2},{Y:F2}) map={Map}", run.RunId, wi.WorkItemId, wi.X, wi.Y, wi.MapId);
    }

    /// <summary>work_item 상태 변화 단건 푸시 — 운영 UI 작업 현황 실시간 갱신용.</summary>
    private static (AlarmEntity Entity, string Severity, string Title) NewAlarm(
        string code, string severity, string title, MissionEntity mission, object detail) =>
        (new AlarmEntity
        {
            AlarmId = Guid.NewGuid(), AlarmCode = code, RobotId = mission.RobotId, MissionId = mission.MissionId,
            Detail = JsonSerializer.Serialize(new { severity, title, detail }), RaisedAt = DateTimeOffset.UtcNow,
        }, severity, title);

    /// <summary>알람 실시간 푸시("AlarmRaised") — 운영 화면 알람·이벤트 패널 표시용. 저장 후 호출.</summary>
    private Task PushAlarmAsync(AlarmEntity a, string severity, string title, CancellationToken ct) =>
        _hub is null ? Task.CompletedTask : _hub.Clients.All.SendAsync("AlarmRaised", new
        {
            a.AlarmId, a.AlarmCode, a.RobotId, a.MissionId, a.Detail,
            Severity = severity, Title = title, a.RaisedAt, ClearedAt = (DateTimeOffset?)null, ClearedBy = (string?)null,
        }, ct);

    /// <summary>Reason = 실패(재큐잉/스킵) 시 AMR 보고 사유 요약 — 운영 UI 이벤트 로그 표시용.</summary>
    private Task PushWorkItemAsync(Guid runId, WorkItemEntity wi, CancellationToken ct, string? reason = null) =>
        _hub is null ? Task.CompletedTask : _hub.Clients.All.SendAsync("WorkItemProgress", new
        {
            RunId = runId, wi.WorkItemId, wi.AreaId, wi.MapId, wi.Status, wi.Attempts, Reason = reason,
        }, ct);

    /// <summary>run 상태 변화 푸시(RUNNING/WAITING_FLOOR_TRANSFER/COMPLETED/ABORTED) — 운영 UI 현재 상태 표시용.</summary>
    public Task PushRunStateAsync(Guid runId, string state, CancellationToken ct) =>
        _hub is null ? Task.CompletedTask : _hub.Clients.All.SendAsync("RunState", new { RunId = runId, State = state }, ct);

    // ── 정차 완료/실패 처리 후 다음 배차 (RobotStateService 완료 훅에서 호출) ──────────
    /// <summary>현재 정차(mission.OrderId)의 액션 결과로 work_item을 DONE / FAILED(자동 재시도 없음 + INSPECTION_FAILED 알람) 처리하고 다음을 배차.</summary>
    public async Task HandleStopOutcomeAsync(MissionEntity mission, CancellationToken ct)
    {
        var wi = await _db.WorkItems
            .FirstOrDefaultAsync(w => w.OrderId == mission.OrderId && w.Status == "DISPATCHED", ct);
        if (wi is not null)
        {
            var acts = await _db.OrderActions.Where(a => a.WorkItemId == wi.WorkItemId).ToListAsync(ct);
            string? failureSummary = null;
            (AlarmEntity Entity, string Severity, string Title)? alarm = null;
            bool anyFailed = acts.Any(a => a.Status == "FAILED");
            if (!anyFailed)
            {
                wi.Status = "DONE";
            }
            else
            {
                // 자동 재시도 없음 — 재시도 여부는 작업자가 결정한다(2026-10-02 운영 결정).
                // 실패한 정차는 FAILED로 종결하고, 실패 항목(용접선별 사유)을 알람으로 기록·푸시한다.
                var failed = acts.Where(a => a.Status == "FAILED").ToList();
                var failureReasons = failed.Select(a => ResultDescription(a.Result))
                    .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
                failureSummary = failureReasons.Length == 0 ? "AMR 실패 사유 미제공" : string.Join(" | ", failureReasons);
                wi.Attempts++;
                wi.Status = "FAILED";

                var areaName = await _db.InspectionAreas.AsNoTracking().Where(x => x.AreaId == wi.AreaId)
                    .Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "영역";
                var taskIds = failed.Where(a => a.TaskId is not null).Select(a => a.TaskId!.Value).Distinct().ToList();
                var taskNames = await _db.AreaTasks.AsNoTracking().Where(t => taskIds.Contains(t.TaskId))
                    .ToDictionaryAsync(t => t.TaskId, t => t.Name ?? $"라인 {t.Seq}", ct);
                var items = failed.Select(a => new
                {
                    a.TaskId,
                    taskName = a.TaskId is { } tid && taskNames.TryGetValue(tid, out var n) ? n : null,
                    a.ActionId,
                    reason = ResultDescription(a.Result) ?? "AMR 실패 사유 미제공",
                }).ToList();
                _log.LogWarning("Run {Run}: wi={Wi}({Area}) 검사 실패 {N}건 — 자동 재시도 없이 FAILED·알람. 원인={Reason}",
                    mission.RunId, wi.WorkItemId, areaName, items.Count, failureSummary);

                var first = failureReasons.FirstOrDefault() ?? "사유 미제공";
                if (first.Length > 80) first = first[..80] + "…";
                alarm = NewAlarm("INSPECTION_FAILED", "WARNING", $"검사 실패: {areaName} — {first}", mission, new
                {
                    workItemId = wi.WorkItemId, areaId = wi.AreaId, areaName, wi.MapId, attempt = wi.Attempts,
                    failedCount = items.Count, items,
                    action = "자동 재시도하지 않음 — 원인 확인·조치 후 작업자가 재실행 여부를 결정하세요.",
                });
                _db.Alarms.Add(alarm.Value.Entity);
            }
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await PushWorkItemAsync(mission.RunId, wi, ct, failureSummary);   // DONE / FAILED
            if (alarm is { } al) await PushAlarmAsync(al.Entity, al.Severity, al.Title, ct);
        }
        await DispatchNextAsync(mission.RunId, ct);
    }

    // ── Order 거부(orderValidationError) 자동 처리 [VDA 사양서 §4.5.2, N11 확정] ──────
    /// <summary>
    /// AMR이 Order를 거부(폐기 + errors 보고)하면 배차만 된 정차가 DISPATCHED로 정체한다.
    /// 거부 errorDescription의 orderId를 현재 DISPATCHED work_item과 대조해 실패로 집계(재시도→스킵 정책)하고
    /// ORDER_REJECTED 알람을 기록한다. orderId 불일치/처리 완료면 false — 반복 state 수신에 멱등.
    /// </summary>
    public async Task<bool> HandleOrderRejectedAsync(string robotId, string? errorDescription, CancellationToken ct)
    {
        var run = await _db.ScenarioRuns.Include(r => r.Missions)
            .Where(r => r.RobotId == robotId && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
            .FirstOrDefaultAsync(ct);
        if (run is null) return false;

        var wi = await _db.WorkItems
            .FirstOrDefaultAsync(w => w.RunId == run.RunId && w.Status == "DISPATCHED", ct);
        if (wi?.OrderId is not { } rejectedOrderId) return false;

        // N11 계약: description에 거부된 orderId 명시 — 대조 실패면 이번 정차의 거부가 아님(구 오류 잔존 등)
        if (errorDescription is null || !errorDescription.Contains(rejectedOrderId, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Run {Run}: orderValidationError 수신했으나 description에 현재 orderId({Order}) 없음 — 무시. desc={Desc}",
                run.RunId, rejectedOrderId, errorDescription);
            return false;
        }

        // 미실행 액션들을 FAILED로 종결(거부=폐기, 실행 없음) 후 기존 실패 정책(재시도→스킵) 경로 재사용
        var acts = await _db.OrderActions.Where(a => a.WorkItemId == wi.WorkItemId && a.Status != "FINISHED").ToListAsync(ct);
        foreach (var a in acts)
        {
            a.Status = "FAILED";
            a.Result = JsonSerializer.Serialize(new { ActionStatus = "FAILED", ResultDescription = "orderValidationError(Order 거부)" });
        }
        var rejected = new AlarmEntity
        {
            AlarmId = Guid.NewGuid(), AlarmCode = "ORDER_REJECTED", RobotId = robotId,
            Detail = JsonSerializer.Serialize(new { severity = "WARNING", title = "Order 거부(AMR 검증 실패)", orderId = rejectedOrderId, description = errorDescription }),
            RaisedAt = DateTimeOffset.UtcNow,
        };
        _db.Alarms.Add(rejected);
        await _db.SaveChangesAsync(ct);
        await PushAlarmAsync(rejected, "WARNING", "Order 거부(AMR 검증 실패)", ct);
        _log.LogWarning("Run {Run}: Order {Order} 거부(orderValidationError) — 실패 정책 적용. desc={Desc}",
            run.RunId, rejectedOrderId, errorDescription);

        var mission = run.Missions.FirstOrDefault(m => m.OrderId == rejectedOrderId);
        if (mission is not null) await HandleStopOutcomeAsync(mission, ct);
        return true;
    }

    private static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s)!;

    private static string? ResultDescription(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty("ResultDescription", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>GET /api/scenarios/{id}/area-stations 응답 — 계획 정차점(도면 프레임)과 도착 허용 오차.</summary>
public sealed record PlannedStations(Guid ScenarioId, string TankId, double AllowedDevXy, double AllowedDevTheta,
    IReadOnlyList<PlannedStation> Stations);

/// <summary>영역 1개의 계획 정차점. Yaw=도면 yaw[rad](B/T 등 미지정이면 null), Manual=수동 오버라이드,
/// WallU/WallNormal=면 u축·내부향 법선의 수평 단위벡터(수평성분 없으면 null) — 오차를 "벽 따라/벽까지"로 분해하는 데 쓴다.</summary>
public sealed record PlannedStation(Guid AreaId, string AreaName, string WallCode, int Level, string MapId,
    double X, double Y, double? Yaw, double StandoffM, bool Manual, double[]? WallU, double[]? WallNormal);

/// <summary>AMR 미연결로 명령을 보내지 않음 — API는 409.</summary>
public sealed class RobotNotConnectedException(string message) : Exception(message);
