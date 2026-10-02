using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>운영 ▸ 알람·이벤트 패널 — 현재 상태 판정과 이벤트 로그.</summary>
public class OperationStatusTests
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
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public void Robot(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
        public void Connection(string robot, string st) => RobotConnectionReceived?.Invoke(this, new(robot, st));
        public void RunState(Guid run, string st) => RunStateReceived?.Invoke(this, new(run, st));
        public void WorkItem(WorkItemProgressDto p) => WorkItemProgressReceived?.Invoke(this, p);
        public void Action(TaskActionProgressDto p) => TaskActionProgressReceived?.Invoke(this, p);
    }

    /// <summary>모든 호출에 기본값을 돌려주는 API 대역 — 이 테스트는 REST를 쓰지 않는다.</summary>
    public class NullApi : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var rt = m!.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = rt.GetGenericArguments()[0];
                var v = t.IsValueType ? Activator.CreateInstance(t) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
            }
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Wi = Guid.NewGuid();
    private static readonly Guid Task1 = Guid.NewGuid();

    private static (AlarmsViewModel vm, MissionViewModel mission, FakeMonitoring mon) Create(bool running = true)
    {
        var mon = new FakeMonitoring();
        var mission = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, NullApi>(), mon);
        var vm = new AlarmsViewModel(mon, mission);
        if (running)
        {
            mission.CurrentRun = new ScenarioRunDto(Run, Guid.NewGuid(), 1, "AMR-01", "RUNNING",
                DateTimeOffset.Now, null, new List<MissionDto>());
            var row = new WorkItemRow(new WorkItemDto(Wi, Guid.NewGuid(), "F-01", 1, "CT1-L1", 0, "DISPATCHED", 0));
            row.Tasks.Add(new TaskRow(Task1, 1, "W1"));
            mission.WorkItems.Add(row);
        }
        return (vm, mission, mon);
    }

    private static RobotStateDto State(bool driving = false, string mode = "AUTOMATIC", string estop = "NONE",
        params string[] errors) =>
        new("AMR-01", "CT1-L1", 1, 2, 97, "o", null, driving, errors.Length, null, mode, estop, errors);

    [Fact]
    public void Describe_Priorities()
    {
        Assert.Equal("fail", AlarmsViewModel.Describe("CONNECTIONBROKEN", State(), "RUNNING", "A", 0, false, null).Kind);
        Assert.Equal("비상정지 상태", AlarmsViewModel.Describe("ONLINE", State(estop: "REMOTE"), "RUNNING", "A", 0, false, null).Headline);
        Assert.Equal("비상정지 상태",
            AlarmsViewModel.Describe("ONLINE", State(errors: "emergencyStopActive: 정지"), null, null, 0, false, null).Headline);
        Assert.Equal("wait", AlarmsViewModel.Describe("ONLINE", State(), null, null, 0, false, null).Kind);
        Assert.Equal("대기 — 로봇 수동 모드", AlarmsViewModel.Describe("ONLINE", State(mode: "MANUAL"), null, null, 0, false, null).Headline);
        Assert.Equal("로봇 수동 모드 — 작업 진행 불가",
            AlarmsViewModel.Describe("ONLINE", State(mode: "MANUAL"), "RUNNING", "A", 0, false, null).Headline);
        Assert.Equal("검사 중 — A", AlarmsViewModel.Describe("ONLINE", State(driving: true), "RUNNING", "A", 0, true, null).Headline);
        var driving = AlarmsViewModel.Describe("ONLINE", State(driving: true), "RUNNING", "A", 1, false, null);
        Assert.Equal("주행 중 → A", driving.Headline);
        Assert.Contains("재시도 2회차", driving.Detail);
        Assert.Equal("이동 준비 → A", AlarmsViewModel.Describe(null, null, "RUNNING", "A", 0, false, null).Headline);
        Assert.Equal("층 이동 대기", AlarmsViewModel.Describe("ONLINE", State(), "WAITING_FLOOR_TRANSFER", null, 0, false, null).Headline);
        Assert.Equal("warn", AlarmsViewModel.Describe("ONLINE", State(), "ABORTED", null, 0, false, null).Kind);
    }

    [Fact]
    public void Describe_Completed_ReportsFailures()
    {
        var ok = new RunProgressDto(Run, 3, 3, 3, 3, 0, 0, 100);
        var bad = new RunProgressDto(Run, 3, 3, 3, 2, 1, 0, 100);
        Assert.Equal("done", AlarmsViewModel.Describe("ONLINE", State(), "COMPLETED", null, 0, false, ok).Kind);
        var s = AlarmsViewModel.Describe("ONLINE", State(), "COMPLETED", null, 0, false, bad);
        Assert.Equal("warn", s.Kind);
        Assert.Contains("실패 1건", s.Headline);
    }

    [Fact]
    public void StoFailureFlow_LogsReasonAndShowsLastFailure()
    {
        var (vm, mission, mon) = Create();
        mon.Robot(State(driving: true));
        Assert.Equal("주행 중 → F-01", vm.StatusHeadline);

        mon.Robot(State(driving: false));
        mon.Action(new TaskActionProgressDto(Run, Wi, Task1, Guid.NewGuid(), "FAILED", "로봇 이동 오류 보고: STO"));
        Assert.Equal("fail", vm.Events[0].Kind);
        Assert.Contains("F-01 / W1 실패", vm.Events[0].Text);
        Assert.Contains("STO", vm.LastFailure);

        mission.WorkItems[0].Status = "SKIPPED";
        mon.WorkItem(new WorkItemProgressDto(Run, Wi, Guid.NewGuid(), "CT1-L1", "SKIPPED", 2, "로봇 이동 오류 보고: STO"));
        Assert.Contains("건너뜀", vm.Events[0].Text);
        Assert.Contains("STO", vm.Events[0].Text);

        mon.RunState(Run, "COMPLETED");
        Assert.Equal("작업 완료 — 건너뛴 작업 있음", vm.Events[0].Text);
        Assert.Contains(vm.Events, e => e.Text == "AMR-01 정지");
    }

    [Fact]
    public void RobotModeEstopErrorAndConnectionChanges_AreLogged()
    {
        var (vm, _, mon) = Create();
        mon.Robot(State());
        mon.Robot(State(mode: "MANUAL"));
        Assert.Contains("자동 → 수동", vm.Events[0].Text);
        Assert.Equal("로봇 수동 모드 — 작업 진행 불가", vm.StatusHeadline);

        mon.Robot(State(mode: "MANUAL", errors: "drivingFailed: 경로 막힘"));
        Assert.Contains("경로 막힘", vm.Events[0].Text);
        mon.Robot(State(mode: "MANUAL"));
        Assert.Contains("오류 해제", vm.Events[0].Text);

        mon.Robot(State(mode: "MANUAL", estop: "MANUAL"));
        Assert.Equal("AMR-01 비상정지 발생", vm.Events[0].Text);
        Assert.Equal("fail", vm.StatusKind);

        mon.Connection("AMR-01", "CONNECTIONBROKEN");
        Assert.Equal("로봇 통신 두절", vm.StatusHeadline);
        Assert.Contains("통신 두절", vm.RobotSummary);
    }

    [Fact]
    public void CommandResults_AreLogged_AndOtherRunsIgnored()
    {
        var (vm, mission, mon) = Create();
        mission.StatusMessage = "미션 시작 실패: 이미 실행 중인 run이 있습니다";
        Assert.Equal("fail", vm.Events[0].Kind);

        int n = vm.Events.Count;
        mon.Action(new TaskActionProgressDto(Guid.NewGuid(), Wi, Task1, Guid.NewGuid(), "FAILED", "x"));
        Assert.Equal(n, vm.Events.Count);
        Assert.Null(vm.LastFailure);
    }

    [Fact]
    public void NoRun_ShowsIdle_AndEventLogIsCapped()
    {
        var (vm, mission, _) = Create(running: false);
        Assert.Equal("wait", vm.StatusKind);
        for (int i = 0; i < AlarmsViewModel.MaxEvents + 20; i++) mission.StatusMessage = $"m{i}";
        Assert.Equal(AlarmsViewModel.MaxEvents, vm.Events.Count);
        Assert.Equal($"m{AlarmsViewModel.MaxEvents + 19}", vm.Events[0].Text);
    }
}
