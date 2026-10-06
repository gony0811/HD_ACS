using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 실패 정책(2026-10-02 운영 결정) — 자동 재시도 없음. 정차 검사가 실패하면 work_item FAILED로 종결하고
/// 용접선별 실패 사유를 담은 INSPECTION_FAILED 알람을 기록한다. 재실행 여부는 작업자가 결정한다.
/// </summary>
public class FailurePolicyTests
{
    // 2026-10-02 현장 실제 사유
    private const string IkFail = "recipe=LINE-WALL step=cobotInspection fail: 실행 실패: 역기구학(GetInverseKin) 실패 (errcode=112): 목표 자세 도달 불가";
    private const string StoFail = "로봇 이동 오류 보고: [{\"code\":\"W12_004\",\"msg\":\"STO\"}]";

    private sealed record Ctx(AcsDbContext Db, MissionEntity Mission, WorkItemEntity Wi, Guid[] TaskIds);

    /// <summary>정차 1곳(영역 F-01, 용접선 W1·W2) — statuses[i] = i번째 용접선 액션 결과(사유 포함).</summary>
    private static async Task<Ctx> Stop(params (string Status, string? Reason)[] actions)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var runId = Guid.NewGuid();
        var orderId = Guid.NewGuid().ToString();
        var mission = new MissionEntity
        {
            MissionId = Guid.NewGuid(), RunId = runId, MapId = "CT1-L1", RobotId = "AMR-01", OrderId = orderId, State = "Released",
        };
        db.ScenarioRuns.Add(new ScenarioRunEntity
        {
            RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = "AMR-01", State = "RUNNING", Missions = { mission },
        });
        var area = new InspectionAreaEntity { AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01" };
        var taskIds = actions.Select(_ => Guid.NewGuid()).ToArray();
        for (int i = 0; i < taskIds.Length; i++)
            area.Tasks.Add(new AreaTaskEntity { TaskId = taskIds[i], Seq = i + 1, Name = $"W{i + 1}", SectionDxfId = "D", ProfileId = "P" });
        db.InspectionAreas.Add(area);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ReportedMapId = "CT1-L1", ReportedX = 0, ReportedY = 0 });
        var wi = new WorkItemEntity
        {
            WorkItemId = Guid.NewGuid(), RunId = runId, AreaId = area.AreaId, MapId = "CT1-L1",
            Status = "DISPATCHED", OrderId = orderId, Actions = "[]",
        };
        db.WorkItems.Add(wi);
        for (int i = 0; i < actions.Length; i++)
            db.OrderActions.Add(new OrderActionEntity
            {
                ActionId = Guid.NewGuid(), MissionId = mission.MissionId, WorkItemId = wi.WorkItemId, TaskId = taskIds[i],
                ActionType = "startWeldInspection", Status = actions[i].Status,
                Result = JsonSerializer.Serialize(new { ActionStatus = actions[i].Status, ResultDescription = actions[i].Reason }),
            });
        await db.SaveChangesAsync();

        var d = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        await d.HandleStopOutcomeAsync(mission, default);   // 남은 PENDING 없음 → 발행 없이 층/런 완료 처리
        return new Ctx(db, mission, await db.WorkItems.AsNoTracking().SingleAsync(), taskIds);
    }

    [Theory]
    [InlineData(IkFail)]
    [InlineData(StoFail)]
    public async Task AnyFailure_IsNotRetried_AndRaisesAlarmWithFailedItems(string reason)
    {
        var c = await Stop(("FINISHED", "OK"), ("FAILED", reason));

        Assert.Equal(("FAILED", 1), (c.Wi.Status, c.Wi.Attempts));   // 재큐잉(PENDING) 없음
        var alarm = Assert.Single(await c.Db.Alarms.AsNoTracking().ToListAsync());
        Assert.Equal(("INSPECTION_FAILED", "AMR-01", (Guid?)c.Mission.MissionId), (alarm.AlarmCode, alarm.RobotId, alarm.MissionId));

        var root = JsonDocument.Parse(alarm.Detail!).RootElement;
        Assert.StartsWith("검사 실패: F-01 — ", root.GetProperty("title").GetString());
        var detail = root.GetProperty("detail");
        Assert.Equal("F-01", detail.GetProperty("areaName").GetString());
        var item = Assert.Single(detail.GetProperty("items").EnumerateArray());   // 실패한 용접선만
        Assert.Equal(c.TaskIds[1].ToString(), item.GetProperty("TaskId").GetString());
        Assert.Equal("W2", item.GetProperty("taskName").GetString());
        Assert.Equal(reason, item.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AllFinished_IsDone_WithoutAlarm()
    {
        var c = await Stop(("FINISHED", "OK"), ("FINISHED", "OK"));
        Assert.Equal("DONE", c.Wi.Status);
        Assert.Empty(await c.Db.Alarms.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailedStop_EndsRun_AndCountsAsFailedTask()
    {
        var c = await Stop(("FAILED", IkFail));
        // 남은 PENDING이 없으므로 run은 COMPLETED — 재시도로 다시 배차되지 않는다
        var run = await c.Db.ScenarioRuns.AsNoTracking().SingleAsync();
        Assert.Equal("COMPLETED", run.State);
        // TASK 최종 결과 판정: work_item FAILED = 소진 → 실패 확정 [SAIGE §6.3]
        Assert.Equal(HD.Acs.Core.Integration.TaskOutcome.Failed,
            HD.Acs.Core.Integration.TaskOutcome.Classify(new[] { "FAILED" }, c.Wi.Status));
    }
}
