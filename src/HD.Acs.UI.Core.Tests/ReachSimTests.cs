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

    [Fact]
    public void 툴_상자_부품은_상자_6면으로_그리고_간섭이면_빨강이다()
    {
        double[][] box =
        {
            new[] { 1.0, -0.1, 1.4 }, new[] { 1.2, -0.1, 1.4 }, new[] { 1.2, 0.1, 1.4 }, new[] { 1.0, 0.1, 1.4 },
            new[] { 1.0, -0.1, 1.6 }, new[] { 1.2, -0.1, 1.6 }, new[] { 1.2, 0.1, 1.6 }, new[] { 1.0, 0.1, 1.6 },
        };
        var tool = new ReachPartDto("검사 툴(카메라 브래킷)", new[] { 1.1, 0, 1.4 }, new[] { 1.1, 0, 1.6 }, 0, -0.01, "SM", box);
        var d = Detail(0.3, Stage("Focus", 0, Sol(-0.01, true, tool)));

        var scene = ReachSimSceneBuilder.Build(new ReachSceneInput(Walls, d, 0, null, false));

        var hit = scene.Faces.Where(f => f.Fill == ReachSimSceneBuilder.Hit with { A = 0x90 }).ToList();
        Assert.Equal(6, hit.Count);
        Assert.All(hit, f => Assert.Equal(4, f.Points.Count));
        Assert.Contains(scene.ExtentPoints, p => Math.Abs(p.X - 1.2) < 1e-9 && Math.Abs(p.Z - 1.6) < 1e-9);
    }

    private static ReachDetailDto ThreeStages(double bodyClearance) => Detail(bodyClearance,
        Stage("Approach", 0, Sol(0.2, true, Part("상완", 0.2))),
        Stage("Align", 0, Sol(0.1, true, Part("상완", 0.1))),
        Stage("Focus", 1,
            Sol(-0.04, true, Part("상완", 0.3), Part("손목1(J4)", -0.04)),
            Sol(0.03, true, Part("상완", 0.3), Part("손목1(J4)", 0.03)),
            Sol(0.4, false, Part("상완", 0.4))));

    [Fact]
    public void 운영_진입_경로가_없으면_자세가_닿아도_막힘으로_판정한다()
    {
        var vm = Vm();
        var entry = new ReachEntryDto(false, "진입 경로 없음 — 면 방향 584mm, 후보 6개 모두 거부: 후퇴 평면 [-1120, -154, 80]: 자기 간섭",
            584, 6, new[] { 0.0, 0, 2 }, null, new[] { 0.5, 0, 2 }, new List<string> { "후퇴 평면 [-1120, -154, 80]: 자기 간섭" });
        vm.ApplyDetail(ThreeStages(0.8) with { Entry = entry });

        Assert.Equal("bad", vm.VerdictLevel);
        Assert.StartsWith("운영 진입 불가", vm.VerdictTitle);
        Assert.StartsWith("진입 경로 없음", vm.VerdictDetail);

        vm.ApplyDetail(ThreeStages(0.8) with { BranchChange = "출발 자세에서 가지 전환 — 손목 반전(J5 90.1° → -77.0°)" });
        Assert.StartsWith("출발 자세에서 가지 전환", vm.VerdictDetail);
    }

    [Fact]
    public void 도면_순회가_막히면_자세가_닿아도_막힘으로_판정하고_통과면_여유를_보인다()
    {
        var vm = Vm();
        var blocked = new ReachTraversalDto(false, "도면 순회 — 0mm 지점 기울기 +40° 손목 특이점(J5 55.6° → -1.2°)", -14,
            "손목 특이점(J5 55.6° → -9.0°)", 0, 400, 40, null, 63);
        vm.ApplyDetail(ThreeStages(0.8) with { Traversal = blocked });

        Assert.Equal("bad", vm.VerdictLevel);
        Assert.StartsWith("도면 순회 불가", vm.VerdictTitle);
        Assert.StartsWith("도면 순회 — 0mm 지점", vm.VerdictDetail);

        var ok = blocked with { Ok = true, Blocker = null, MinMarginDeg = 15.2, Limiting = "J5 관절 한계 근접(157.8°)" };
        vm.ApplyDetail(ThreeStages(0.8) with { Traversal = ok });
        Assert.DoesNotContain("도면 순회 불가", vm.VerdictTitle);
        Assert.StartsWith("도면 순회 여유 15.2° (J5 관절 한계 근접(157.8°)) · 이격 400 mm · ±40° 포괄 근사.", vm.VerdictDetail);
        Assert.Equal("실제 경유점", ReachSimViewModel.TraversalText(ok with { TiltDeg = null }).Split(" · ")[^1].TrimEnd('.'));
    }

    [Fact]
    public void 최적화_결과는_순회_여유_확보를_먼저_보고_부족한_용접선을_알린다()
    {
        ReachStationCandidateDto C(int clear, int? safe, double? margin, double dist = 1.8) =>
            new(13.7, 2.5, 0, dist, 0, 0, 0.3, true, clear, 0, 4 - clear, 0.1, 0, safe, margin, safe is null ? null : 10);
        // 간섭 없음 수는 같아도(4 → 4) 여유 확보가 늘면(2 → 3) 옮길 영역
        Assert.True(ReachSimViewModel.IsImproved(C(4, 2, 1.0), C(4, 3, 1.5, 2.1)));
        Assert.False(ReachSimViewModel.IsImproved(C(4, 3, 12), C(4, 3, 15, 2.1)));   // 목표(10°) 넘는 여유는 같게 본다
        Assert.True(ReachSimViewModel.IsImproved(C(4, 3, 2), C(4, 3, 6, 2.1)));
        Assert.True(ReachSimViewModel.IsImproved(C(2, null, null), C(3, null, null)));   // 구서버 — 간섭 없음 수만

        var vm = Vm();
        var seams = new List<ReachOptimumSeamDto>
        {
            new(Guid.NewGuid(), 1, "Clear", "Clear", 100, 19, 10.9),
            new(Guid.NewGuid(), 4, "Unreachable", "Clear", 100, null, 1.5),
        };
        vm.ApplyOptima(new[] { new ReachStationOptimumDto(Guid.NewGuid(), "L2-R02C05", "F", 2, true, C(2, 1, 8.9), C(4, 3, 1.5, 2.1), 98, 25000, seams) });

        Assert.Equal("간섭 없이 도달 2 → 4 / 4 · 순회 여유 확보 1 → 3 · 옮길 영역 1개", vm.OptimizeSummary);
        Assert.Equal("4/0/0 (여유 3)", vm.Optima[0].Best);
        Assert.Contains("순회 여유 10° 미만 — 4번 1.5°", vm.OptimumDetail);
    }

    [Fact]
    public void 영역_분리안_요약과_적용_조건()
    {
        ReachStationCandidateDto C(double x, double y, double? h, double? m) =>
            new(x, y, 0, 2.0, 0, 0, 0.5, true, 2, 0, 0, 0.1, h, 2, m, 10);
        var g1 = new ReachSplitGroupDto(new() { Guid.NewGuid(), Guid.NewGuid() }, new() { 1, 5 }, C(13.88, 2.7, 0, 12.6), new double[4][],
            new() { new(Guid.NewGuid(), 1, "Clear", 19.3, null), new(Guid.NewGuid(), 5, "Clear", 12.6, null) });
        var g2 = g1 with { Seqs = new() { 4 }, Station = C(13.38, 1.88, 300, 10.6),
            Seams = new() { new(Guid.NewGuid(), 4, "Clear", 10.6, null) } };
        var plan = new ReachSplitPlanDto(Guid.NewGuid(), "L2-R02C05", "F", 2, 0, new(), 2772, 37741, new() { g1, g2 });

        var text = ReachSimViewModel.SplitText(plan);
        Assert.StartsWith("L2-R02C05: 2개 영역으로 나누는 안 — 모든 용접선 순회 여유 확보\n  1) 용접선 1(19°) 5(13°) · 정차 (13.880, 2.700, 0.000 rad) · 리프트 0 mm · 순회 최소 여유 12.6°", text);
        Assert.EndsWith("(판정 격자 2772칸, 38 s)", text);
        Assert.StartsWith("L2-R02C05: 나누지 않아도 됩니다", ReachSimViewModel.SplitText(plan with { Groups = new() { g1 } }));

        var vm = Vm();
        Assert.False(vm.CanApplySplit);
        vm.SplitPlan = plan;
        Assert.True(vm.CanApplySplit);
        vm.SplitPlan = plan with { Groups = new() { g1 } };
        Assert.False(vm.CanApplySplit);   // 묶음 1개 = 나눌 것 없음
    }

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

    private static ReachStationCandidateDto Cand(double x, double dist, int clear, int hit, int un, double lateral = 0, double yaw = 0, double? strokeMm = null) =>
        new(x, 0, 0, dist, lateral, yaw, 0.2, true, clear, hit, un, 0.1, strokeMm);

    private static List<ReachStationOptimumDto> Optima() => new()
    {
        new(Guid.NewGuid(), "L1-R01", "SL", 1, false, Cand(1, 0.57, 0, 0, 7, strokeMm: 0), Cand(1.8, 1.4, 7, 0, 0, strokeMm: 600), 90, 300),
        new(Guid.NewGuid(), "L1-R04", "SL", 1, false, Cand(1, 0.57, 0, 0, 4, strokeMm: 0), Cand(1, 0.57, 0, 0, 4, strokeMm: 0), 1, 5),
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
        Assert.Equal(600.0, set.Args[5]);   // 최적화된 영역 공통 리프트 높이(StrokeMm) 함께 저장
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
        Assert.Equal("clearance", call.Args[4]);   // tankId, level, wallCode, ct, branch, areaLift
        Assert.Empty(vm.Optima);   // 규칙이 바뀌면 최적화 결과는 버린다
    }
    [Fact]
    public async Task 리프트_영역_고정을_켜면_조사에_넘기고_요약에_손실을_보인다()
    {
        var api = DispatchProxy.Create<IAcsApiClient, RecordingApi>();
        var vm = new ReachSimViewModel(api);

        vm.AreaLift = true;
        await Task.Delay(50);
        var call = ((RecordingApi)(object)api).Calls.Last(c => c.Method == nameof(IAcsApiClient.GetReachSurveyAsync));
        Assert.Equal(true, call.Args[5]);

        ReachSurveySeamDto Seam(int seq, string v, string best, double? bestH) =>
            new(Guid.NewGuid(), seq, 1, 1.2, v, v == "Unreachable" ? null : 500, 0.02, 0, null, best, bestH);
        var area = new ReachSurveyAreaDto(Guid.NewGuid(), "L4-R03C01", "PU", 4, 0, 0, new List<ReachSurveySeamDto>
        {
            Seam(1, "Clear", "Clear", 500), Seam(2, "Unreachable", "Clear", 0), Seam(3, "Collision", "Collision", 500),
        }, FixedStrokeMm: 500, BestClear: 2, BestReachable: 3);

        Assert.Equal("\n리프트 영역 고정 — 용접선별 최적 대비 간섭 없이 도달 2 → 1 · 도달 3 → 2 (도달 불가 +1) · 손실 영역 1/1",
            ReachSimViewModel.LiftSummary(new[] { area }));
        Assert.Equal("", ReachSimViewModel.LiftSummary(new[] { area with { FixedStrokeMm = null } }));
    }

    private static AreaDto AreaOn(int level, string wall) =>
        new(Guid.NewGuid(), "CT1", wall, level, $"L{level}-{wall}", 0, 0, 1, 1, null, null, null, 0, 1);

    [Fact]
    public void 면_목록은_그_층에_영역이_있는_면만_보인다()
    {
        var vm = Vm();
        vm.SelectedLevel = 4;
        vm.SelectedWall = "PU";
        vm.ApplyAreaWalls(new[] { AreaOn(4, "PU"), AreaOn(4, "SM"), AreaOn(4, "T"), AreaOn(2, "SM"), AreaOn(2, "SL"), AreaOn(2, "A") });
        Assert.Equal(new[] { "PU", "SM", "T" }, vm.WallCodes);
        Assert.Equal(0, vm.WallIndex);

        vm.SelectedLevel = 2;   // PU 가 없는 층 — 첫 면으로 옮긴다
        Assert.Equal(new[] { "SL", "SM", "A" }, vm.WallCodes);
        Assert.Equal("SL", vm.SelectedWall);

        vm.SelectedWall = "SM";
        vm.SelectedLevel = 4;   // 두 층에 다 있는 면은 그대로 둔다
        Assert.Equal("SM", vm.SelectedWall);
        Assert.Equal(1, vm.WallIndex);

        vm.SelectedLevel = 3;   // 영역이 하나도 없는 층은 모든 면
        Assert.Equal(ReachSimViewModel.WallOrder, vm.WallCodes);
    }
}
