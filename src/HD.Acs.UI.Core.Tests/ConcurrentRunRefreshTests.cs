using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// run 새로고침이 동시에 두 번 돌 때(미션 시작 경로 + RunState 푸시의 Adopt) 실행 큐 행이 중복되면 안 된다.
/// 2026-11-24 현장: 행이 중복된 채로 WorkItemProgress 푸시를 받아 ToDictionary(AreaId) 에서 UI 가 종료됐다.
/// </summary>
public class ConcurrentRunRefreshTests
{
    public class GatedApi : DispatchProxy
    {
        public static readonly Guid Run = Guid.NewGuid(), Area = Guid.NewGuid(), Wi = Guid.NewGuid(), Weld = Guid.NewGuid();
        /// <summary>work-items 응답을 테스트가 풀 때까지 붙잡는다 — 두 새로고침이 모두 조회 중인 상태를 만든다.</summary>
        public static TaskCompletionSource<IReadOnlyList<WorkItemDto>> Gate = new();
        /// <summary>run 응답도 붙잡는다 — 현장처럼 두 번째 푸시가 왔을 때 아직 CurrentRun 이 바뀌기 전이다.</summary>
        public static TaskCompletionSource<ScenarioRunDto?> RunGate = new();
        public static int WorkItemCalls;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = null;
            switch (m!.Name)
            {
                case nameof(IAcsApiClient.GetRunAsync):
                    return RunGate.Task;
                case nameof(IAcsApiClient.GetWorkItemsAsync):
                    Interlocked.Increment(ref WorkItemCalls);
                    return Gate.Task;
                case nameof(IAcsApiClient.GetTankAreaTasksAsync):
                    v = new[] { new TankAreaTaskDto(Area, Weld, 1, "H01", "LINE", 0, 0, 1, 0, "D", "P") };
                    break;
            }
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }
    }

    /// <summary>구독한 푸시 핸들러를 잡아 두고 테스트가 직접 발화한다.</summary>
    public class PushMonitoring : DispatchProxy
    {
        public static EventHandler<RunStateDto>? RunState;
        public static EventHandler<WorkItemProgressDto>? WorkItemProgress;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            switch (m!.Name)
            {
                case "add_RunStateReceived": RunState += (EventHandler<RunStateDto>)args![0]!; break;
                case "add_WorkItemProgressReceived": WorkItemProgress += (EventHandler<WorkItemProgressDto>)args![0]!; break;
            }
            return m.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    [Fact]
    public async Task TwoOverlappingRefreshes_LeaveEachWorkItemOnce_AndProgressPushDoesNotThrow()
    {
        GatedApi.Gate = new(); GatedApi.RunGate = new();
        GatedApi.WorkItemCalls = 0;
        PushMonitoring.RunState = null; PushMonitoring.WorkItemProgress = null;
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, GatedApi>(),
            DispatchProxy.Create<IMonitoringClient, PushMonitoring>());

        // 같은 새 run 에 대한 새로고침 두 번이 겹친다(현장: 미션 시작 경로 + RunState 푸시)
        var push = new RunStateDto(GatedApi.Run, "WAITING_FLOOR_TRANSFER");
        PushMonitoring.RunState!(null, push);
        PushMonitoring.RunState!(null, push);
        GatedApi.RunGate.SetResult(new ScenarioRunDto(GatedApi.Run, Guid.NewGuid(), 1, "AMR-01", "WAITING_FLOOR_TRANSFER",
            DateTimeOffset.Now, null, new List<MissionDto>()));
        await WaitUntil(() => Volatile.Read(ref GatedApi.WorkItemCalls) == 2);

        GatedApi.Gate.SetResult(new[] { new WorkItemDto(GatedApi.Wi, GatedApi.Area, "L4-R01C21", 4, "CT1-L4", 0, "PENDING", 0) });
        await WaitUntil(() => vm.CurrentRun is not null && vm.WorkItems.Count > 0);
        await Task.Delay(50);   // 늦게 끝나는 쪽까지 기다린다

        var row = Assert.Single(vm.WorkItems);
        Assert.Equal(GatedApi.Wi, row.WorkItemId);
        Assert.Single(row.Tasks);

        // 현장에서 UI 를 죽인 바로 그 푸시 — 예외 없이 반영돼야 한다
        var raised = 0;
        vm.WorkItemsChanged += (_, _) =>
        {
            raised++;
            _ = ShellViewModel.ToLastWins(vm.WorkItems.Select(w => (w.AreaId, w.DisplayStatus)));
        };
        PushMonitoring.WorkItemProgress!(null, new WorkItemProgressDto(GatedApi.Run, GatedApi.Wi, GatedApi.Area, "CT1-L4", "DISPATCHED", 1));
        Assert.Equal(1, raised);
        Assert.Equal("DISPATCHED", row.Status);
    }

    [Fact]
    public void ToLastWins_DuplicateKeys_KeepsLastValue()
    {
        var k = Guid.NewGuid();
        var map = ShellViewModel.ToLastWins(new[] { (k, "PENDING"), (Guid.NewGuid(), "SUCCESS"), (k, "FAILED") });
        Assert.Equal(2, map.Count);
        Assert.Equal("FAILED", map[k]);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 5초 안에 충족되지 않았습니다.");
            await Task.Delay(10);
        }
    }
}
