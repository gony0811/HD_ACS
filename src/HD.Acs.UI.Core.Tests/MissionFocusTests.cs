using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;
using H = HD.Acs.UI.Core.Tests.TaskHistoryColorTests.HistoryApi;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 운영 화면 보기 — 미션 강조(선택 미션 대상 강조·나머지 흑백) / 전체 현황(종전).
/// 영역 A·B·C: A=용접선 전부 성공 기록, B=실패 기록, C=일부만 기록(TaskHistoryColorTests와 같은 데이터).
/// </summary>
public class MissionFocusTests
{
    private static async Task<TankViewModel> Load()
    {
        H.History = new()
        {
            new(H.A1, H.A, "FINISHED", DateTimeOffset.Now), new(H.A2, H.A, "FINISHED", DateTimeOffset.Now),
            new(H.B1, H.B, "FINISHED", DateTimeOffset.Now), new(H.B2, H.B, "FAILED", DateTimeOffset.Now),
            new(H.C1, H.C, "FINISHED", DateTimeOffset.Now),
        };
        var vm = new TankViewModel(DispatchProxy.Create<IAcsApiClient, H>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>());
        await vm.LoadOverlaysAsync();
        await vm.LoadTaskHistoryAsync();
        return vm;
    }

    private static MissionAreaScope Only(params Guid[] ids) => new(false, ids.ToHashSet(), "'부분'");

    [Fact]
    public async Task BeforeRun_MissionAreasHighlighted_OthersMutedWithHistory()
    {
        var vm = await Load();
        Assert.Equal(TankDisplayMode.MissionFocus, vm.DisplayMode);   // 기본 보기
        Assert.False(vm.MissionFocusActive);                          // 기준 미션이 없으면 전체 현황과 같다
        Assert.Equal("SUCCESS", vm.AreaDisplayStatusOf(H.A));
        Assert.False(vm.IsAreaMuted(H.B));

        vm.SetMissionScope(Only(H.A));
        Assert.True(vm.MissionFocusActive);
        Assert.Equal(TankViewModel.MissionTarget, vm.AreaDisplayStatusOf(H.A));   // 지난 기록이 있어도 강조
        Assert.Null(vm.TaskDisplayStatusOf(H.A1));                                // 대상 영역 용접선 = 미검사(주황)
        Assert.False(vm.IsAreaMuted(H.A));

        Assert.Equal("FAILED", vm.AreaDisplayStatusOf(H.B));   // 대상 외 — 지난 기록 색(뷰가 흑백으로 낮춤)
        Assert.Equal("FAILED", vm.TaskDisplayStatusOf(H.B2));
        Assert.True(vm.IsAreaMuted(H.B));
        Assert.Null(vm.AreaDisplayStatusOf(H.C));              // 기록 불완전 — 계획색(흑백)
        Assert.True(vm.IsAreaMuted(H.C));
        Assert.True(vm.ShowLegend);
        Assert.Contains("대상 1개 영역 강조", vm.MissionViewSummary);
    }

    [Fact]
    public async Task DuringRun_ProgressColorsUnchanged_PendingStaysHighlighted_NonRunMuted()
    {
        var vm = await Load();
        vm.SetMissionScope(Only(H.A, H.B));
        vm.ApplyWorkItemStatuses(
            new Dictionary<Guid, string> { [H.A] = "DISPATCHED", [H.B] = "PENDING" },
            new Dictionary<Guid, string> { [H.A1] = "FINISHED", [H.A2] = "RUNNING", [H.B1] = "PLANNED", [H.B2] = "PLANNED" });

        Assert.Equal("DISPATCHED", vm.AreaDisplayStatusOf(H.A));               // 검사 중 = 파랑(종전과 같음)
        Assert.Equal("FINISHED", vm.TaskDisplayStatusOf(H.A1));
        Assert.Equal("RUNNING", vm.TaskDisplayStatusOf(H.A2));
        Assert.Equal(TankViewModel.MissionTarget, vm.AreaDisplayStatusOf(H.B));  // 차례 전 = 강조(지난 실패 기록 대신)
        Assert.Null(vm.TaskDisplayStatusOf(H.B2));
        Assert.Null(vm.AreaDisplayStatusOf(H.C));                               // run 밖 — 흐린 EXCLUDED 대신 계획색 + 흑백
        Assert.True(vm.IsAreaMuted(H.C));

        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [H.A] = "SUCCESS", [H.B] = "FAILED" },
            new Dictionary<Guid, string> { [H.A1] = "FINISHED", [H.A2] = "FINISHED", [H.B1] = "FAILED", [H.B2] = "FINISHED" });
        Assert.Equal("SUCCESS", vm.AreaDisplayStatusOf(H.A));
        Assert.Equal("FAILED", vm.AreaDisplayStatusOf(H.B));
    }

    [Fact]
    public async Task OverviewMode_IsPreviousDisplay_AndWholeTankScopeHighlightsAll()
    {
        var vm = await Load();
        vm.SetMissionScope(Only(H.A));
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [H.A] = "PENDING" },
            new Dictionary<Guid, string> { [H.A1] = "WAITING" });

        vm.ShowOverviewCommand.Execute(null);
        Assert.True(vm.IsOverviewView);
        Assert.False(vm.MissionFocusActive);
        foreach (var id in new[] { H.A, H.B, H.C })
        {
            Assert.Equal(vm.WorkItemStatusOf(id), vm.AreaDisplayStatusOf(id));   // 종전 판정 그대로
            Assert.False(vm.IsAreaMuted(id));
        }
        Assert.Equal(WorkOutcome.Excluded, vm.AreaDisplayStatusOf(H.C));
        Assert.Equal("선창 전체 진행 현황", vm.MissionViewSummary);

        vm.ShowMissionViewCommand.Execute(null);
        vm.ApplyWorkItemStatuses(null);
        vm.SetMissionScope(MissionAreaScope.Whole("'전체'"));
        Assert.All(new[] { H.A, H.B, H.C }, id =>
        {
            Assert.Equal(TankViewModel.MissionTarget, vm.AreaDisplayStatusOf(id));
            Assert.False(vm.IsAreaMuted(id));
        });
        Assert.Contains("선창 전체가 대상", vm.MissionViewSummary);
    }

    [Fact]
    public void Muted_IsGreyscaleWithLowerAlpha()
    {
        var m = Rgba.FromRgb(0x2E, 0xCC, 0x71).Muted();
        Assert.Equal(m.R, m.G); Assert.Equal(m.G, m.B);
        Assert.Equal(146, m.R);                        // 0.299·46 + 0.587·204 + 0.114·113
        Assert.Equal(107, m.A);                        // 255 × 0.42
        Assert.Equal(Rgba.FromRgb(1, 2, 3), Rgba.FromRgb(1, 2, 3).MutedIf(false));
        // 강조색은 다른 상태색과 구분된다
        var hl = TankViewModel.StatusColors(TankViewModel.MissionTarget);
        Assert.NotEqual(TankViewModel.StatusColors(null).Line, hl.Line);
        Assert.NotEqual(TankViewModel.StatusColors("DISPATCHED").Line, hl.Line);
    }

    [Fact]
    public void Scene3D_MutedAreaAndWeldsAreGreyscale()
    {
        var walls = new[]
        {
            new WallDto("T1", "SM", new[] { -10.0, -5, 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, 20, 4, null, true, null, null),
        };
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid();
        AreaDto Area(Guid id, string name, double u) => new(id, "T1", "SM", 1, name, u, 0.5, u + 1, 1.5, null, null, null, 0, 1);
        var overlays = new[]
        {
            new TankViewModel.AreaOverlay(Area(a1, "IN", 2), new[] { new AreaTaskDto(Guid.NewGuid(), 1, "W1", "LINE", 2.2, 1, 2.8, 1, "d", "p") }),
            new TankViewModel.AreaOverlay(Area(a2, "OUT", 5), new[] { new AreaTaskDto(Guid.NewGuid(), 1, "W1", "LINE", 5.2, 1, 5.8, 1, "d", "p") }),
        };
        var input = new TankSceneInput(walls, Array.Empty<WallDto>(), null, overlays, ShowOverlays: true, SelectedLevel: null,
            id => id == a1 ? TankViewModel.MissionTarget : "SUCCESS", _ => "FINISHED",
            HasRobotPosition: false, RobotPosition: default, AreaMuted: id => id == a2);
        var scene = TankSceneBuilder.Build(input);

        Assert.Contains(scene.Faces, f => f.Fill == TankViewModel.StatusColors(TankViewModel.MissionTarget).Fill);
        Assert.Contains(scene.Faces, f => f.Fill == TankViewModel.StatusColors("SUCCESS").Fill.Muted());
        Assert.DoesNotContain(scene.Faces, f => f.Fill == TankViewModel.StatusColors("SUCCESS").Fill);
        var weld = TankViewModel.WeldLineColor("FINISHED");
        Assert.Contains(scene.Segments, s => s.Color == weld);
        Assert.Contains(scene.Segments, s => s.Color == weld.Muted());
    }

    /// <summary>시나리오 대상 조회만 흉내 — 다른 테스트 클래스와 정적 상태를 나누지 않도록 따로 둔다.</summary>
    public class ScenarioApi : DispatchProxy
    {
        public static List<AreaDto> Areas = new();
        public static HashSet<Guid> Linked = new();

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = m!.Name switch
            {
                nameof(IAcsApiClient.GetAreasAsync) => (IReadOnlyList<AreaDto>)Areas,
                nameof(IAcsApiClient.GetScenarioAreasAsync) => (IReadOnlyList<ScenarioAreaDto>)Areas.Where(a => Linked.Contains(a.AreaId))
                    .Select(a => new ScenarioAreaDto(a.AreaId, a.WallCode, a.Level, a.Name, 0)).ToList(),
                _ => null,
            };
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }
    }

    [Fact]
    public async Task MissionViewModel_ScopeFollowsSelectedScenario()
    {
        var api = ScenarioApi.Areas = new()
        {
            new(Guid.NewGuid(), "CT1", "PM", 2, "L2-R01C01", 0, 0, 1, 1, null, null, null, 0, 3),
            new(Guid.NewGuid(), "CT1", "SM", 2, "L2-R01C01", 0, 0, 1, 1, null, null, null, 0, 3),
        };
        ScenarioApi.Linked = new() { api[0].AreaId };
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, ScenarioApi>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>());
        int changes = 0;
        vm.MissionScopeChanged += (_, _) => changes++;
        Assert.Null(vm.MissionScope);

        vm.Scenarios.Add(new ScenarioSummaryDto(Guid.NewGuid(), "부분", 1, "CT1", "ACTIVE"));
        vm.SelectedScenario = vm.Scenarios[0];
        for (int i = 0; i < 50 && vm.MissionScope is null; i++) await Task.Delay(10);
        var scope = Assert.IsType<MissionAreaScope>(vm.MissionScope);
        Assert.False(scope.All);
        Assert.Equal(new[] { api[0].AreaId }, scope.AreaIds);
        Assert.Equal(1, changes);

        // 대상 미지정 시나리오 = 선창 전체
        ScenarioApi.Linked = new();
        vm.Scenarios.Add(new ScenarioSummaryDto(Guid.NewGuid(), "전체", 1, "CT1", "ACTIVE"));
        vm.SelectedScenario = vm.Scenarios[1];
        for (int i = 0; i < 50 && vm.MissionScope?.All != true; i++) await Task.Delay(10);
        Assert.True(vm.MissionScope!.All);

        vm.SelectedScenario = null;
        for (int i = 0; i < 50 && vm.MissionScope is not null; i++) await Task.Delay(10);
        Assert.Null(vm.MissionScope);
    }
}
