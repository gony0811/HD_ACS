using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>계획 › 리치·간섭 — 영역 검사 가능 지도(3D 에 영역을 가능/불가 색으로 칠함).</summary>
public class ReachAreaMapTests
{
    public class MapApi : DispatchProxy
    {
        public List<WallDto> Walls { get; set; } = new();
        public List<AreaDto> Areas { get; set; } = new();
        public Dictionary<string, List<ReachAreaVerdictDto>> Map { get; } = new();
        public bool Supported { get; set; } = true;
        public List<(int? Level, string? Wall)> MapRequests { get; } = new();

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            switch (m!.Name)
            {
                case nameof(IAcsApiClient.GetWallsAsync):
                    return Task.FromResult<IReadOnlyList<WallDto>>(Walls);
                case nameof(IAcsApiClient.GetTankGeometryAsync):
                    return Task.FromResult<TankGeometryDto?>(null);
                case nameof(IAcsApiClient.GetAreasAsync):
                    return Task.FromResult<IReadOnlyList<AreaDto>>(Areas);
                case nameof(IAcsApiClient.GetReachSurveyAsync):
                    return Task.FromResult<IReadOnlyList<ReachSurveyAreaDto>?>(new List<ReachSurveyAreaDto>());
                case nameof(IAcsApiClient.GetReachAreaMapAsync):
                    MapRequests.Add(((int?)args![1], (string?)args[2]));
                    return Task.FromResult<IReadOnlyList<ReachAreaVerdictDto>?>(
                        Supported ? Map.GetValueOrDefault((string)args[2]!, new List<ReachAreaVerdictDto>()) : null);
                default:
                    return m.ReturnType == typeof(Task) ? Task.CompletedTask : null;
            }
        }
    }

    private static WallDto Wall(string code, double[] origin, double[] u, double[] v, double[] n) =>
        new("CT1", code, origin, u, v, n, 10, 3, 0, true, null);

    private static ReachAreaVerdictDto Verdict(string wall, string name, bool? possible, double u0, int level = 4) =>
        new(Guid.NewGuid(), name, wall, level, new[] { new[] { u0, 0.5 }, new[] { u0 + 1, 0.5 }, new[] { u0 + 1, 1.5 }, new[] { u0, 1.5 } },
            possible is null ? 0 : 3, possible == true ? 3 : possible == false ? 2 : 0, possible is null ? 3 : 0, possible, 0, null,
            // 용접선 3개 — 가능 영역은 전부 가능, 불가 영역은 3번만 불가, 대상 없음 영역은 전부 임시 제외
            Enumerable.Range(1, 3).Select(i => new ReachWeldVerdictDto(Guid.NewGuid(), i, u0 + 0.1, 0.5 + 0.25 * i, u0 + 0.9, 0.5 + 0.25 * i,
                possible is null ? null : possible == true || i < 3)).ToList());

    private static async Task<(ReachSimViewModel Vm, MapApi Api)> SetupAsync()
    {
        var api = DispatchProxy.Create<IAcsApiClient, MapApi>();
        var fake = (MapApi)(object)api;
        fake.Walls = new()
        {
            Wall("PU", new[] { 0.0, 4, 10 }, new[] { 1.0, 0, 0 }, new[] { 0.0, -0.7071, 0.7071 }, new[] { 0.0, -0.7071, -0.7071 }),
            Wall("T", new[] { 0.0, 2, 13 }, new[] { 1.0, 0, 0 }, new[] { 0.0, -1, 0 }, new[] { 0.0, 0, -1 }),
        };
        fake.Areas = new()
        {
            new(Guid.NewGuid(), "CT1", "PU", 4, "a", 0, 0, 1, 1, null, null, null, 0, 3),
            new(Guid.NewGuid(), "CT1", "T", 4, "b", 0, 0, 1, 1, null, null, null, 0, 3),
        };
        fake.Map["PU"] = new() { Verdict("PU", "L4-R01C01", true, 1), Verdict("PU", "L4-R01C02", false, 3) };
        fake.Map["T"] = new() { Verdict("T", "L4-R01C01X", null, 1) };
        var vm = new ReachSimViewModel(api) { TankId = "CT1" };
        await vm.LoadAsync();
        return (vm, fake);
    }

    private static List<Segment3> Seams(Scene3 s) => s.Segments.Where(x => x.Thickness == ReachAreaMapSceneBuilder.SeamThickness).ToList();

    [Fact]
    public async Task 지도는_층의_면마다_묻고_용접선을_가능_초록_불가_빨강으로_그린다()
    {
        var (vm, api) = await SetupAsync();
        var resets = 0;
        vm.ViewReset += (_, _) => resets++;

        await vm.ShowAreaMapAsync();

        Assert.Equal(new (int?, string?)[] { (4, "PU"), (4, "T") }, api.MapRequests);   // 그 층에 영역이 있는 면만
        Assert.True(vm.AreaMapMode);
        Assert.True(resets >= 1);                                                        // 카메라를 선창 전체에 다시 맞춤

        // 영역은 판정과 무관하게 같은 옅은 음영
        Assert.Equal(3, vm.Scene.Faces.Count);
        Assert.All(vm.Scene.Faces, f => Assert.Equal(ReachAreaMapSceneBuilder.AreaShade, f.Fill));

        // 용접선 9개 — 가능 5(초록) · 불가 1(빨강) · 임시 제외 3(회색)
        var seams = Seams(vm.Scene);
        Assert.Equal(9, seams.Count);
        Assert.Equal(5, seams.Count(x => x.Color == ReachAreaMapSceneBuilder.Green));
        Assert.Equal(1, seams.Count(x => x.Color == ReachAreaMapSceneBuilder.Red));
        Assert.Equal(3, seams.Count(x => x.Color == ReachAreaMapSceneBuilder.NoTarget));

        Assert.Contains("용접선 6개 — 검사 가능 5 · 불가 1 · 임시 제외 3", vm.AreaMapSummary);
        Assert.Contains("PU 5/6", vm.AreaMapSummary);
        Assert.Contains("전부 가능한 영역 1 · 하나라도 불가인 영역 1", vm.AreaMapSummary);
        Assert.NotEmpty(vm.Scene.ExtentPoints);                                          // 선창 윤곽
        Assert.Contains(vm.Scene.Labels, l => l.Text.StartsWith("PU") && l.Text.Contains("5 / 6"));
    }

    [Fact]
    public async Task 전체_층을_한_번_계산하고_층을_골라_걸러_본다()
    {
        var (vm, api) = await SetupAsync();
        // L1 에도 영역이 있는 선창
        api.Walls.Add(Wall("SM", new[] { 0.0, -4, 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }));
        api.Areas.Add(new(Guid.NewGuid(), "CT1", "SM", 1, "c", 0, 0, 1, 1, null, null, null, 0, 3));
        api.Map["SM"] = new() { Verdict("SM", "L1-R01C01", true, 1, level: 1) };
        await vm.LoadAsync();
        Assert.Equal(new[] { "전체", "L1", "L2", "L3", "L4" }, vm.AreaMapScopes);

        await vm.ShowAreaMapAsync();

        Assert.Equal(new (int?, string?)[] { (1, "SM"), (4, "PU"), (4, "T") }, api.MapRequests);   // 모든 층, 영역이 있는 면만
        Assert.Equal(4, vm.Scene.Faces.Count);
        Assert.Contains("전체 층 용접선 9개 — 검사 가능 8 · 불가 1", vm.AreaMapSummary);
        Assert.Contains("층별 가능/대상: L1 3/3 · L4 5/6", vm.AreaMapSummary);

        var changes = 0;
        vm.SceneChanged += (_, _) => changes++;
        vm.AreaMapScopeIndex = 1;                                                        // L1 만
        Assert.Equal(1, vm.AreaMapLevel);
        Assert.Single(vm.Scene.Faces);
        Assert.Equal(3, Seams(vm.Scene).Count);
        Assert.StartsWith("L1 용접선 3개 — 검사 가능 3 · 불가 0", vm.AreaMapSummary);
        Assert.DoesNotContain("층별", vm.AreaMapSummary);
        Assert.True(changes >= 1);

        vm.AreaMapScopeIndex = 4;                                                        // L4 만
        Assert.Equal(3, vm.Scene.Faces.Count);
        Assert.Contains("PU 5/6", vm.AreaMapSummary);

        vm.AreaMapScopeIndex = 2;                                                        // 영역이 없는 층
        Assert.Empty(vm.Scene.Faces);
        Assert.StartsWith("L2 용접선 0개", vm.AreaMapSummary);

        vm.AreaMapScopeIndex = 0;                                                        // 다시 전체
        Assert.Equal(4, vm.Scene.Faces.Count);
        Assert.Equal(3, api.MapRequests.Count);                                          // 층을 바꿔도 서버에 다시 묻지 않는다
    }

    [Fact]
    public async Task 닫으면_리치_상세로_돌아간다()
    {
        var (vm, _) = await SetupAsync();
        await vm.ShowAreaMapAsync();

        vm.CloseAreaMapCommand.Execute(null);

        Assert.False(vm.AreaMapMode);
        Assert.Empty(vm.Scene.Faces);                                                    // 리치 상세 씬(선택 용접선 없음)
    }

    [Fact]
    public void 시작과_끝이_같은_용접선은_점으로_그린다()
    {
        var wall = Wall("T", new[] { 0.0, 2, 13 }, new[] { 1.0, 0, 0 }, new[] { 0.0, -1, 0 }, new[] { 0.0, 0, -1 });
        var area = new ReachAreaVerdictDto(Guid.NewGuid(), "a", "T", 4, new[] { new[] { 0.0, 0 }, new[] { 1.0, 0 }, new[] { 1.0, 1 }, new[] { 0.0, 1 } },
            1, 0, 0, false, 0, null, new() { new(Guid.NewGuid(), 1, 0.5, 0.5, 0.5, 0.5, false) });

        var scene = ReachAreaMapSceneBuilder.Build(new[] { wall }, null, new[] { area });

        Assert.Empty(Seams(scene));
        Assert.Equal(ReachAreaMapSceneBuilder.Red, Assert.Single(scene.Markers).Fill);
    }

    [Fact]
    public async Task 구서버면_안내하고_지도를_켜지_않는다()
    {
        var (vm, api) = await SetupAsync();
        api.Supported = false;

        await vm.ShowAreaMapAsync();

        Assert.False(vm.AreaMapMode);
        Assert.False(vm.IsMapping);
        Assert.Contains("구빌드", vm.AreaMapSummary);
    }
}