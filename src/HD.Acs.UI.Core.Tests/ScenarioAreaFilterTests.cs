using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 계획 ▸ 시나리오 검사 대상 영역의 층·면·이름 필터 — 현장 요구: 영역이 천 개가 넘어 층·면을 고를 때 실수한다.
/// 필터는 화면 목록만 거르고, 저장은 필터로 가려진 대상까지 전부 보낸다.
/// </summary>
public class ScenarioAreaFilterTests
{
    public class Api : DispatchProxy
    {
        public static List<AreaDto> Areas = new();
        public static HashSet<Guid> Linked = new();
        public static List<Guid>? Saved;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = null;
            switch (m!.Name)
            {
                case nameof(IAcsApiClient.GetAreasAsync): v = (IReadOnlyList<AreaDto>)Areas; break;
                case nameof(IAcsApiClient.GetScenarioAreasAsync):
                    v = (IReadOnlyList<ScenarioAreaDto>)Areas.Where(a => Linked.Contains(a.AreaId))
                        .Select(a => new ScenarioAreaDto(a.AreaId, a.WallCode, a.Level, a.Name, 0)).ToList();
                    break;
                case nameof(IAcsApiClient.SetScenarioAreasAsync):
                    Saved = ((IReadOnlyList<Guid>)args![1]!).ToList();
                    break;
            }
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }
    }

    private static AreaDto A(string wall, int level, string name) =>
        new(Guid.NewGuid(), "CT1", wall, level, name, 0, 0, 1, 1, null, null, null, 0, 3);

    private static async Task<MissionViewModel> VmAsync()
    {
        Api.Areas = new()
        {
            A("B", 1, "L1-R01C01"), A("B", 1, "L1-R01C02"),
            A("PM", 2, "L2-R01C01"), A("SM", 2, "L2-R01C01"),
            A("PM", 4, "L4-R01C01"), A("T", 4, "L4-R02C05"),
        };
        Api.Linked = new() { Api.Areas[5].AreaId };   // 천장 하나가 이미 대상
        Api.Saved = null;
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, Api>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>());
        vm.Scenarios.Add(new ScenarioSummaryDto(Guid.NewGuid(), "부분", 1, "CT1", "ACTIVE"));
        vm.SelectedScenario = vm.Scenarios[0];
        for (int i = 0; i < 50 && vm.LevelFilterOptions.Count == 0; i++) await Task.Delay(10);
        return vm;
    }

    [Fact]
    public async Task LevelAndWallFilter_NarrowBothLists_AndWallOptionsFollowLevel()
    {
        var vm = await VmAsync();
        Assert.Equal(new[] { "전체 층", "L0", "L1", "L3" }, vm.LevelFilterOptions.Select(o => o.Label));
        Assert.Equal(5, vm.AvailableAreas.Count);
        Assert.Single(vm.SelectedAreas);

        vm.LevelFilter = vm.LevelFilterOptions.First(o => o.Level == 2);
        Assert.Equal(new[] { null, "SM", "PM" }, vm.WallFilterOptions.Select(o => o.Wall));   // 그 층에 있는 면만
        Assert.Equal(2, vm.AvailableAreas.Count);
        Assert.Empty(vm.SelectedAreas);                                                       // 대상 목록에도 같은 필터

        vm.WallFilter = vm.WallFilterOptions.First(o => o.Wall == "PM");
        Assert.Equal("L2-R01C01", Assert.Single(vm.AvailableAreas).Name);
        Assert.Equal("PM", vm.AvailableAreas[0].WallCode);
        Assert.Contains("표시 1 / 5", vm.AvailableAreasHeader);

        // 층을 바꾸면 그 층에 없는 면 필터는 "전체 면"으로 풀린다(B 층에는 PM 없음)
        vm.LevelFilter = vm.LevelFilterOptions.First(o => o.Level == 1);
        Assert.Null(vm.WallFilter!.Wall);
        Assert.Equal(2, vm.AvailableAreas.Count);
    }

    [Fact]
    public async Task NameFilter_AndClear()
    {
        var vm = await VmAsync();
        vm.AreaNameFilter = "r01c01";
        Assert.Equal(4, vm.AvailableAreas.Count);
        Assert.True(vm.IsAreaFilterActive);
        vm.ClearAreaFilterCommand.Execute(null);
        Assert.False(vm.IsAreaFilterActive);
        Assert.Equal(5, vm.AvailableAreas.Count);
    }

    [Fact]
    public async Task AddVisible_ThenSave_IncludesTargetsHiddenByFilter()
    {
        var vm = await VmAsync();
        vm.LevelFilter = vm.LevelFilterOptions.First(o => o.Level == 1);   // L0 바닥만 보이게
        vm.AddFilteredAreasCommand.Execute(null);
        Assert.Empty(vm.AvailableAreas);
        Assert.Equal(2, vm.SelectedAreas.Count);                           // 천장 대상은 필터로 가려짐
        Assert.Contains("L0 B 2", vm.ScenarioAreaBreakdown);
        Assert.Contains("L3 T 1", vm.ScenarioAreaBreakdown);
        Assert.Contains("대상 3개", vm.ScenarioAreaSummary);

        await vm.SaveScenarioAreasCommand.ExecuteAsync(null);
        Assert.NotNull(Api.Saved);
        Assert.Equal(3, Api.Saved!.Count);                                 // 가려진 천장 대상도 저장
        Assert.Contains(Api.Areas[5].AreaId, Api.Saved);
    }

    [Fact]
    public async Task RemoveVisible_KeepsHiddenTargets()
    {
        var vm = await VmAsync();
        vm.AddScenarioAreaCommand.Execute(vm.AvailableAreas[0]);            // 바닥 하나 추가 → 대상 2
        vm.LevelFilter = vm.LevelFilterOptions.First(o => o.Level == 1);
        vm.RemoveFilteredAreasCommand.Execute(null);                       // 보이는 바닥만 제거
        Assert.Contains("대상 1개", vm.ScenarioAreaSummary);
        Assert.Contains("L3 T 1", vm.ScenarioAreaBreakdown);
    }
}
