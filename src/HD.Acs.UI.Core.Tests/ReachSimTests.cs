using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>계획 › 리치·간섭 — 서버 상세 응답으로 판정 카드·IK 해 표·3D 씬을 만든다.</summary>
public class ReachSimTests
{
    private static WallDto Wall(string code, double[] origin, double[] u, double[] v, double[] n, double uLen, double vLen) =>
        new("T1", code, origin, u, v, n, uLen, vLen, null, true, null);

    // 차체가 +x 를 보고 (0,0) 에 섰다 — SM 벽은 x = 1.2, 차체 좌측(+y)으로 넓게 뻗은 면 하나.
    private static readonly List<WallDto> Walls = new()
    {
        Wall("SM", new[] { 1.2, -5, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }, 10, 4),
    };

    private static ReachPartDto Part(string name, double clearance, double z = 1.8) =>
        new(name, new[] { 0.0, 0, z }, new[] { 0.3, 0, z }, 0.05, clearance, "SM");

    private static ReachSolutionDto Sol(double minCl, bool ok = true, params ReachPartDto[] parts) =>
        new(new[] { 10.0, -60, 90, -100, 70, 0 }, ok, ok ? 30 : -5, ok ? "여유 있음" : "J2 관절한계", false, minCl,
            new[] { 1.2, 0, 1.5 }, parts.ToList(), new List<ReachJointDto> { new("J2", new[] { 0.0, 0.1, 1.8 }) });

    private static ReachDetailDto Detail(double bodyClearance, params ReachStageDto[] stages) => new(
        Guid.NewGuid(), "L2-SM-R01", "SM", 2, 0, 0, 0, 0, 1, 0, 300, 0, 0, Guid.NewGuid(), 1, new[] { 1.2, 0, 1.5 },
        new List<ReachSeamLineDto>(),
        new ReachBodyDto(new[]
        {
            new[] { 0.395, -0.35, 0 }, new[] { 0.395, 0.35, 0 }, new[] { -0.365, 0.35, 0 }, new[] { -0.365, -0.35, 0 },
            new[] { 0.395, -0.35, 0.887 }, new[] { 0.395, 0.35, 0.887 }, new[] { -0.365, 0.35, 0.887 }, new[] { -0.365, -0.35, 0.887 },
        }, bodyClearance, "SM"),
        stages.ToList(), 0.05);

    private static ReachStageDto Stage(string name, int chosen, params ReachSolutionDto[] sols) =>
        new(name, 0, new[] { 1.2, 0, 1.5 }, chosen, sols.ToList());

    private static ReachSimViewModel Vm() => new(DispatchProxy.Create<IAcsApiClient, NullApi>());

    public class NullApi : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null;
    }

    private static ReachDetailDto ThreeStages(double bodyClearance) => Detail(bodyClearance,
        Stage("Approach", 0, Sol(0.2, true, Part("상완", 0.2))),
        Stage("Align", 0, Sol(0.1, true, Part("상완", 0.1))),
        Stage("Focus", 1,
            Sol(-0.04, true, Part("상완", 0.3), Part("손목1(J4)", -0.04)),
            Sol(0.03, true, Part("상완", 0.3), Part("손목1(J4)", 0.03)),
            Sol(0.4, false, Part("상완", 0.4))));

    [Fact]
    public void 운영_선택_해의_간섭으로_판정한다()
    {
        var vm = Vm();
        vm.ApplyDetail(ThreeStages(0.8));   // 기본 = 초점 검사 자세, 운영 선택 #2(여유 30 mm)

        Assert.Equal("warn", vm.VerdictLevel);
        Assert.StartsWith("여유 부족 1곳", vm.VerdictTitle);
        Assert.Equal(3, vm.Solutions.Count);
        Assert.True(vm.Solutions[1].IsOperational);
        Assert.Equal("2 ★", vm.Solutions[1].Label);
        Assert.Equal(vm.Solutions[1], vm.SelectedSolution);
        Assert.Equal("AMR 차체", vm.Parts[0].Name);
    }

    [Fact]
    public void 해를_고르면_그_해로_판정하고_간섭_최소_해는_자세_한계_안에서_고른다()
    {
        var vm = Vm();
        vm.ApplyDetail(ThreeStages(0.8));

        vm.SolutionIndex = 0;
        Assert.Equal("bad", vm.VerdictLevel);
        Assert.Contains("손목1(J4)", vm.VerdictDetail);

        vm.ClearestSolutionCommand.Execute(null);
        Assert.Equal(1, vm.SolutionIndex);   // #3 은 여유가 더 크지만 자세 한계 밖

        vm.OperationalSolutionCommand.Execute(null);
        Assert.Null(vm.SolutionIndex);
    }

    [Fact]
    public void 차체가_면에_묻히면_간섭이고_자세를_바꾸면_다시_판정한다()
    {
        var vm = Vm();
        vm.ApplyDetail(ThreeStages(-0.7));
        Assert.Equal("bad", vm.VerdictLevel);
        Assert.Contains("AMR 차체", vm.VerdictDetail);

        vm.StageIndex = 0;
        Assert.Single(vm.Solutions);
        Assert.Equal("bad", vm.VerdictLevel);   // 차체 간섭은 자세와 무관
    }

    [Fact]
    public void 앞_자세에서_해가_없으면_뒤_자세는_도달불가다()
    {
        var vm = Vm();
        vm.ApplyDetail(Detail(0.8, Stage("Approach", -1)));

        Assert.Equal("na", vm.VerdictLevel);
        Assert.Equal("도달 불가", vm.VerdictTitle);
        Assert.Contains("앞 자세", vm.VerdictDetail);
    }

    [Fact]
    public void 씬은_부품을_여유로_칠하고_단면_자르기는_카메라쪽_면을_자른다()
    {
        var d = ThreeStages(0.8);
        var solFaces = ReachSimSceneBuilder.Build(new ReachSceneInput(Walls, d, 2, 0, ClipSection: false));
        Assert.Contains(solFaces.Faces, f => f.Fill == ReachSimSceneBuilder.Hit);   // 손목1 간섭
        Assert.Contains(solFaces.Labels, l => l.Text == "J2");

        // 벽은 y −5…+5 — 차체 좌측(+y) 0.9 m 밖은 남고 우측은 잘린다 → 잘린 면의 y 최소는 −0.9.
        var wall = solFaces.Faces.First(f => f.Fill == ReachSimSceneBuilder.WallActiveFill);
        Assert.Equal(-5, wall.Points.Min(p => p.Y), 6);
        var clipped = ReachSimSceneBuilder.Build(new ReachSceneInput(Walls, d, 2, 0, ClipSection: true))
            .Faces.First(f => f.Fill == ReachSimSceneBuilder.WallActiveFill);
        Assert.Equal(-ReachSimSceneBuilder.ClipOffsetM, clipped.Points.Min(p => p.Y), 6);
    }

    [Fact]
    public void 차체가_간섭이면_차체_면을_간섭색으로_그린다()
    {
        var scene = ReachSimSceneBuilder.Build(new ReachSceneInput(Walls, ThreeStages(-0.2), 2, null, false));
        Assert.Equal(5, scene.Faces.Count(f => f.Fill == ReachSimSceneBuilder.Hit with { A = 0xD0 }));
        Assert.NotEmpty(scene.ExtentPoints);
    }

    // ── 정차점 최적화 ──

    /// <summary>호출을 기록하는 API — Task 는 완료, Task&lt;T&gt; 는 default 를 돌려준다.</summary>
    public class RecordingApi : DispatchProxy
    {
        public List<(string Method, object?[] Args)> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            Calls.Add((m!.Name, args ?? Array.Empty<object?>()));
            var t = m.ReturnType;
            if (t == typeof(Task)) return Task.CompletedTask;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var r = t.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(r)
                    .Invoke(null, new[] { r.IsValueType ? Activator.CreateInstance(r) : null });
            }
            return t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t) : null;
        }
    }

    private static ReachStationCandidateDto Cand(double x, double dist, int clear, int hit, int un, double lateral = 0, double yaw = 0) =>
        new(x, 0, 0, dist, lateral, yaw, 0.2, true, clear, hit, un, 0.1);

    private static List<ReachStationOptimumDto> Optima() => new()
    {
        new(Guid.NewGuid(), "L1-R01", "SL", 1, false, Cand(1, 0.57, 0, 0, 7), Cand(1.8, 1.4, 7, 0, 0), 90, 300),
        new(Guid.NewGuid(), "L1-R04", "SL", 1, false, Cand(1, 0.57, 0, 0, 4), Cand(1, 0.57, 0, 0, 4), 1, 5),
    };

    [Fact]
    public void 최적화_표는_옮길_영역만_적용_대상으로_둔다()
    {
        var vm = Vm();
        vm.ApplyOptima(Optima());

        Assert.Equal(2, vm.Optima.Count);
        Assert.True(vm.Optima[0].Improved);
        Assert.Equal("0/0/7", vm.Optima[0].Current);
        Assert.Equal("7/0/0", vm.Optima[0].Best);
        Assert.Equal("1.4 m ← 0.6", vm.Optima[0].Move);
        Assert.Contains("찾은 정차 — 안쪽 1.40 m", vm.OptimumDetail);
        Assert.False(vm.Optima[1].Improved);
        Assert.Equal("그대로", vm.Optima[1].Move);
        Assert.Equal(vm.Optima[0], vm.SelectedOptimum);
        Assert.StartsWith("간섭 없이 도달 0 → 7 / 11 · 옮길 영역 1개", vm.OptimizeSummary);
    }

    [Fact]
    public async Task 모두_적용은_옮길_영역의_찾은_정차만_저장한다()
    {
        var api = DispatchProxy.Create<IAcsApiClient, RecordingApi>();
        var vm = new ReachSimViewModel(api);
        var optima = Optima();
        vm.ApplyOptima(optima);

        await vm.ApplyAllOptimaCommand.ExecuteAsync(null);

        var set = Assert.Single(((RecordingApi)(object)api).Calls, c => c.Method == nameof(IAcsApiClient.SetAreaStationAsync));
        Assert.Equal(optima[0].AreaId, set.Args[0]);
        Assert.Equal(1.8, set.Args[1]);
        Assert.True(vm.Optima[0].Applied);
        Assert.False(vm.Optima[0].Improved);   // 다시 적용하지 않는다
        Assert.EndsWith("(덮어씀)", vm.Optima[0].Area);
    }

    [Fact]
    public void 층이나_면을_바꾸면_최적화_표를_비운다()
    {
        var vm = Vm();
        vm.ApplyOptima(Optima());
        vm.SelectedWall = "PL";
        Assert.Empty(vm.Optima);
        Assert.Equal("", vm.OptimizeSummary);
    }

    [Fact]
    public async Task 간섭_여유_최대_가지를_켜면_조사에_가지_규칙을_넘긴다()
    {
        var api = DispatchProxy.Create<IAcsApiClient, RecordingApi>();
        var vm = new ReachSimViewModel(api);
        vm.ApplyOptima(Optima());

        vm.ClearanceBranch = true;
        await Task.Delay(50);

        var call = ((RecordingApi)(object)api).Calls.Last(c => c.Method == nameof(IAcsApiClient.GetReachSurveyAsync));
        Assert.Equal("clearance", call.Args[^1]);
        Assert.Empty(vm.Optima);   // 규칙이 바뀌면 최적화 결과는 버린다
    }
}
