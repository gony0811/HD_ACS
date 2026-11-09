using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 영역 재분할 — CT1 L2-R02C05(F 벽, 용접선 8개: v 5.22·5.58 두 줄 × u 360 mm 네 칸). 한 정차로는 오른쪽 끝(H04·H08)의
/// 순회 여유가 1.5° 에 그친다(정차 최적화 결과). 두 묶음으로 나누면 8개 모두 여유 10° 이상.
/// </summary>
public class AreaSplitPlannerTests
{
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };
    private static readonly ReachRobotModel Field = ReachRobotModel.Default with
    {
        MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, ScanStandoffMm = 400,
        StartJointsByWall = new Dictionary<string, double[]> { ["F"] = Ready }, ScanTiltProfile = ReachTraversalBacktestTests.Line1Cor,
    };
    private const double FloorZ = 3.2175;
    private static readonly double[] Normal = { -1.0, 0, 0 };
    private static readonly ReachPlane[] Planes =
    {
        new("F", new[] { 15.48, 0, 0 }, Normal),
        new(ReachPlane.DeckCode, new[] { 0.0, 0, FloorZ }, new[] { 0.0, 0, 1 }),
    };
    private static double[] F(double u, double v) => new[] { 15.48, 8.64 - u, v };
    private static readonly double[] Strokes = Enumerable.Range(0, 11).Select(i => i * 100.0).ToArray();

    private static (List<IReadOnlyList<double[]>> Seams, List<IReadOnlyList<double[]>> Uv) R02C05()
    {
        var seams = new List<IReadOnlyList<double[]>>();
        var uv = new List<IReadOnlyList<double[]>>();
        foreach (var v in new[] { 5.22, 5.58 })
            for (var k = 0; k < 4; k++)
            {
                var u0 = 5.7599 + 0.36 * k;
                seams.Add(new[] { F(u0, v), F(u0 + 0.18, v), F(u0 + 0.36, v) });
                uv.Add(new[] { new[] { u0, v }, new[] { u0 + 0.36, v } });
            }
        return (seams, uv);
    }

    private static StationSearchSpace Space()
    {
        var c = F(6.4799, 5.4);
        return new StationSearchSpace(new[] { c[0], c[1] }, new[] { -1.0, 0 }, 0, FloorZ, 1.755, -0.32, -1.7);
    }

    [Fact]
    public void 실기_R02C05_는_두_묶음으로_나누면_8개_모두_순회_여유를_확보한다()
    {
        var (seams, uv) = R02C05();
        var p = AreaSplitPlanner.Plan(Field, Planes, Space(), seams, uv, 1.44, 1.44, Normal, Strokes, "F");

        Assert.Equal(2, p.Groups.Count);
        Assert.Equal(0, p.Unsafe);
        Assert.Empty(p.Hopeless);
        Assert.Equal(Enumerable.Range(0, 8), p.Groups.SelectMany(g => g.Seams).OrderBy(i => i));   // 모든 용접선이 정확히 한 묶음에
        Assert.All(p.Groups, g =>
        {
            Assert.Equal(g.Seams.Count, g.Best.SafeClear);
            Assert.True(g.Best.TraverseMarginDeg >= Field.TraverseMarginTargetDeg, $"{g.Best.TraverseMarginDeg:0.0}");
        });
        // u 방향으로 자른다 — 한 묶음은 같은 열(두 줄)을 함께 갖는다.
        Assert.Contains(p.Groups, g => g.Seams.SequenceEqual(new[] { 0, 4 }));
    }

    [Fact]
    public void 지금_정차가_이미_최상이면_정차_최적화는_탐색하지_않는다()
    {
        // 분리안의 왼쪽 묶음(H01·H05)을 그 묶음 정차로 — 두 용접선 모두 여유 확보. 최상 점수면 후보를 더 보지 않는다(평가 1곳).
        var (seams, uv) = R02C05();
        var sub = new List<IReadOnlyList<double[]>> { seams[0], seams[4] };
        var first = StationOptimizer.Optimize(Field, Planes, Space() with { CenterXY = new[] { 15.48, 2.70 } }, sub, Normal, Strokes, wallCode: "F");
        var best = first.Best;
        var again = StationOptimizer.Optimize(Field, Planes,
            Space() with { CenterXY = new[] { 15.48, 2.70 }, BaselineDistM = best.DistM, BaselineLateralM = best.LateralM, BaselineYawDeg = best.YawDeg },
            sub, Normal, Strokes, wallCode: "F");

        // 면 간섭 여유는 초점 자세에서 툴 끝–면 98.5 mm 가 최대 — 그 값이면 더 나은 후보가 없다.
        Assert.Equal(0.0985, StationOptimizer.RobustnessBound(Field), 4);
        Assert.True(StationOptimizer.IsTop(again.Baseline, 2, Field.TraverseMarginTargetDeg, StationOptimizer.RobustnessBound(Field)));
        Assert.Equal(1, again.Evaluated);
        Assert.Same(again.Baseline, again.Best);
        Assert.True(first.Evaluated > 1);
        Assert.Equal(StationOptimizer.Score(best.Seams, 10), StationOptimizer.Score(again.Best.Seams, 10));
    }

    [Fact]
    public void 최상_점수_판정()
    {
        StationCandidate C(int safe, double? margin, double robust, bool feasible = true) =>
            new(1, 0, 0, new ReachStation(0, 0, 0, 0), 0.3, feasible, safe, 0, 0, robust, Array.Empty<ReachSeamSurvey>(), 0, safe, margin, 10);
        Assert.True(StationOptimizer.IsTop(C(4, 12, 0.15), 4, 10));
        Assert.True(StationOptimizer.IsTop(C(4, null, 0.15), 4, 10));     // 순회 판정 없음 = 목표로 본다
        Assert.False(StationOptimizer.IsTop(C(3, 12, 0.15), 4, 10));     // 여유 확보 못 한 용접선
        Assert.False(StationOptimizer.IsTop(C(4, 9.6, 0.15), 4, 10));    // 최소 여유가 목표 미만(0.5° 단위 9.5)
        Assert.False(StationOptimizer.IsTop(C(4, 12, 0.12), 4, 10));     // 면 간섭 여유가 상한 미만 — 더 나은 후보가 있을 수 있다
        Assert.True(StationOptimizer.IsTop(C(4, 12, 0.0985), 4, 10, 0.0985));   // 기구상 최대 여유(초점 자세 툴 끝–면)면 최상
        Assert.False(StationOptimizer.IsTop(C(4, 12, 0.15, feasible: false), 4, 10));
    }

    [Fact]
    public void 한_정차로_충분하면_나누지_않는다()
    {
        // 왼쪽 두 칸(H01·H02·H05·H06)만 — 한 정차로 모두 여유 확보.
        var (seams, uv) = R02C05();
        int[] keep = { 0, 1, 4, 5 };
        var p = AreaSplitPlanner.Plan(Field, Planes, Space(), keep.Select(i => seams[i]).ToList(), keep.Select(i => uv[i]).ToList(),
            1.44, 1.44, Normal, Strokes, "F", refine: false);

        var g = Assert.Single(p.Groups);
        Assert.Equal(4, g.Best.SafeClear);
    }

    [Fact]
    public void 묶음은_AREA_최대_크기를_넘지_않는다()
    {
        // 폭 상한 0.5 m — 360 mm 칸 하나씩만 담을 수 있다.
        var (seams, uv) = R02C05();
        int[] keep = { 0, 1 };
        var p = AreaSplitPlanner.Plan(Field, Planes, Space(), keep.Select(i => seams[i]).ToList(), keep.Select(i => uv[i]).ToList(),
            0.5, 1.44, Normal, Strokes, "F", refine: false);
        Assert.Equal(2, p.Groups.Count);
    }
}
