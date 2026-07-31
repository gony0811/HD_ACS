using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>SeamSlicer 단위 테스트 [PHASE2 §3 / §7 수용기준]. 도면 좌표계 순수계산.</summary>
public class SeamSlicerTests
{
    private static readonly SlicerConfig Cfg = new(
        CobotReachM: 1.0, OverlapM: 0.2, StandoffM: 0.4, StationThetaOffset: 0.0, MergeDistM: 0.3);

    private static SeamInput Line((double, double, double) a, (double, double, double) b,
        (double, double, double) normal, string tank = "CT1", int level = 2, string wall = "W03") =>
        new("S1", tank, level, wall, "LINE", new[] { a, b }, normal, "DXF-1", "PROF-1");

    /// <summary>L=3.2, reach=1.0, overlap=0.2 → n=ceil(3.2/0.8)=4 구간. 간격 0.8&gt;병합0.3 → 4 스테이션.</summary>
    [Fact]
    public void Line_SlicesInto4Segments()
    {
        var seam = Line((0, 0, 0), (3.2, 0, 0), (0, 1, 0));

        var stations = SeamSlicer.Slice(new[] { seam }, Cfg);

        Assert.Equal(4, stations.Count());
        Assert.Equal(4, stations.Sum(s => s.Tasks.Count));
        // 각 스테이션 1개 TASK, seqInGroup=1
        Assert.All(stations, s => Assert.Equal(1, s.Tasks.Count));
        Assert.All(stations, s => Assert.Equal(1, s.Tasks[0].SeqInGroup));
        // 첫 구간: (0,0,0)→(0.8,0,0)
        var t0 = stations[0].Tasks[0];
        Assert.Equal(0.0, t0.SeamStartDrawing.X, 9);
        Assert.Equal(0.8, t0.SeamEndDrawing.X, 9);
        // 스테이션 = 구간 중점 + 법선(0,1)×standoff(0.4)
        Assert.Equal(0.4, stations[0].StationDrawing.X, 9);   // mid x=0.4
        Assert.Equal(0.4, stations[0].StationDrawing.Y, 9);   // +0.4 법선
        // 정차 방향 = 벽면(−법선) 향함 = atan2(-1,0) = −π/2
        Assert.Equal(-Math.PI / 2, stations[0].StationDrawing.Theta, 9);
        Assert.Equal("CT1-L2-W03-ST01", stations[0].AnchorGroupId);
    }

    /// <summary>정차점이 병합거리(0.3m) 이내인 두 seam → 한 스테이션 병합, anchorGroupId 공유, seqInGroup 1·2.</summary>
    [Fact]
    public void NearSeams_MergeIntoOneStation()
    {
        // 각 seam 길이 0.5 → n=ceil(0.5/0.8)=1. 정차점 y=0.4 vs 0.45 (거리 0.05 < 0.3) → 병합
        var s1 = new SeamInput("A", "CT1", 2, "W03", "LINE",
            new[] { ((double)0, (double)0, (double)0), (0.5, 0, 0) }, (0, 1, 0), "DXF-A", "PROF");
        var s2 = new SeamInput("B", "CT1", 2, "W03", "LINE",
            new[] { ((double)0, 0.05, (double)0), (0.5, 0.05, 0) }, (0, 1, 0), "DXF-B", "PROF");

        var stations = SeamSlicer.Slice(new[] { s1, s2 }, Cfg);

        Assert.Single(stations);
        var st = stations[0];
        Assert.Equal(2, st.Tasks.Count);
        Assert.All(st.Tasks, t => Assert.Equal(st.AnchorGroupId, t.AnchorGroupId));
        Assert.Equal(new[] { 1, 2 }, st.Tasks.Select(t => t.SeqInGroup).ToArray());
        // 서로 다른 seam이 같은 앵커그룹에 배정됨
        Assert.Equal(new[] { "A", "B" }, st.Tasks.Select(t => t.SeamId).ToArray());
    }

    /// <summary>POLYLINE — 호길이 기준 분할. 총길이 2.0, step 0.8 → n=ceil(2.5)=3. 코너 넘어 샘플링.</summary>
    [Fact]
    public void Polyline_SlicesByArcLength()
    {
        var seam = new SeamInput("P", "CT1", 2, "W05", "POLYLINE",
            new[] { ((double)0, (double)0, (double)0), (1, 0, 0), (1, 1, 0) }, (0, 1, 0), "DXF-P", "PROF");

        var stations = SeamSlicer.Slice(new[] { seam }, Cfg);

        Assert.Equal(3, stations.Sum(s => s.Tasks.Count));
        var tasks = stations.SelectMany(s => s.Tasks).OrderBy(t => t.SeqInGroup).ToList();
        // 첫 구간 시작 = 폴리라인 시작 (0,0)
        Assert.Equal(0.0, tasks.First().SeamStartDrawing.X, 9);
        Assert.Equal(0.0, tasks.First().SeamStartDrawing.Y, 9);
        // 마지막 구간 끝 = 폴리라인 끝 (1,1)
        Assert.Equal(1.0, tasks.Last().SeamEndDrawing.X, 9);
        Assert.Equal(1.0, tasks.Last().SeamEndDrawing.Y, 9);
        // 중간 구간(task1)은 코너(1,0)를 넘어 수직 구간으로 진입: 시작 (0.667,0), 끝 (1, 0.333)
        var mid = tasks[1];
        Assert.Equal(2.0 / 3.0, mid.SeamStartDrawing.X, 6);
        Assert.Equal(0.0, mid.SeamStartDrawing.Y, 6);
        Assert.Equal(1.0, mid.SeamEndDrawing.X, 6);
        Assert.Equal(1.0 / 3.0, mid.SeamEndDrawing.Y, 6);
    }

    /// <summary>reach ≤ overlap → step≤0 이면 예외.</summary>
    [Fact]
    public void InvalidConfig_Throws()
    {
        var bad = Cfg with { CobotReachM = 0.2, OverlapM = 0.2 };
        Assert.Throws<ArgumentException>(() => SeamSlicer.Slice(
            new[] { Line((0, 0, 0), (1, 0, 0), (0, 1, 0)) }, bad));
    }
}
