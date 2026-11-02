using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 계획 › 리치·간섭 정차점 최적화 — 면 전체를 요청 1건으로 보내면 수십 초~분이 걸려 HTTP 제한 시간에 걸리므로
/// 영역마다 나눠 동시 <see cref="ReachSimViewModel.OptimizeConcurrency"/>개씩 보내고, 진행·중지를 보인다.
/// </summary>
public class ReachOptimizeProgressTests
{
    /// <summary>영역 목록과 영역별 최적화 응답(지연 포함)을 흉내 내는 API.</summary>
    public class OptimizeApi : DispatchProxy
    {
        public List<AreaDto> Areas { get; set; } = new();
        public int DelayMs { get; set; } = 30;
        public List<Guid?> OptimizedAreaIds { get; } = new();
        public int MaxConcurrent { get; private set; }
        private int _running;

        protected override object? Invoke(MethodInfo? m, object?[]? args) => m!.Name switch
        {
            nameof(IAcsApiClient.GetAreasAsync) => Task.FromResult<IReadOnlyList<AreaDto>>(
                Areas.Where(a => a.WallCode == (string?)args![1] && a.Level == (int?)args[2]).ToList()),
            nameof(IAcsApiClient.GetStationOptimaAsync) => Optimize((Guid?)args![3], (CancellationToken)args[4]!),
            _ => m.ReturnType == typeof(Task) ? Task.CompletedTask : null,
        };

        private async Task<IReadOnlyList<ReachStationOptimumDto>?> Optimize(Guid? areaId, CancellationToken ct)
        {
            lock (OptimizedAreaIds)
            {
                OptimizedAreaIds.Add(areaId);
                MaxConcurrent = Math.Max(MaxConcurrent, ++_running);
            }
            try
            {
                await Task.Delay(DelayMs, ct);
                var a = Areas.Single(x => x.AreaId == areaId);
                return new[]
                {
                    new ReachStationOptimumDto(a.AreaId, a.Name, a.WallCode, a.Level, false, Cand(0, 1), Cand(2, 1.2), 50, DelayMs),
                };
            }
            finally { lock (OptimizedAreaIds) _running--; }
        }
    }

    private static ReachStationCandidateDto Cand(int clear, double dist) =>
        new(0, 0, 0, dist, 0, 0, 0.2, true, clear, 0, 2 - clear, 0.1);

    private static AreaDto Area(string name, int tasks, string wall = "PU", int level = 4) =>
        new(Guid.NewGuid(), "CT1", wall, level, name, 0, 0, 1, 1, null, null, null, 0, tasks);

    private static (ReachSimViewModel Vm, OptimizeApi Api) Setup(params AreaDto[] areas)
    {
        var api = DispatchProxy.Create<IAcsApiClient, OptimizeApi>();
        var fake = (OptimizeApi)(object)api;
        fake.Areas = areas.ToList();
        var vm = new ReachSimViewModel(api) { TankId = "CT1", SelectedLevel = 4, SelectedWall = "PU" };
        return (vm, fake);
    }

    [Fact]
    public async Task 면_최적화는_영역마다_나눠_보내고_영역_순서로_채운다()
    {
        var areas = Enumerable.Range(1, 9).Select(i => Area($"L4-R{10 - i:00}", 5)).ToList();
        areas.Add(Area("L4-EMPTY", 0));                 // 용접선 없는 영역은 보내지 않는다
        areas.Add(Area("L4-OTHER", 5, wall: "SU"));     // 다른 면
        var (vm, api) = Setup(areas.ToArray());

        await vm.OptimizeStationsCommand.ExecuteAsync(null);

        Assert.Equal(9, api.OptimizedAreaIds.Count);
        Assert.DoesNotContain(null, api.OptimizedAreaIds);   // 면 전체 1건 요청이 아니라 영역마다
        Assert.InRange(api.MaxConcurrent, 2, ReachSimViewModel.OptimizeConcurrency);
        Assert.Equal(areas.Take(9).Select(a => a.Name).OrderBy(n => n), vm.Optima.Select(r => r.Optimum.Name));
        Assert.StartsWith("정차점 최적화 완료 — L4 PU 영역 9개", vm.StatusMessage);
        Assert.StartsWith("간섭 없이 도달 0 → 18 / 18", vm.OptimizeSummary);
        Assert.False(vm.IsOptimizing);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task 중지하면_받은_영역까지만_남긴다()
    {
        var (vm, api) = Setup(Enumerable.Range(1, 12).Select(i => Area($"L4-R{i:00}", 5)).ToArray());
        api.DelayMs = 200;

        var run = vm.OptimizeStationsCommand.ExecuteAsync(null);
        while (vm.Optima.Count == 0) await Task.Delay(10);
        Assert.True(vm.IsOptimizing);
        vm.CancelOptimizeCommand.Execute(null);
        await run;

        Assert.False(vm.IsOptimizing);
        Assert.Contains("중지", vm.StatusMessage);
        Assert.InRange(vm.Optima.Count, 1, 11);
        Assert.True(api.OptimizedAreaIds.Count < 12, "중지 뒤에는 남은 영역을 보내지 않는다");
    }

    [Fact]
    public async Task 용접선_있는_영역이_없으면_요청하지_않는다()
    {
        var (vm, api) = Setup(Area("L4-EMPTY", 0));

        await vm.OptimizeStationsCommand.ExecuteAsync(null);

        Assert.Empty(api.OptimizedAreaIds);
        Assert.Contains("용접선이 있는 영역이 없습니다", vm.StatusMessage);
        Assert.False(vm.IsOptimizing);
    }
}
