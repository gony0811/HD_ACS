using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Abstractions;
using HD.Acs.Core.Domain;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
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
    private readonly ReachSimService? _reach;   // 재검사 전 정차 최적화(없으면 저장된 정차 그대로)

    public MissionService(AcsDbContext db, Vda5050MasterClient vda, InspectionDispatcher dispatcher, ILogger<MissionService> log,
        ReachSimService? reach = null)
    {
        _db = db; _vda = vda; _dispatcher = dispatcher; _log = log; _reach = reach;
    }

    /// <summary>
    /// 시나리오 → Run 생성. 선창 영역을 작업 큐로 전개(층별 미션 포함)한 뒤 첫 정차를 greedy 최근접 배차.
    /// 검사 순서는 고정 시퀀스가 아니라 "현재 층 미검사 중 로봇 최근접"으로 동적 결정된다.
    /// </summary>
    public async Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default)
        => (await StartRunCoreAsync(scenarioId, robotId, null, false, ct)).RunId;

    /// <summary>
    /// 리페어 run — 지정한 용접선(TASK)만 검사한다(1개 이상). 영역 검사에서 실패한 용접선을 골라 다시 돌릴 때 쓴다.
    /// 일반 run과 같은 큐·배차·재시도·기록 경로를 타며, 정차는 그 용접선이 속한 영역의 정차점 그대로다.
    /// 시나리오는 run 기록의 소속 표시용 — 지정이 없으면 그 선창의 가장 최근 run 시나리오, 그것도 없으면 선창의 첫 시나리오.
    /// <paramref name="optimize"/>(기본) — 영역 검사 때의 정차는 영역의 모든 용접선을 보고 고른 자리라 실패한 용접선에는
    /// 거리·리프트가 모자랐을 수 있다. 시작 전에 **지정한 용접선만** 놓고 정차점 최적화를 돌려 그 정차·리프트로 검사한다
    /// (이 run의 정차에만 적용 — 영역에 저장된 정차는 바꾸지 않는다).
    /// </summary>
    public async Task<RepairRunStarted> StartRepairRunAsync(string robotId, IReadOnlyCollection<Guid> taskIds, Guid? scenarioId = null,
        CancellationToken ct = default, bool optimize = true)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) throw new RepairRequestException("검사할 용접선을 1개 이상 지정하세요.");

        var found = await (from t in _db.AreaTasks.AsNoTracking()
                           join a in _db.InspectionAreas.AsNoTracking() on t.AreaId equals a.AreaId
                           where ids.Contains(t.TaskId)
                           select new { t.TaskId, a.TankId }).ToListAsync(ct);
        var missing = ids.Except(found.Select(f => f.TaskId)).ToList();
        if (missing.Count > 0)
            throw new RepairRequestException($"등록되지 않은 용접선 {missing.Count}개: {string.Join(", ", missing.Take(5))}");
        var tanks = found.Select(f => f.TankId).Distinct().ToList();
        if (tanks.Count != 1)
            throw new RepairRequestException("용접선이 서로 다른 선창에 속해 있습니다 — 선창 하나의 용접선만 지정하세요.");
        var tankId = tanks[0];

        Guid? sid = scenarioId;
        if (sid is { } given)
        {
            var tank = await _db.Scenarios.AsNoTracking().Where(s => s.ScenarioId == given)
                .Select(s => s.TankId).FirstOrDefaultAsync(ct);
            if (tank is null) throw new RepairRequestException($"시나리오 '{given}' 없음");
            if (tank != tankId) throw new RepairRequestException($"시나리오의 선창({tank})과 용접선의 선창({tankId})이 다릅니다.");
        }
        else
        {
            sid = await (from r in _db.ScenarioRuns.AsNoTracking()
                         join s in _db.Scenarios.AsNoTracking() on r.ScenarioId equals s.ScenarioId
                         where s.TankId == tankId
                         orderby r.StartedAt descending
                         select (Guid?)s.ScenarioId).FirstOrDefaultAsync(ct)
                  ?? await _db.Scenarios.AsNoTracking().Where(s => s.TankId == tankId)
                      .OrderBy(s => s.Name).Select(s => (Guid?)s.ScenarioId).FirstOrDefaultAsync(ct);
            if (sid is null) throw new RepairRequestException($"선창 {tankId}에 시나리오가 없습니다 — 계획 ▸ 시나리오에서 먼저 만드세요.");
        }

        return await StartRunCoreAsync(sid.Value, robotId, ids, optimize, ct);
    }

    /// <summary>리페어 run의 정차 최적화 — 실패해도 재검사는 저장된 정차로 진행한다(빈 목록).</summary>
    private async Task<IReadOnlyList<RepairStop>> RepairStopsAsync(string tankId, HashSet<Guid> taskIds, CancellationToken ct,
        IReadOnlySet<Guid>? onlyAreas = null)
    {
        if (_reach is null) return Array.Empty<RepairStop>();
        try { return await _reach.RepairStopsAsync(tankId, taskIds, ct, onlyAreas); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "재검사 정차 최적화 실패 — 저장된 정차로 진행합니다.");
            return Array.Empty<RepairStop>();
        }
    }

    private static Dictionary<Guid, InspectionDispatcher.StopOverride> Overrides(IEnumerable<RepairStop> stops) =>
        stops.Where(s => s.Applied).ToDictionary(s => s.AreaId, s => new InspectionDispatcher.StopOverride(s.X, s.Y, s.Yaw, s.LiftMm));

    private async Task<RepairRunStarted> StartRunCoreAsync(Guid scenarioId, string robotId, List<Guid>? repairTaskIds, bool optimize,
        CancellationToken ct)
    {
        var what = repairTaskIds is null ? "미션 시작" : "용접선 재검사";
        // 활성 run 가드 — 같은 로봇에 RUNNING/WAITING_FLOOR_TRANSFER run이 있으면 새 시작 거부 (유령 run 누적 방지)
        var active = await _db.ScenarioRuns.AsNoTracking()
            .Where(r => r.RobotId == robotId && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
            .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(ct);
        if (active is not null)
            throw new RunConflictException(
                $"로봇 {robotId}에 진행 중인 run({active})이 있습니다 — 이어하기(resume) 또는 중단(abort) 후 시작하세요.");

        // AMR 미연결이면 run을 만들지 않고 알람 — 받을 로봇 없이 Order가 나가 "무반응"이 되는 것을 막는다
        await _dispatcher.EnsureRobotConnectedAsync(robotId, what, ct);
        _dispatcher.EnsureNoDeviceFault(robotId, what);   // 장치 HEAVY 알람 중 Order 금지 [장치통신 HEAVY알람 §5]

        var scenario = await _db.Scenarios.AsNoTracking().FirstAsync(s => s.ScenarioId == scenarioId, ct);

        var run = new ScenarioRunEntity
        {
            RunId = Guid.NewGuid(),
            ScenarioId = scenario.ScenarioId,
            ScenarioVer = scenario.Version,
            RobotId = robotId,
            State = "RUNNING",
            StartedAt = DateTimeOffset.UtcNow,
            TaskIds = repairTaskIds is null ? null : JsonSerializer.Serialize(repairTaskIds),
        };
        // 재검사 — 지정한 용접선만 놓고 정차·리프트를 다시 고른다(가드를 모두 통과한 뒤 계산: 수 초 걸린다)
        IReadOnlyList<RepairStop> stops = repairTaskIds is not null && optimize
            ? await RepairStopsAsync(scenario.TankId, repairTaskIds.ToHashSet(), ct) : Array.Empty<RepairStop>();
        _db.ScenarioRuns.Add(run);
        await _dispatcher.BuildQueueAsync(run, scenario.TankId, ct, Overrides(stops));   // 영역→작업 큐 + 층별 미션
        if (repairTaskIds is not null && run.TotalTasks is null or 0)
        {
            // 갈 수 있는 정차가 하나도 없다(층 캘리브레이션 없음·면 미등록) — 빈 run을 남기지 않는다
            _db.ChangeTracker.Clear();
            throw new RepairRequestException(
                "지정한 용접선을 검사 큐에 넣을 수 없습니다 — 그 층의 맵 캘리브레이션(T_W_D)과 면 등록을 확인하세요.");
        }
        if (repairTaskIds is not null)
            _db.AuditLogs.Add(new AuditLogEntity
            {
                UserId = "", Action = "REPAIR_RUN_START",
                Target = $"{robotId} run {run.RunId} 용접선 {repairTaskIds.Count}개"
                         + (stops.Count == 0 ? (optimize ? " · 정차 최적화 없음" : " · 정차 최적화 끔")
                             : " · " + string.Join(" / ", stops.Select(s =>
                                 $"{s.AreaName}: {s.Note} → ({s.X:F2},{s.Y:F2}) 리프트 {s.LiftMm:0} · 간섭 없이 {s.ClearBefore}→{s.ClearAfter}/{s.Welds}"))),
            });
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchNextAsync(run.RunId, ct, commanded: true);   // 첫 정차 배차(로봇 층 일치 시, 불일치면 FLOOR_MISMATCH 알람)
        return new RepairRunStarted(run.RunId, stops);
    }

    /// <summary>
    /// run 중단 — 상태만 ABORTED로 전환하고 후속 배차를 멈춘다. 진행 중 Order는 회수하지 않음
    /// (cancelOrder 미사용 [VDA5050_INTERFACE_SPEC §4.5.3] — 로봇은 현재 정차를 완주). 즉시 정지는 비상정지 사용.
    /// 예외: 통신 단절 PAUSE 중 운영자 중단은 CommLossService.AbortRunAsync가 이 메서드 뒤에 cancelOrder를 덧붙인다.
    /// </summary>
    public async Task AbortRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ScenarioRuns.Include(r => r.Missions).FirstOrDefaultAsync(r => r.RunId == runId, ct)
                  ?? throw new KeyNotFoundException($"run '{runId}' 없음");
        if (run.State is "COMPLETED" or "ABORTED")
            throw new RunStateException($"run이 이미 {run.State} 상태입니다.");

        run.State = "ABORTED";
        run.EndedAt = DateTimeOffset.UtcNow;
        foreach (var m in run.Missions.Where(m => m.State != nameof(MissionState.Completed)
                                               && m.State != nameof(MissionState.Aborted)))
        {
            m.State = nameof(MissionState.Aborted);
            m.EndedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        await _dispatcher.PushRunStateAsync(runId, run.State, ct);
        _log.LogInformation("Run {Run} 중단(ABORTED) — 완료 이력은 보존, resume으로 이어하기 가능.", runId);
    }

    /// <summary>
    /// run 재개 — DONE/SKIPPED는 보존(재검사 없음), 중단 시점에 종결 못 한 DISPATCHED는 PENDING으로
    /// 리셋해 재검사, 남은 PENDING만 greedy 재배차. [INSPECTION_SCENARIO §3.1 재개 규정]
    /// </summary>
    public async Task<ResumeSummary> ResumeRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ScenarioRuns.Include(r => r.Missions).FirstOrDefaultAsync(r => r.RunId == runId, ct)
                  ?? throw new KeyNotFoundException($"run '{runId}' 없음");
        if (run.State == "COMPLETED")
            throw new RunStateException("완료된 run은 재개할 수 없습니다 — 새 run으로 시작하세요(전체 재검사 사이클).");

        // 가장 최근 run만 재개 — 그 뒤에 시작한 run이 있으면 계획이 바뀌었을 가능성이 크고,
        // 오래된 run을 되살리면 운영자가 의도하지 않은 작업이 재개된다(2026-10-02 현장: 전날 중단 run이 재개됨).
        var newer = await _db.ScenarioRuns.AsNoTracking()
            .AnyAsync(r => r.RobotId == run.RobotId && r.RunId != runId && r.StartedAt > run.StartedAt, ct);
        if (newer)
            throw new RunStateException(
                "이 run 이후에 시작한 run이 있어 재개할 수 없습니다 — 가장 최근 run만 이어할 수 있습니다. '미션 시작'으로 새로 시작하세요.");

        var otherActive = await _db.ScenarioRuns.AsNoTracking()
            .Where(r => r.RobotId == run.RobotId && r.RunId != runId
                        && (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"))
            .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(ct);
        if (otherActive is not null)
            throw new RunConflictException(
                $"로봇 {run.RobotId}에 다른 진행 중 run({otherActive})이 있습니다 — 먼저 중단하세요.");

        await _dispatcher.EnsureRobotConnectedAsync(run.RobotId, "이어하기", ct);
        _dispatcher.EnsureNoDeviceFault(run.RobotId, "이어하기");   // 장치 HEAVY 알람 중 Order 금지 [장치통신 HEAVY알람 §5]

        // 배차만 되고 종결 못 한 정차 복구 — attempts는 유지(실패 정책 연속성)
        var open = await _db.WorkItems
            .Where(w => w.RunId == runId && (w.Status == "DISPATCHED" || w.Status == "PENDING")).ToListAsync(ct);
        if (open.Count == 0)
            throw new RunStateException("재개할 작업이 없습니다(전부 종결됨).");
        int stale = 0;
        foreach (var w in open.Where(w => w.Status == "DISPATCHED"))
        {
            w.Status = "PENDING"; w.OrderId = null; w.UpdatedAt = DateTimeOffset.UtcNow; stale++;
        }

        // 남은 정차의 좌표·액션을 현재 계획/캘리브레이션으로 다시 계산 — 시작 시점 스냅샷으로 움직이지 않는다
        // 리페어 run은 남은 정차를 다시 계산할 때도 그 용접선만 놓고 정차를 고른다
        Dictionary<Guid, InspectionDispatcher.StopOverride>? overrides = null;
        if (InspectionDispatcher.RepairFilter(run) is { } only)
        {
            var tank = await _db.Scenarios.AsNoTracking().Where(s => s.ScenarioId == run.ScenarioId)
                .Select(s => s.TankId).FirstOrDefaultAsync(ct);
            if (tank is not null)
                overrides = Overrides(await RepairStopsAsync(tank, only, ct, open.Select(w => w.AreaId).ToHashSet()));
        }
        var (refreshed, skipped) = await _dispatcher.RefreshOpenItemsAsync(run, open, ct, overrides);

        run.State = "RUNNING";
        run.EndedAt = null;
        foreach (var m in run.Missions.Where(m => m.State == nameof(MissionState.Aborted)))
            m.EndedAt = null;   // 상태는 재배차 시 디스패처가 Released/Running으로 갱신
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Run {Run} 재개 — DISPATCHED 리셋 {Stale}건, 현재 계획으로 재계산 {Refreshed}건, 재계산 불가 스킵 {Skipped}건.",
            runId, stale, refreshed, skipped);
        await _dispatcher.DispatchNextAsync(runId, ct, commanded: true);
        return new ResumeSummary(refreshed, skipped);
    }

    /// <summary>로봇의 가장 최근 run이 재개 가능(미완료 + 미종결 작업 보유)하면 그 run, 아니면 null.
    /// 더 오래된 run은 후보로 삼지 않는다 — 재개는 직전 작업을 이어갈 때만 쓴다.</summary>
    public async Task<object?> FindResumableRunAsync(string robotId, CancellationToken ct = default)
    {
        var r = await _db.ScenarioRuns.AsNoTracking()
            .Where(x => x.RobotId == robotId)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => new { x.RunId, x.ScenarioId, x.State, x.StartedAt })
            .FirstOrDefaultAsync(ct);
        if (r is null || r.State == "COMPLETED") return null;
        var counts = await _db.WorkItems.Where(w => w.RunId == r.RunId)
            .GroupBy(w => w.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int Of(string s) => counts.FirstOrDefault(c => c.Key == s)?.N ?? 0;
        int open = Of("PENDING") + Of("DISPATCHED");
        if (open == 0) return null;
        return new { r.RunId, r.ScenarioId, r.State, r.StartedAt,
                     Pending = open, Done = Of("DONE"), Skipped = Of("SKIPPED") };
    }

    /// <summary>
    /// 다음 CREATED 미션 릴리즈. 층 검증 게이트: 로봇 보고 mapId == 미션 mapId 일 때만.
    /// 불일치 시 Run을 WAITING_FLOOR_TRANSFER로 두고 작업자 수동 절차를 기다린다 [Q9].
    /// </summary>
    /// <param name="commanded">운영자가 누른 '다음 층 릴리스'인가(기본). 재접속 자동 재시도는 false — 층 불일치 알람을 반복해서 내지 않는다.</param>
    public async Task<bool> TryReleaseNextMissionAsync(Guid runId, CancellationToken ct = default, bool commanded = true)
    {
        // 영역 기반 run(greedy 배차)은 디스패처가 층 게이트·배차를 담당 — 수동 층 변경 후 재개도 이 경로.
        if (await _db.WorkItems.AnyAsync(w => w.RunId == runId, ct))
        {
            await _dispatcher.DispatchNextAsync(runId, ct, commanded);
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
            if (commanded)
                await _dispatcher.RaiseAlarmAsync("FLOOR_MISMATCH", "WARNING",
                    $"AMR 층 불일치 — AMR은 {ctx?.ReportedMapId ?? "(미확인)"}, 작업은 {next.MapId} — 작업 지시(Order) 보류",
                    run.RobotId, next.MissionId, new { runId, robotMapId = ctx?.ReportedMapId, missionMapId = next.MapId }, ct);
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
        double[]? weldLevelZ = null;
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
            // seam z를 층 바닥 기준으로 보내기 위한 level_z [VDA §10 N17]
            var tankId = await _db.Scenarios.AsNoTracking().Where(s => s.ScenarioId == run.ScenarioId)
                .Select(s => s.TankId).FirstOrDefaultAsync(ct);
            var levelZJson = await _db.TankGeometries.AsNoTracking().Where(g => g.TankId == tankId)
                .Select(g => g.LevelZ).FirstOrDefaultAsync(ct);
            weldLevelZ = levelZJson is null ? null : JsonSerializer.Deserialize<double[]>(levelZJson);
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
                    // 도면 좌표 → 유효 T_W_D 적용한 월드 position + 발행 직전 스키마 검증 [WP-3 §4.2]
                    var d = ParseWeldDrawing(t.Position);
                    var worldPos = WeldInspectionPayload.BuildPosition(weldTransform!, d,
                        WeldInspectionPayload.FloorZ(weldLevelZ, d.Level));
                    var paramsNode = t.Params is null ? null : JsonNode.Parse(t.Params);
                    var actionParams = WeldInspectionPayload.BuildActionParameters(t.JobRef ?? "", worldPos, paramsNode);

                    var violations = WeldInspectionPayload.ValidateSchema(weldSchema, actionParams);
                    if (violations.Count > 0)
                    {
                        _log.LogWarning("릴리즈 중단 — mission {Mission} / {Job} payload 스키마 위반: {V}",
                            mission.MissionId, t.JobRef, string.Join("; ", violations));
                        throw new WeldPayloadSchemaException(violations);
                    }

                    parameters["jobRef"] = t.JobRef;
                    parameters["position"] = actionParams["position"]!.DeepClone();
                    parameters["params"] = actionParams["params"]!.DeepClone();
                    historyPos = worldPos.ToJsonString();   // 실제 발행한 월드 좌표 보존
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

    /// <summary>작업자 수동 층(존) 지정 [Q9] — AMR이 자체 initPose 후 보고하는 mapId를 검증한다.</summary>
    public async Task ManualZoneChangeAsync(string robotId, string mapId, string userId,
        CancellationToken ct = default)
    {
        var ctx = await _db.RobotContexts.FindAsync(new object[] { robotId }, ct)
                  ?? _db.RobotContexts.Add(new RobotContextEntity { RobotId = robotId }).Entity;
        ctx.ManualMapId = mapId;
        ctx.ManualUpdatedBy = userId;
        ctx.ManualUpdatedAt = DateTimeOffset.UtcNow;

        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId, Action = "MANUAL_ZONE_CHANGE", Target = robotId,
            Detail = JsonSerializer.Serialize(new { mapId })
        });
        await _db.SaveChangesAsync(ct);
        // AMR 자체 initPose 이후 state의 agvPosition.mapId 확인 → 릴리스 게이트 통과
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
            Vec(pos, "seamEndDrawing"),
            pos["u"]?.GetValue<double>() ?? 0.0,   // dormant seam 경로: Position에 u,v 없으면 0 폴백
            pos["v"]?.GetValue<double>() ?? 0.0);
    }

    private static string? FindNearestNode(Core.Graph.MapGraph graph, RobotContextEntity ctx)
    {
        if (ctx.ReportedX is not double rx || ctx.ReportedY is not double ry) return null;
        return graph.Nodes.Values
            .OrderBy(n => Math.Pow(n.X - rx, 2) + Math.Pow(n.Y - ry, 2))
            .FirstOrDefault()?.NodeId;
    }
}

/// <summary>run 시작/재개 충돌(동일 로봇 활성 run 존재) — 409로 매핑.</summary>
public sealed class RunConflictException(string message) : Exception(message);

/// <summary>run 상태상 불가한 요청(완료 run 재개 등) — 400으로 매핑.</summary>
public sealed class RunStateException(string message) : Exception(message);

/// <summary>리페어 run 요청이 잘못됨(용접선 없음·선창 혼재·시나리오 없음·큐에 넣을 수 없음) — 400.</summary>
public sealed class RepairRequestException(string message) : Exception(message);

/// <summary>리페어 run 시작 결과 — run + 재검사 전 정차 최적화 결과(영역마다 1건, 최적화를 안 했으면 빈 목록).</summary>
public sealed record RepairRunStarted(Guid RunId, IReadOnlyList<RepairStop> Stops);

/// <summary>재개 결과 — 현재 계획으로 다시 계산한 정차 수 / 재계산 불가로 건너뛴 정차 수.</summary>
public sealed record ResumeSummary(int Refreshed, int Skipped);
