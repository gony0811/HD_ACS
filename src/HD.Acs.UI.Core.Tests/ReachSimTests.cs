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
}
