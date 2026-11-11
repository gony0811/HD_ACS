using System.Globalization;
using HD.Acs.App.Services;
using HD.Acs.Core.Reach;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 현장 실적 백테스트 — 2026-11-11~12 천장(T) 검사 168건(정차·리프트·용접선·AMR 결과)을 운영 설정의 시뮬레이터로 다시 푼다.
/// 자료: Data/t_reach_20261111.csv (시각, jobRef, 유형, 결과[OK·FLANGE·PRECHECK·REACH·SENSOR·SKIP], 정차 맵 x·y·θ, 리프트, 용접선 시작 x·y·z, 끝 x·y).
/// 좌표는 맵 프레임 그대로 쓴다(천장 한 면만 보므로 도면 변환이 필요 없다 — z 는 층 바닥 기준).
/// </summary>
public class ReachFieldBacktestTests
{
    public sealed record Row(string Time, string Job, string SeamType, string Outcome, ReachStation Station, double LiftMm,
        double[] Start, double[] End);

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HD.Acs.sln"))) dir = dir.Parent;
        // 별도 출력 폴더(-o)로 돌려도 저장소를 찾도록 환경 변수 폴백
        var root = dir?.FullName ?? Environment.GetEnvironmentVariable("HDACS_SRC")
                   ?? throw new InvalidOperationException("src 폴더를 찾지 못했습니다(HDACS_SRC 지정).");
        return Path.Combine(root, relative);
    }

    public static List<Row> Load()
    {
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        return File.ReadAllLines(Find(Path.Combine("HD.Acs.App.Tests", "Data", "t_reach_20261111.csv")))
            .Where(l => l.Length > 0).Select(l => l.Split(','))
            .Select(c => new Row(c[0], c[1], c[2], c[3], new ReachStation(D(c[4]), D(c[5]), D(c[6]), 0), D(c[7]),
                new[] { D(c[8]), D(c[9]), D(c[10]) }, new[] { D(c[11]), D(c[12]), D(c[10]) }))
            .ToList();
    }

    public static ReachRobotModel FieldModel()
    {
        var m = ReachSimService.ModelFrom(
            new ConfigurationBuilder().AddJsonFile(Find(Path.Combine("HD.Acs.App", "appsettings.json"))).Build());
        return double.TryParse(Environment.GetEnvironmentVariable("HDACS_BT_ELBOW"), NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
            ? m with { MinElbowBendDeg = e } : m;   // 기준값을 바꿔 가며 볼 때만
    }

    /// <summary>판정 자세·순회 전체에서 팔꿈치가 가장 펴진 각도(|J3| 최소) — 팔 뻗음 기준을 정할 때 본다.</summary>
    public static double MinElbow(ReachRobotModel m, Row r)
    {
        var free = m with { MinElbowBendDeg = 0, MinFlangeReachMm = 0 };
        var planes = new[] { new ReachPlane("T", new[] { 0.0, 0, r.Start[2] }, CeilingNormal) };
        var mid = new[] { (r.Start[0] + r.End[0]) / 2, (r.Start[1] + r.End[1]) / 2, r.Start[2] };
        var min = double.PositiveInfinity;
        double[]? seed = null, startSeed = null;
        var pts = new[] { r.Start, mid, r.End };
        for (var i = 0; i < pts.Length; i++)
        {
            var e = ReachSimulator.Evaluate(free, planes, r.Station, pts[i], CeilingNormal, r.LiftMm, withGeometry: false,
                wallCode: i == 0 ? "T" : null, checkEntry: false, seedJoints: seed);
            foreach (var s in e.JudgedStages) if (s.Chosen is { } c) min = Math.Min(min, Math.Abs(c.JointsDeg[2]));
            seed = ReachSimulator.ScanSeed(e);
            if (i == 0) startSeed = ReachSimulator.TraverseSeed(e, free);
            if (seed is null) break;
        }
        if (startSeed is not null && ReachSimulator.TraverseSeam(free, r.Station, r.LiftMm, pts, CeilingNormal, startSeed) is { } t)
            foreach (var p in t.Points) min = Math.Min(min, Math.Abs(p.JointsDeg[2]));
        return min;
    }

    private static readonly double[] CeilingNormal = { 0, 0, -1 };

    public static ReachSeamSurvey Judge(ReachRobotModel m, Row r)
    {
        var planes = new[] { new ReachPlane("T", new[] { 0.0, 0, r.Start[2] }, CeilingNormal) };
        var mid = new[] { (r.Start[0] + r.End[0]) / 2, (r.Start[1] + r.End[1]) / 2, r.Start[2] };
        return ReachSimulator.Survey(m, planes, new[] { r.Station }, new[] { r.Start, mid, r.End }, CeilingNormal,
            new[] { r.LiftMm }, "T");
    }

    /// <summary>진단 출력 — HDACS_BACKTEST_OUT 을 주면 건별 판정을 그 파일에 쓴다(기준값을 정할 때 본다).</summary>
    [Fact]
    public void FieldRows_AreJudgedLikeTheRobot()
    {
        var m = FieldModel();
        var rows = Load().Where(r => r.Outcome is "OK" or "FLANGE" or "PRECHECK" or "REACH").ToList();
        var lines = new List<string>();
        int okPass = 0, ok = 0, failCaught = 0, fail = 0;
        foreach (var r in rows)
        {
            var s = Judge(m, r);
            var reachable = s.Verdict != ReachVerdict.Unreachable;
            if (r.Outcome == "OK") { ok++; if (reachable) okPass++; }
            else { fail++; if (!reachable) failCaught++; }
            lines.Add(string.Join(",", r.Time, r.Job, r.SeamType, r.Outcome, r.LiftMm.ToString(CultureInfo.InvariantCulture),
                Math.Max(Math.Sqrt(Math.Pow(r.Start[0] - r.Station.X, 2) + Math.Pow(r.Start[1] - r.Station.Y, 2)),
                    Math.Sqrt(Math.Pow(r.End[0] - r.Station.X, 2) + Math.Pow(r.End[1] - r.Station.Y, 2))).ToString("0.00", CultureInfo.InvariantCulture),
                MinElbow(m, r).ToString("0.0", CultureInfo.InvariantCulture), s.Verdict, s.TraverseMarginDeg?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
                (s.Blocker ?? "").Replace(',', ';')));
        }
        if (Environment.GetEnvironmentVariable("HDACS_BACKTEST_OUT") is { Length: > 0 } path)
            File.WriteAllLines(path, lines.Prepend($"# 성공 {okPass}/{ok} 가능 판정 · 실패 {failCaught}/{fail} 불가 판정"));

        // AMR 이 리프트 100 에서 거부한 52건(플랜지 뻗음 342 mm < 350 mm)은 전부 불가로 판정해야 한다
        Assert.All(rows.Where(r => r.Outcome == "FLANGE"), r => Assert.Equal(ReachVerdict.Unreachable, Judge(m, r).Verdict));
        // 실제로 성공한 용접선을 불가로 막는 비율은 낮아야 하고(정차 후보를 잃는다), 실패는 절반 넘게 걸러야 한다
        // (팔 뻗음 상한 30°: 실패 17건의 |J3| 최소 = 6°×5 · 27~28°×9 · 42° · 101°×2, 성공 51건 = 28°×2 · 42°×2 · 54° 이상 47건)
        Assert.True(okPass >= ok * 0.9, $"성공 {ok}건 중 {okPass}건만 가능 판정");
        Assert.True(failCaught >= fail * 0.9, $"실패 {fail}건 중 {failCaught}건만 불가 판정");
    }
}
