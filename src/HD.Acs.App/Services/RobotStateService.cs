using HD.Acs.App.Hubs;
using HD.Acs.Core.Domain;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// VDA 5050 state/connection 수신 처리 — robot-is-truth [ADR-002].
/// 로봇 보고를 DB에 반영하고 상태머신 트리거를 발화, SignalR로 전파한다.
/// </summary>
public sealed class RobotStateService
{
    private readonly AcsDbContext _db;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly InspectionDispatcher _dispatcher;
    private readonly ILogger<RobotStateService> _log;

    public RobotStateService(AcsDbContext db, IHubContext<MonitoringHub> hub, InspectionDispatcher dispatcher, ILogger<RobotStateService> log)
    {
        _db = db; _hub = hub; _dispatcher = dispatcher; _log = log;
    }

    public async Task HandleStateAsync(RobotRef robot, Vda5050State state, CancellationToken ct = default)
    {
        // ── 1. robot_context 갱신 (보고값)
        var ctx = await _db.RobotContexts.FindAsync(new object[] { robot.RobotId }, ct)
                  ?? _db.RobotContexts.Add(new RobotContextEntity { RobotId = robot.RobotId }).Entity;
        ctx.ReportedMapId = state.AgvPosition?.MapId;
        ctx.ReportedX = state.AgvPosition?.X;
        ctx.ReportedY = state.AgvPosition?.Y;
        ctx.ReportedTheta = state.AgvPosition?.Theta;
        ctx.BatteryPct = state.BatteryState?.BatteryCharge;
        ctx.ReportedAt = DateTimeOffset.UtcNow;

        // ── 2. 활성 미션 대조 (orderId 기준)
        var mission = await _db.Missions
            .FirstOrDefaultAsync(m => m.OrderId == state.OrderId && m.RobotId == robot.RobotId, ct);

        if (mission != null)
        {
            // lastNodeId → order_node 진행 반영
            await _db.OrderNodes
                .Where(n => n.MissionId == mission.MissionId && n.SequenceId <= state.LastNodeSequenceId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, "PASSED"), ct);

            // actionStates → order_action 대조 (actionId 키)
            foreach (var a in state.ActionStates)
            {
                if (!Guid.TryParse(a.ActionId, out var actionId)) continue;
                var oa = await _db.OrderActions.FindAsync(new object[] { actionId }, ct);
                if (oa == null || oa.Status == a.ActionStatus) continue;
                oa.Status = a.ActionStatus;
                if (a.ActionStatus is "FINISHED" or "FAILED")
                    await RecordInspectionResultAsync(mission, oa, a, robot, ct);
                // TODO: FAILED 시 정책 엔진(시나리오 policy jsonb) 적용 → 재시도 Order(orderUpdateId+1) / 스킵 / 알람
            }

            await FireAsync(mission, MissionTrigger.RobotProgress, ct);

            // 전 액션 종결 + 마지막 노드 도달 → 현재 정차 완료
            var remaining = await _db.OrderActions.CountAsync(
                x => x.MissionId == mission.MissionId && x.Status != "FINISHED" && x.Status != "FAILED", ct);
            var lastSeq = await _db.OrderNodes.Where(n => n.MissionId == mission.MissionId)
                .MaxAsync(n => (int?)n.SequenceId, ct) ?? 0;
            if (remaining == 0 && state.LastNodeSequenceId >= lastSeq)
            {
                // 영역 기반(greedy): 이번 정차 완료를 work_item에 반영하고 다음 최근접 배차. 아니면 legacy 완료.
                bool areaModel = await _db.WorkItems.AnyAsync(w => w.RunId == mission.RunId, ct);
                if (areaModel)
                {
                    await _db.SaveChangesAsync(ct);   // order_action 상태 확정 후 결과 집계
                    await _dispatcher.HandleStopOutcomeAsync(mission, ct);
                }
                else
                {
                    await FireAsync(mission, MissionTrigger.RobotCompleted, ct);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        await _hub.Clients.All.SendAsync("RobotState", new
        {
            robot.RobotId, ctx.ReportedMapId, ctx.ReportedX, ctx.ReportedY, ctx.BatteryPct,
            state.OrderId, state.LastNodeId, state.Driving, Errors = state.Errors.Count
        }, ct);
    }

    public async Task HandleConnectionAsync(RobotRef robot, Vda5050Connection conn, CancellationToken ct = default)
    {
        var ctx = await _db.RobotContexts.FindAsync(new object[] { robot.RobotId }, ct)
                  ?? _db.RobotContexts.Add(new RobotContextEntity { RobotId = robot.RobotId }).Entity;
        ctx.ConnectionState = conn.ConnectionState;

        var active = await _db.Missions.Where(m => m.RobotId == robot.RobotId &&
            (m.State == nameof(MissionState.Released) || m.State == nameof(MissionState.Running) ||
             m.State == nameof(MissionState.Disconnected))).ToListAsync(ct);

        var trigger = conn.ConnectionState == "ONLINE"
            ? MissionTrigger.ConnectionRestored : MissionTrigger.ConnectionLost;
        foreach (var m in active)
            await FireAsync(m, trigger, ct);

        await _db.SaveChangesAsync(ct);
        await _hub.Clients.All.SendAsync("RobotConnection",
            new { robot.RobotId, conn.ConnectionState }, ct);
    }

    /// <summary>상태머신 트리거 발화 + 전이 이벤트를 hist.transition_log에 기록 [ADR-010]</summary>
    private async Task FireAsync(MissionEntity mission, MissionTrigger trigger, CancellationToken ct)
    {
        var machine = new MissionStateMachine(Enum.Parse<MissionState>(mission.State));
        if (!machine.CanFire(trigger)) return;

        machine.OnTransition += (from, to, trg) =>
        {
            mission.State = to.ToString();
            if (to == MissionState.Completed || to == MissionState.Aborted)
                mission.EndedAt = DateTimeOffset.UtcNow;
            _db.TransitionLogs.Add(new TransitionLogEntity
            {
                MissionId = mission.MissionId,
                FromState = from.ToString(), ToState = to.ToString(), Trigger = trg.ToString()
            });
        };
        machine.Fire(trigger);
        await _hub.Clients.All.SendAsync("MissionProgress",
            new { mission.MissionId, mission.State }, ct);
    }

    private Task RecordInspectionResultAsync(MissionEntity mission, Data.Entities.OrderActionEntity oa,
        ActionState a, RobotRef robot, CancellationToken ct)
    {
        oa.Result = System.Text.Json.JsonSerializer.Serialize(new { a.ActionStatus, a.ResultDescription });
        _db.InspectionResults.Add(new InspectionResultEntity
        {
            ResultId = Guid.NewGuid(),
            RunId = mission.RunId,
            MissionId = mission.MissionId,
            TaskId = oa.TaskId,
            RobotId = robot.RobotId,
            NodeId = "",                        // TODO: node_sequence_id → order_node 조인으로 채움
            ActionType = oa.ActionType,
            Position = oa.Params ?? "{}",       // 위치+시각 대조 키 [ADR-004, Q2]
            Status = a.ActionStatus == "FINISHED" ? "SUCCESS" : "FAILED",
            Attempts = oa.Attempts + 1,
            OccurredAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }
}
