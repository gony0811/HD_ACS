using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>선창 전체 누적 기록 — run과 무관하게 용접선의 가장 최근 결과를 도면 색으로 쓴다.</summary>
public class TaskHistoryColorTests
{
    /// <summary>영역 3개(A: 용접선 2개 모두 성공 기록 / B: 하나 실패 기록 / C: 하나만 성공·하나 미검사).</summary>
    public class HistoryApi : DispatchProxy
    {
        public static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
        public static readonly Guid A1 = Guid.NewGuid(), A2 = Guid.NewGuid(), B1 = Guid.NewGuid(), B2 = Guid.NewGuid(),
            C1 = Guid.NewGuid(), C2 = Guid.NewGuid();
        public static List<TaskLatestResultDto>? History;
        public static int HistoryCalls;

        private static AreaDto Area(Guid id, string name) => new(id, "CT1", "F", 1, name, 0, 0, 1, 1, null, null, null, 0, 2);
        private static TankAreaTaskDto T(Guid area, Guid task, int seq) => new(area, task, seq, $"W{seq}", "LINE", 0, 0, 1, 0, "D", "P");

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = m!.Name switch
            {
                nameof(IAcsApiClient.GetAreasAsync) => new[] { Area(A, "A"), Area(B, "B"), Area(C, "C") },
                nameof(IAcsApiClient.GetTankAreaTasksAsync) => new[] { T(A, A1, 1), T(A, A2, 2), T(B, B1, 1), T(B, B2, 2), T(C, C1, 1), T(C, C2, 2) },
                nameof(IAcsApiClient.GetTaskLatestResultsAsync) => Count(History),
                _ => null,
            };
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                && m.Name != nameof(IAcsApiClient.GetTaskLatestResultsAsync))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }

        private static object? Count(object? v) { HistoryCalls++; return v; }
    }

    private static TaskLatestResultDto R(Guid area, Guid task, string status) => new(task, area, status, DateTimeOffset.Now);

    private static async Task<TankViewModel> Load()
    {
        HistoryApi.History = new()
        {
            R(HistoryApi.A, HistoryApi.A1, "FINISHED"), R(HistoryApi.A, HistoryApi.A2, "FINISHED"),
            R(HistoryApi.B, HistoryApi.B1, "FINISHED"), R(HistoryApi.B, HistoryApi.B2, "FAILED"),
            R(HistoryApi.C, HistoryApi.C1, "FINISHED"),
        };
        var vm = new TankViewModel(DispatchProxy.Create<IAcsApiClient, HistoryApi>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>());
        await vm.LoadOverlaysAsync();
        await vm.LoadTaskHistoryAsync();
        return vm;
    }

    [Fact]
    public async Task NoRun_ShowsLatestResultOfWholeTank()
    {
        var vm = await Load();

        Assert.Equal("FINISHED", vm.TaskStatusOf(HistoryApi.A1));
        Assert.Equal("FAILED", vm.TaskStatusOf(HistoryApi.B2));
        Assert.Null(vm.TaskStatusOf(HistoryApi.C2));                    // 미검사 = 계획 주황
        Assert.Equal("SUCCESS", vm.WorkItemStatusOf(HistoryApi.A));     // 용접선 전부 성공
        Assert.Equal("FAILED", vm.WorkItemStatusOf(HistoryApi.B));      // 최근 결과에 실패
        Assert.Null(vm.WorkItemStatusOf(HistoryApi.C));                 // 일부만 검사 — 판정 안 함
        Assert.True(vm.HasRunStatuses);                                 // 범례 표시
        Assert.Equal("누적 검사 5개 · 실패 1", vm.HistorySummary);

        // 끄면 현재 run 진행만(지금은 run 없음 → 계획 보기)
        vm.ShowHistory = false;
        Assert.Null(vm.TaskStatusOf(HistoryApi.A1));
        Assert.Null(vm.WorkItemStatusOf(HistoryApi.A));
        Assert.False(vm.HasRunStatuses);
    }

    [Fact]
    public async Task DuringRun_CurrentRunResultWins_UntouchedWeldsKeepHistory()
    {
        var vm = await Load();
        // 새 run: 영역 A·B 대상(전부 대기), A1은 이번에 실패, B2는 검사 중
        vm.ApplyWorkItemStatuses(
            new Dictionary<Guid, string> { [HistoryApi.A] = "FAILED", [HistoryApi.B] = "PENDING" },
            new Dictionary<Guid, string>
            {
                [HistoryApi.A1] = "FAILED", [HistoryApi.A2] = "WAITING",
                [HistoryApi.B1] = "PLANNED", [HistoryApi.B2] = "RUNNING",
            });

        Assert.Equal("FAILED", vm.TaskStatusOf(HistoryApi.A1));     // 이번 run 결과(가장 최근)
        Assert.Equal("FINISHED", vm.TaskStatusOf(HistoryApi.A2));   // 아직 대기 — 지난 기록 유지
        Assert.Equal("FINISHED", vm.TaskStatusOf(HistoryApi.B1));
        Assert.Equal("RUNNING", vm.TaskStatusOf(HistoryApi.B2));    // 검사 중이 지난 실패보다 앞
        Assert.Equal("FAILED", vm.WorkItemStatusOf(HistoryApi.A));
        Assert.Equal("FAILED", vm.WorkItemStatusOf(HistoryApi.B));  // run에선 대기 — 지난 기록(B2 실패)
        Assert.Equal(WorkOutcome.Excluded, vm.WorkItemStatusOf(HistoryApi.C));   // run 대상 아님·기록 불완전

        vm.ShowHistory = false;                                     // run 진행만
        Assert.Equal("WAITING", vm.TaskStatusOf(HistoryApi.A2));
        Assert.Equal("PENDING", vm.WorkItemStatusOf(HistoryApi.B));
    }

    /// <summary>리페어 run은 영역의 용접선 일부만 돈다 — 영역 결과는 이번 결과 + 나머지 용접선의 지난 기록으로 본다.</summary>
    [Fact]
    public async Task RepairRun_AreaOutcomeUsesAllWelds_NotOnlyRepairedOnes()
    {
        var vm = await Load();

        // B2(지난번 실패)만 재검사 → 성공. B1은 지난 기록이 성공 → 영역 성공
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [HistoryApi.B] = "RUNNING" },
            new Dictionary<Guid, string> { [HistoryApi.B2] = "RUNNING" }, partialAreas: true);
        Assert.Equal("RUNNING", vm.WorkItemStatusOf(HistoryApi.B));
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [HistoryApi.B] = "SUCCESS" },
            new Dictionary<Guid, string> { [HistoryApi.B2] = "FINISHED" }, partialAreas: true);
        Assert.Equal("SUCCESS", vm.WorkItemStatusOf(HistoryApi.B));

        // B1만 재검사해 성공해도 B2가 실패로 남아 있으면 영역은 실패
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [HistoryApi.B] = "SUCCESS" },
            new Dictionary<Guid, string> { [HistoryApi.B1] = "FINISHED" }, partialAreas: true);
        Assert.Equal("FAILED", vm.WorkItemStatusOf(HistoryApi.B));

        // C1만 재검사 성공 — C2는 한 번도 검사하지 않음 → 판정 안 함(대기색)
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [HistoryApi.C] = "SUCCESS" },
            new Dictionary<Guid, string> { [HistoryApi.C1] = "FINISHED" }, partialAreas: true);
        Assert.Equal("PENDING", vm.WorkItemStatusOf(HistoryApi.C));

        // 일반 run이면 종전대로 run 결과가 곧 영역 결과
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [HistoryApi.B] = "SUCCESS" },
            new Dictionary<Guid, string> { [HistoryApi.B1] = "FINISHED" });
        Assert.Equal("SUCCESS", vm.WorkItemStatusOf(HistoryApi.B));
    }

    [Fact]
    public async Task OldServer_NoHistory_KeepsPlanView()
    {
        var vm = await Load();
        HistoryApi.History = null;          // 구빌드 서버(조회 미지원)
        await vm.LoadTaskHistoryAsync();

        Assert.Null(vm.TaskStatusOf(HistoryApi.A1));
        Assert.Null(vm.WorkItemStatusOf(HistoryApi.A));
        Assert.Equal("누적 기록 없음", vm.HistorySummary);
    }
}
