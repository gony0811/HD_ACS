using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>정차 묶음 최적화(탐욕 set cover) — 창(영역 크기 상한)·덮기·탐욕 순서.</summary>
public class StationSetCoverTests
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

    [Fact]
    public void 창은_상한_안에서_가장_많은_용접선을_담는다()
    {
        // 가로 0.36 m 용접선 5개가 한 줄(u 0~1.8) — 창 1.44 m 에는 4개
        var box = Enumerable.Range(0, 5).Select(k => (U0: 0.36 * k, V0: 1.0, U1: 0.36 * (k + 1), V1: 1.0)).ToList();
        var w = StationSetCover.BestWindow(Enumerable.Range(0, 5).ToList(), box, 1.44, 1.44);
        Assert.Equal(4, w.Seams.Count);
        Assert.Equal(0, w.U0, 6);

        // 세로로 떨어진 두 줄(v 1.0·3.0) — 창 세로 1.44 m 면 한 줄만, 2.9 m 면 둘 다
        var two = box.Take(2).Concat(box.Take(2).Select(b => b with { V0 = 3.0, V1 = 3.0 })).ToList();
        Assert.Equal(2, StationSetCover.BestWindow(Enumerable.Range(0, 4).ToList(), two, 1.44, 1.44).Seams.Count);
        Assert.Equal(4, StationSetCover.BestWindow(Enumerable.Range(0, 4).ToList(), two, 1.44, 2.9).Seams.Count);

        // 창보다 큰 용접선은 하나씩
        var big = new List<(double, double, double, double)> { (0, 0, 2.0, 0) };
        Assert.Single(StationSetCover.BestWindow(new List<int> { 0 }, big, 1.44, 1.44).Seams);
    }

    [Fact]
    public void 리프트_후보는_양_끝_포함_고르게_줄인다()
    {
        var all = Enumerable.Range(0, 11).Select(i => i * 100.0).ToList();
        Assert.Equal(new[] { 0.0, 300, 700, 1000 }, StationSetCover.Thin(all, 4));
        Assert.Equal(new[] { 0.0 }, StationSetCover.Thin(new[] { 0.0 }, 4));
    }

    [Fact]
    public void 실기_F_벽_두_줄_8개를_창_상한_안에서_덮는다()
    {
        // CT1 L2-R02C05 꼴(v 5.22·5.58 두 줄 × 네 칸) + 오른쪽으로 1.8 m 떨어진 네 칸 한 줄 — 창 하나(1.44 m)로는 못 담는다
        var seams = new List<IReadOnlyList<double[]>>();
        var uv = new List<IReadOnlyList<double[]>>();
        void Add(double u0, double v)
        {
            seams.Add(new[] { F(u0, v), F(u0 + 0.18, v), F(u0 + 0.36, v) });
            uv.Add(new[] { new[] { u0, v }, new[] { u0 + 0.36, v } });
        }
        foreach (var v in new[] { 5.22, 5.58 })
            for (var k = 0; k < 4; k++) Add(5.7599 + 0.36 * k, v);
        for (var k = 0; k < 4; k++) Add(7.92 + 0.36 * k, 5.22);
        var c = F(6.9, 5.4);
        var space = new StationSearchSpace(new[] { c[0], c[1] }, new[] { -1.0, 0 }, 0, FloorZ, 0);
        var plan = StationSetCover.Plan(Field, Planes, space, seams, uv, 1.44, 1.44, Normal, new[] { 0.0, 500 }, "F",
            new SetCoverOptions(GridStepM: 0.4));

        Assert.True(plan.Stations.Count >= 2);                         // 창 상한 때문에 정차 하나로는 안 된다
        var all = plan.Stations.SelectMany(s => s.Seams).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());                 // 용접선은 한 정차에만
        Assert.Equal(seams.Count, all.Count + plan.Uncovered.Count);     // 덮은 것 + 못 덮은 것 = 전부
        Assert.All(plan.Stations, s =>
        {
            Assert.True(s.Window[2] - s.Window[0] <= 1.44 + 1e-9);
            foreach (var i in s.Seams)                                    // 맡은 용접선은 창 안
                Assert.All(uv[i], p => Assert.InRange(p[0], s.Window[0] - 1e-6, s.Window[2] + 1e-6));
            Assert.All(s.Surveys, r => Assert.Equal(ReachVerdict.Clear, r.Verdict));
        });
        // 탐욕 — 앞에 고른 정차가 더 많이(또는 같게) 덮는다(같은 단계 안)
        var safe = plan.Stations.Where(s => s.Tier == "Safe").Select(s => s.Seams.Count).ToList();
        Assert.Equal(safe.OrderByDescending(x => x), safe);
        Assert.True(plan.Evaluations > 0 && plan.Cells > 0);
    }
}
