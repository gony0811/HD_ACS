namespace HD.Acs.Core.Reach;

/// <summary>
/// 영역 하나의 리프트 고정 판정 — <paramref name="StrokeMm"/> = 고른 고정 높이, <paramref name="Fixed"/> = 그 높이에서 용접선 판정,
/// <paramref name="Best"/> = 용접선마다 가장 좋은 높이를 따로 고른 판정(비교 기준). 순서는 입력 용접선 순서.
/// </summary>
public sealed record AreaLiftResult(double StrokeMm, IReadOnlyList<ReachSeamSurvey> Fixed, IReadOnlyList<ReachSeamSurvey> Best)
{
    public int FixedClear => Fixed.Count(s => s.Verdict == ReachVerdict.Clear);
    public int FixedReachable => Fixed.Count(s => s.Verdict != ReachVerdict.Unreachable);
    public int BestClear => Best.Count(s => s.Verdict == ReachVerdict.Clear);
    public int BestReachable => Best.Count(s => s.Verdict != ReachVerdict.Unreachable);
}

/// <summary>
/// 영역 안에서 텔레스코픽 리프트를 한 높이로 고정할 때의 판정. 용접선마다 리프트 후보 높이 하나씩으로
/// <see cref="ReachSimulator.Survey"/> 를 풀어(높이 × 용접선 판정표) 영역 공통 높이를 고른다 —
/// 간섭 없이 닿는 용접선 수 최대 → 닿는 용접선 수 최대 → 낮은 높이. 같은 표에서 용접선별 최적(종전 조사와 같은 판정)도 낸다.
/// </summary>
public static class AreaLift
{
    public static AreaLiftResult Evaluate(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, IReadOnlyList<ReachStation> stations,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm, string? wallCode = null)
    {
        var table = new ReachSeamSurvey[seams.Count][];
        Parallel.For(0, seams.Count, i =>
            table[i] = strokesMm.Select(h => ReachSimulator.Survey(model, planes, stations, seams[i], wallNormal, new[] { h }, wallCode)).ToArray());
        return Choose(strokesMm, table);
    }

    /// <summary>
    /// 정차 하나(<paramref name="station"/>)에서 영역 공통 리프트 높이를 빠르게 고른다 — 진입 경로 검사 전 판정으로
    /// 상한(최대 Clear·도달 수)을 먼저 구해 더 나아질 수 없는 리프트 높이는 진입 경로를 풀지 않는다.
    /// 고른 높이와 그 높이에서의 용접선 판정은 <see cref="Evaluate"/> 의 <c>StrokeMm</c>·<c>Fixed</c> 와 같다.
    /// </summary>
    public static (double StrokeMm, IReadOnlyList<ReachSeamSurvey> Seams) EvaluateFixed(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm, string? wallCode = null)
    {
        if (strokesMm.Count == 0 || seams.Count == 0)
            return (strokesMm.Count > 0 ? strokesMm[0] : 0,
                seams.Select(_ => new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0)).ToArray());

        var pre = new (bool Reach, double MinCl, string? Blocker)[seams.Count][];
        Parallel.For(0, seams.Count, i =>
        {
            var row = new (bool Reach, double MinCl, string? Blocker)[strokesMm.Count];
            var pts = seams[i];
            for (var k = 0; k < strokesMm.Count; k++)
            {
                var h = strokesMm[k];
                var min = double.PositiveInfinity;
                var reach = true;
                string? blocker = null, entryBlocker = null;
                double[]? seed = null;
                for (var p = 0; p < pts.Count; p++)
                {
                    var r = ReachSimulator.Evaluate(model, planes, station, pts[p], wallNormal, h, withGeometry: false,
                        wallCode: p == 0 ? wallCode : null, checkEntry: false, seedJoints: seed);
                    seed = ReachSimulator.ScanSeed(r);
                    if (!r.Reachable)
                    {
                        reach = false;
                        blocker ??= r.Blocker;
                        if (r.JudgedStages.All(s => s.Chosen is { PostureOk: true })) entryBlocker ??= r.Blocker;
                        break;
                    }
                    min = Math.Min(min, r.MinClearanceM);
                }
                row[k] = (reach, min, entryBlocker ?? blocker);
            }
            pre[i] = row;
        });

        var needEntry = ReachSimulator.StartJoints(model, wallCode) is not null;
        var ub = Enumerable.Range(0, strokesMm.Count).Select(k => (
            K: k,
            Clear: pre.Count(r => r[k].Reach && r[k].MinCl >= 0),
            Reach: pre.Count(r => r[k].Reach))).ToArray();

        var bestScore = (Clear: -1, Reach: -1);
        var bestStroke = double.PositiveInfinity;
        var bestRow = Array.Empty<ReachSeamSurvey>();
        bool Better(int c, int r, double h) =>
            c > bestScore.Clear || (c == bestScore.Clear && r > bestScore.Reach)
            || ((c, r) == bestScore && h < bestStroke);

        foreach (var u in ub.OrderByDescending(x => x.Clear).ThenByDescending(x => x.Reach).ThenBy(x => strokesMm[x.K]))
        {
            var h = strokesMm[u.K];
            if (!Better(u.Clear, u.Reach, h)) continue;
            int remClear = u.Clear, remReach = u.Reach;
            var row = new ReachSeamSurvey[seams.Count];
            var aborted = false;
            for (var i = 0; i < seams.Count; i++)
            {
                var p = pre[i][u.K];
                if (!p.Reach)
                {
                    row[i] = new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, p.Blocker);
                    continue;
                }
                if (needEntry && seams[i].Count > 0)
                {
                    var e = ReachSimulator.Evaluate(model, planes, station, seams[i][0], wallNormal, h, withGeometry: false, wallCode: wallCode);
                    if (!e.Reachable)
                    {
                        row[i] = new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, e.Blocker ?? p.Blocker);
                        remReach--;
                        if (p.MinCl >= 0) remClear--;
                        if (!Better(remClear, remReach, h)) { aborted = true; break; }
                        continue;
                    }
                }
                row[i] = new ReachSeamSurvey(p.MinCl >= 0 ? ReachVerdict.Clear : ReachVerdict.Collision, h, p.MinCl, 0);
            }
            if (!aborted && Better(remClear, remReach, h))
            {
                bestScore = (remClear, remReach);
                bestStroke = h;
                bestRow = row;
            }
        }
        return (bestStroke, bestRow);
    }

    /// <summary>높이 × 용접선 판정표(<paramref name="table"/>[용접선][높이])에서 영역 공통 높이를 고른다.</summary>
    public static AreaLiftResult Choose(IReadOnlyList<double> strokesMm, IReadOnlyList<IReadOnlyList<ReachSeamSurvey>> table)
    {
        var best = table.Select(BestOf).ToList();
        if (strokesMm.Count == 0) return new AreaLiftResult(0, best, best);
        var pick = 0;
        (int Clear, int Reach) Score(int k) =>
            (table.Count(r => r[k].Verdict == ReachVerdict.Clear), table.Count(r => r[k].Verdict != ReachVerdict.Unreachable));
        var top = Score(0);
        for (var k = 1; k < strokesMm.Count; k++)
        {
            var s = Score(k);
            var better = s.Clear > top.Clear || (s.Clear == top.Clear && s.Reach > top.Reach)
                         || (s == top && strokesMm[k] < strokesMm[pick]);
            if (better) { pick = k; top = s; }
        }
        return new AreaLiftResult(strokesMm[pick], table.Select(r => r[pick]).ToList(), best);
    }

    /// <summary>
    /// 높이별 판정 중 가장 좋은 것 — 간섭 없음(여유 큰 높이) → 간섭(덜한 높이) → 도달 불가(진입 사유 우선).
    /// 종전 조사(전 높이를 한 번에)의 판정과 같다.
    /// </summary>
    public static ReachSeamSurvey BestOf(IReadOnlyList<ReachSeamSurvey> byStroke)
    {
        var reach = byStroke.Where(s => s.Verdict != ReachVerdict.Unreachable)
            .OrderByDescending(s => s.Verdict == ReachVerdict.Clear).ThenByDescending(s => s.MinClearanceM ?? double.NegativeInfinity)
            .FirstOrDefault();
        if (reach is not null) return reach;
        var blocker = byStroke.Select(s => s.Blocker).FirstOrDefault(b => b is not null && IsEntryBlocker(b))
                      ?? byStroke.Select(s => s.Blocker).FirstOrDefault(b => b is not null);
        return new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, blocker);
    }

    private static bool IsEntryBlocker(string b) =>
        b.StartsWith("출발 자세", StringComparison.Ordinal) || b.StartsWith("진입 경로", StringComparison.Ordinal);
}
