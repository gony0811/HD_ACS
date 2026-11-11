using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>용접선 재검사(리페어 run) — 실패한 용접선만 골라 다시 검사한다.</summary>
public class RepairRunTests
{
    public class RepairApi : DispatchProxy
    {
        public static readonly Guid Area = Guid.NewGuid(), A1 = Guid.NewGuid(), A2 = Guid.NewGuid(), A3 = Guid.NewGuid();
        public static readonly Guid RepairRun = Guid.NewGuid(), Wi = Guid.NewGuid();
        public static List<(string Robot, List<Guid> Tasks, Guid? Scenario)> Started = new();
        public static bool OldServer;
        public static string? Reject;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = null;
            switch (m!.Name)
            {
                case nameof(IAcsApiClient.StartRepairRunAsync):
                    if (Reject is not null) throw new InvalidOperationException(Reject);
                    Started.Add(((string)args![0]!, ((IReadOnlyCollection<Guid>)args[1]!).ToList(), (Guid?)args[2]));
                    v = OldServer ? null : RepairRun;
                    break;
                case nameof(IAcsApiClient.GetRunAsync):
                    v = new ScenarioRunDto(RepairRun, Guid.NewGuid(), 1, "AMR-01", "RUNNING", DateTimeOffset.Now, null,
                        new List<MissionDto>(), Started.Last().Tasks);
                    break;
                case nameof(IAcsApiClient.GetWorkItemsAsync):
                    v = new[] { new WorkItemDto(Wi, Area, "A", 1, "CT1-L1", 0, "PENDING", 0) };
                    break;
                case nameof(IAcsApiClient.GetTankAreaTasksAsync):
                    v = new[] { T(A1, 1), T(A2, 2), T(A3, 3) };
                    break;
            }
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }

        private static TankAreaTaskDto T(Guid task, int seq) => new(Area, task, seq, $"H0{seq}", "LINE", 0, 0, 1, 0, "D", "P");
    }

    /// <summary>끝난 일반 run — 용접선 3개 중 A2·A3 실패.</summary>
    private static (MissionViewModel Vm, Guid Scenario) FinishedRunWithFailures(string state = "COMPLETED")
    {
        RepairApi.Started = new(); RepairApi.OldServer = false; RepairApi.Reject = null;
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, RepairApi>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>()) { SelectedRobotId = "AMR-01" };
        var row = new WorkItemRow(new WorkItemDto(Guid.NewGuid(), RepairApi.Area, "A", 1, "CT1-L1", 0, "SKIPPED", 2));
        foreach (var (id, seq, st) in new[] { (RepairApi.A1, 1, "FINISHED"), (RepairApi.A2, 2, "FAILED"), (RepairApi.A3, 3, "FAILED") })
        {
            var t = new TaskRow(id, seq, $"H0{seq}");
            t.Apply(st, null);
            row.Tasks.Add(t);
        }
        vm.WorkItems.Add(row);
        var scenario = Guid.NewGuid();
        vm.CurrentRun = new ScenarioRunDto(Guid.NewGuid(), scenario, 1, "AMR-01", state, DateTimeOffset.Now, null, new List<MissionDto>());
        return (vm, scenario);
    }

    [Fact]
    public async Task RepairFailed_StartsRunWithFailedWeldsOnly_AndShowsOnlyThose()
    {
        var (vm, scenario) = FinishedRunWithFailures();
        Assert.Equal("실패 용접선 재검사 (2)", vm.RepairFailedLabel);
        Assert.True(vm.RepairFailedCommand.CanExecute(null));

        await vm.RepairFailedCommand.ExecuteAsync(null);

        var call = Assert.Single(RepairApi.Started);
        Assert.Equal("AMR-01", call.Robot);
        Assert.Equal(new[] { RepairApi.A2, RepairApi.A3 }, call.Tasks);
        Assert.Equal(scenario, call.Scenario);                      // 보고 있던 run의 시나리오로 기록

        Assert.True(vm.CurrentRun!.IsRepair);
        var row = Assert.Single(vm.WorkItems);
        Assert.Equal(new[] { RepairApi.A2, RepairApi.A3 }, row.Tasks.Select(t => t.TaskId));   // 성공했던 A1은 대상 아님
        Assert.StartsWith("재검사 · ", vm.WorkSummary);
        Assert.Contains("재검사 시작: 실패 용접선 2개", vm.StatusMessage);
        Assert.False(vm.RepairFailedCommand.CanExecute(null));      // 재검사 run 진행 중
    }

    [Fact]
    public async Task RepairTask_SingleWeld()
    {
        var (vm, _) = FinishedRunWithFailures();
        var weld = vm.WorkItems[0].Tasks[1];
        Assert.True(vm.RepairTaskCommand.CanExecute(weld));

        await vm.RepairTaskCommand.ExecuteAsync(weld);

        Assert.Equal(new[] { RepairApi.A2 }, Assert.Single(RepairApi.Started).Tasks);
        Assert.Equal(new[] { RepairApi.A2 }, Assert.Single(vm.WorkItems).Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public void Repair_DisabledWhileRunActive()
    {
        var (vm, _) = FinishedRunWithFailures("RUNNING");
        Assert.False(vm.RepairFailedCommand.CanExecute(null));
        Assert.False(vm.RepairTaskCommand.CanExecute(vm.WorkItems[0].Tasks[1]));
    }

    [Fact]
    public async Task Repair_ShowsServerRejection_AndOldServer()
    {
        var (vm, _) = FinishedRunWithFailures();
        RepairApi.Reject = "로봇 AMR-01에 진행 중인 run이 있습니다";
        await vm.RepairFailedCommand.ExecuteAsync(null);
        Assert.Equal("재검사 실패: 로봇 AMR-01에 진행 중인 run이 있습니다", vm.StatusMessage);

        RepairApi.Reject = null; RepairApi.OldServer = true;
        await vm.RepairFailedCommand.ExecuteAsync(null);
        Assert.Contains("구빌드", vm.StatusMessage);
        Assert.False(vm.CurrentRun!.IsRepair);                      // 화면은 그대로
    }
}
