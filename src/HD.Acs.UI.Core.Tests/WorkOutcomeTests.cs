using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>미션 진행 표시 — 용접선(TASK) 성공/실패와 "소속 용접선 전부 성공 = 영역 성공" 판정.</summary>
public class WorkOutcomeTests
{
    private sealed class FakeMonitoring : IMonitoringClient
    {
#pragma warning disable CS0067
        public HubStatus Status => HubStatus.Connected;
        public event EventHandler<HubStatus>? StatusChanged;
        public event EventHandler<RobotStateDto>? RobotStateReceived;
        public event EventHandler<RobotConnectionDto>? RobotConnectionReceived;
        public event EventHandler<MissionProgressDto>? MissionProgressReceived;
        public event EventHandler<RunProgressDto>? RunProgressReceived;
        public event EventHandler<RunStateDto>? RunStateReceived;
        public event EventHandler<WorkItemProgressDto>? WorkItemProgressReceived;
        public event EventHandler<TaskActionProgressDto>? TaskActionProgressReceived;
        public event EventHandler<AlarmDto>? AlarmRaised;
        public event EventHandler<BatterySwapDispatchedDto>? BatterySwapDispatched;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void WorkItem(WorkItemProgressDto p) => WorkItemProgressReceived?.Invoke(this, p);
        public void Action(TaskActionProgressDto p) => TaskActionProgressReceived?.Invoke(this, p);
    }

    [Theory]
    [InlineData("DONE", "FINISHED,FINISHED", "SUCCESS")]
    [InlineData("FAILED", "FINISHED,FAILED", "FAILED")]
    [InlineData("SKIPPED", "FINISHED,FAILED", "FAILED")]
    [InlineData("DISPATCHED", "FINISHED,RUNNING", "RUNNING")]
    [InlineData("PENDING", "PLANNED,PLANNED", "PENDING")]
    [InlineData("PENDING", "FINISHED,WAITING", "PENDING")]      // 중단 후 이어하기 대기
    [InlineData("FAILED", "FINISHED,FINISHED", "SUCCESS")]      // 다시 실행해 결국 전부 성공
    [InlineData("DONE", "FINISHED,FAILED", "FAILED")]           // 성공 못 한 용접선이 있으면 성공 아님
    [InlineData("DONE", "FINISHED,PLANNED", "RUNNING")]
    [InlineData("DONE", "", "SUCCESS")]                          // 용접선 정보 없음 — work_item 상태를 따름
    [InlineData("FAILED", "", "FAILED")]
    public void Area_SuccessOnlyWhenAllTasksSucceeded(string workItem, string tasks, string expected)
    {
        var list = tasks.Length == 0 ? Array.Empty<string>() : tasks.Split(',');
        Assert.Equal(expected, WorkOutcome.Area(workItem, list));
    }

    [Fact]
    public void Colors_SuccessAreaDiffersFromPlanAndFailure()
    {
        var success = TankViewModel.StatusColors("SUCCESS");
        Assert.Equal("#802ECC71", success.Fill.ToString());
        Assert.NotEqual(TankViewModel.StatusColors(null).Fill, success.Fill);
        Assert.NotEqual(TankViewModel.StatusColors("FAILED").Fill, success.Fill);
        Assert.NotEqual(TankViewModel.StatusColors("FINISHED").Line, TankViewModel.StatusColors("FAILED").Line);
        Assert.NotEqual(TankViewModel.StatusColors(null).Fill, TankViewModel.StatusColors(WorkOutcome.Excluded).Fill);
    }

    [Fact]
    public void Labels_AreKorean()
    {
        Assert.Equal("성공", WorkOutcome.Label("FINISHED"));
        Assert.Equal("성공", WorkOutcome.Label("SUCCESS"));
        Assert.Equal("실패", WorkOutcome.Label("FAILED"));
        Assert.Equal("검사 중", WorkOutcome.Label("RUNNING"));
        Assert.Equal("대기", WorkOutcome.Label("PLANNED"));
    }

    [Fact]
    public void Pushes_UpdateTaskAndAreaOutcome_AndTankColors()
    {
        var mon = new FakeMonitoring();
        var api = DispatchProxy.Create<IAcsApiClient, OperationStatusTests.NullApi>();
        var mission = new MissionViewModel(api, mon);
        var tank = new TankViewModel(api, mon);
        Guid run = Guid.NewGuid(), wiA = Guid.NewGuid(), wiB = Guid.NewGuid();
        Guid areaA = Guid.NewGuid(), areaB = Guid.NewGuid(), other = Guid.NewGuid();
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), b1 = Guid.NewGuid();

        mission.CurrentRun = new ScenarioRunDto(run, Guid.NewGuid(), 1, "AMR-01", "RUNNING",
            DateTimeOffset.Now, null, new List<MissionDto>());
        var rowA = new WorkItemRow(new WorkItemDto(wiA, areaA, "A", 1, "CT1-L1", 0, "PENDING", 0));
        rowA.Tasks.Add(new TaskRow(a1, 1, "H01"));
        rowA.Tasks.Add(new TaskRow(a2, 2, "H02"));
        var rowB = new WorkItemRow(new WorkItemDto(wiB, areaB, "B", 1, "CT1-L1", 1, "PENDING", 0));
        rowB.Tasks.Add(new TaskRow(b1, 1, "V01"));
        mission.WorkItems.Add(rowA);
        mission.WorkItems.Add(rowB);
        // ShellViewModel 과 같은 연결
        mission.WorkItemsChanged += (_, _) => tank.ApplyWorkItemStatuses(
            mission.WorkItems.ToDictionary(w => w.AreaId, w => w.DisplayStatus),
            mission.WorkItems.SelectMany(w => w.Tasks).ToDictionary(t => t.TaskId, t => t.Status));

        // 영역 A: 배차 → 용접선 2개 성공
        mon.WorkItem(new WorkItemProgressDto(run, wiA, areaA, "CT1-L1", "DISPATCHED", 0, null));
        Assert.Equal("RUNNING", rowA.DisplayStatus);
        mon.Action(new TaskActionProgressDto(run, wiA, a1, Guid.NewGuid(), "FINISHED", "OK"));
        Assert.Equal("성공", rowA.Tasks[0].StatusLabel);
        Assert.Equal("RUNNING", rowA.DisplayStatus);                 // 아직 하나 남음
        Assert.Equal("FINISHED", tank.TaskStatusOf(a1));
        mon.Action(new TaskActionProgressDto(run, wiA, a2, Guid.NewGuid(), "FINISHED", "OK"));
        Assert.Equal("SUCCESS", rowA.DisplayStatus);                 // 전부 성공 → 영역 성공
        mon.WorkItem(new WorkItemProgressDto(run, wiA, areaA, "CT1-L1", "DONE", 0, null));
        Assert.Equal("SUCCESS", tank.WorkItemStatusOf(areaA));
        Assert.Equal("성공 2 / 2", rowA.TaskSummary);

        // 영역 B: 용접선 실패 → 영역 실패
        mon.WorkItem(new WorkItemProgressDto(run, wiB, areaB, "CT1-L1", "DISPATCHED", 0, null));
        mon.Action(new TaskActionProgressDto(run, wiB, b1, Guid.NewGuid(), "FAILED", "IK 실패"));
        mon.WorkItem(new WorkItemProgressDto(run, wiB, areaB, "CT1-L1", "FAILED", 1, "IK 실패"));
        Assert.Equal("FAILED", tank.WorkItemStatusOf(areaB));
        Assert.Equal("FAILED", tank.TaskStatusOf(b1));
        Assert.Equal("실패", rowB.StatusLabel);

        Assert.Equal("영역 성공 1 · 실패 1 / 2  |  용접선 성공 2 · 실패 1 / 3", mission.WorkSummary);
        Assert.True(tank.HasRunStatuses);
        Assert.Equal(WorkOutcome.Excluded, tank.WorkItemStatusOf(other));   // 실행 대상이 아닌 영역

        // 같은 정차를 다시 실행해 이미 성공한 용접선이 실패 보고돼도 성공으로 남는다
        mon.WorkItem(new WorkItemProgressDto(run, wiA, areaA, "CT1-L1", "DISPATCHED", 0, null));
        mon.Action(new TaskActionProgressDto(run, wiA, a1, Guid.NewGuid(), "FAILED", "중단"));
        Assert.Equal("FINISHED", rowA.Tasks[0].Status);

        // run 표시가 없으면 계획 보기
        tank.ApplyWorkItemStatuses(null);
        Assert.Null(tank.WorkItemStatusOf(areaA));
        Assert.False(tank.HasRunStatuses);
    }
}
