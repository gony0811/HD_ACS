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
        return Choose(strokesMm, table, model.TraverseMarginTargetDeg);
    }

    /// <summary>
    /// 정차 하나(<paramref name="station"/>)에서 영역 공통 리프트 높이를 빠르게 고른다 — 진입 경로 검사 전 판정으로
    /// 상한(최대 Clear·도달 수)을 먼저 구해 더 나아질 수 없는 리프트 높이는 진입 경로·도면 순회를 풀지 않는다.
    /// 순위는 여유 확보 간섭 없음(도면 순회 관절 여유 ≥ <see cref="ReachRobotModel.TraverseMarginTargetDeg"/>) → 간섭 없음 → 도달 → 순회 최소 여유 → 낮은 높이.
    /// 고른 높이와 그 높이에서의 용접선 판정은 <see cref="Evaluate"/> 의 <c>StrokeMm</c>·<c>Fixed</c> 와 같다.
    /// </summary>
    public static (double StrokeMm, IReadOnlyList<ReachSeamSurvey> Seams) EvaluateFixed(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm, string? wallCode = null)
    {
        if (strokesMm.Count == 0 || seams.Count == 0)
            return (strokesMm.Count > 0 ? strokesMm[0] : 0,
                seams.Select(_ => new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0)).ToArray());

        var pre = new (bool Reach, double MinCl, string? Blocker, double[]? Seed)[seams.Count][];
        Parallel.For(0, seams.Count, i =>
        {
            var row = new (bool Reach, double MinCl, string? Blocker, double[]? Seed)[strokesMm.Count];
            var pts = seams[i];
            for (var k = 0; k < strokesMm.Count; k++)
            {
                var h = strokesMm[k];
                var min = double.PositiveInfinity;
                var reach = true;
                string? blocker = null, entryBlocker = null;
                double[]? seed = null, startSeed = null;
                for (var p = 0; p < pts.Count; p++)
                {
                    var r = ReachSimulator.Evaluate(model, planes, station, pts[p], wallNormal, h, withGeometry: false,
                        wallCode: p == 0 ? wallCode : null, checkEntry: false, seedJoints: seed);
                    seed = ReachSimulator.ScanSeed(r);
                    if (p == 0) startSeed = ReachSimulator.TraverseSeed(r);
                    if (!r.Reachable)
                    {
                        reach = false;
                        blocker ??= r.Blocker;
                        if (r.JudgedStages.All(s => s.Chosen is { PostureOk: true })) entryBlocker ??= r.Blocker;
                        break;
                    }
                    min = Math.Min(min, r.MinClearanceM);
                }
                row[k] = (reach, min, entryBlocker ?? blocker, startSeed);
            }
            pre[i] = row;
        });

        var needEntry = ReachSimulator.StartJoints(model, wallCode) is not null;
        var ub = Enumerable.Range(0, strokesMm.Count).Select(k => (
            K: k,
            Clear: pre.Count(r => r[k].Reach && r[k].MinCl >= 0),
            Reach: pre.Count(r => r[k].Reach))).ToArray();

        // 순위 — 여유 확보 간섭 없음(도면 순회 관절 여유 ≥ 목표) > 간섭 없음 > 도달 > 순회 최소 여유(목표에서 자름) > 낮은 높이.
        var cap = model.TraverseMarginTargetDeg;
        var bestScore = (Safe: -1, Clear: -1, Reach: -1, Margin: double.NegativeInfinity);
        var bestStroke = double.PositiveInfinity;
        var bestRow = Array.Empty<ReachSeamSurvey>();
        bool Better((int Safe, int Clear, int Reach, double Margin) s, double h)
        {
            var c = StationOptimizer.CompareScore(s, bestScore);
            return c > 0 || (c == 0 && h < bestStroke);
        }

        foreach (var u in ub.OrderByDescending(x => x.Clear).ThenByDescending(x => x.Reach).ThenBy(x => strokesMm[x.K]))
        {
            var h = strokesMm[u.K];
            if (!Better((u.Clear, u.Clear, u.Reach, cap), h)) continue;
            int safeUb = u.Clear, clearUb = u.Clear, reachUb = u.Reach;
            var minMargin = double.PositiveInfinity;
            var row = new ReachSeamSurvey[seams.Count];
            var aborted = false;
            bool Lost(bool wasClear, bool clearStays)
            {
                if (!clearStays) { reachUb--; if (wasClear) clearUb--; }
                if (wasClear) safeUb--;
                return !Better((safeUb, clearUb, reachUb, cap), h);
            }
            for (var i = 0; i < seams.Count; i++)
            {
                var p = pre[i][u.K];
                if (!p.Reach)
                {
                    row[i] = new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, p.Blocker);
                    continue;
                }
                var wasClear = p.MinCl >= 0;
                if (needEntry && seams[i].Count > 0)
                {
                    var e = ReachSimulator.Evaluate(model, planes, station, seams[i][0], wallNormal, h, withGeometry: false, wallCode: wallCode);
                    if (!e.Reachable)
                    {
                        row[i] = new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, e.Blocker ?? p.Blocker);
                        if (Lost(wasClear, false)) { aborted = true; break; }
                        continue;
                    }
                }
                double? margin = null;
                if (ReachSimulator.TraverseSeam(model, station, h, seams[i], wallNormal, p.Seed) is { } t)
                {
                    if (!t.Ok)
                    {
                        row[i] = new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, t.Blocker);
                        if (Lost(wasClear, false)) { aborted = true; break; }
                        continue;
                    }
                    margin = t.MinMarginDeg;
                    minMargin = Math.Min(minMargin, t.MinMarginDeg);
                    if (wasClear && t.MinMarginDeg < cap && Lost(true, true)) { aborted = true; row[i] = Row(p, h, margin); break; }
                }
                row[i] = Row(p, h, margin);
            }
            if (aborted) continue;
            var score = (safeUb, clearUb, reachUb, StationOptimizer.MarginKey(double.IsPositiveInfinity(minMargin) ? null : minMargin, cap));
            if (Better(score, h))
            {
                bestScore = score;
                bestStroke = h;
                bestRow = row;
            }
        }
        return (bestStroke, bestRow);
    }

    private static ReachSeamSurvey Row((bool Reach, double MinCl, string? Blocker, double[]? Seed) p, double h, double? margin) =>
        new(p.MinCl >= 0 ? ReachVerdict.Clear : ReachVerdict.Collision, h, p.MinCl, 0, TraverseMarginDeg: margin);

    /// <summary>
    /// 높이 × 용접선 판정표(<paramref name="table"/>[용접선][높이])에서 영역 공통 높이를 고른다 — <see cref="EvaluateFixed"/> 와 같은 순위
    /// (여유 확보 간섭 없음 → 간섭 없음 → 도달 → 순회 최소 여유 → 낮은 높이, <see cref="StationOptimizer.Score"/>).
    /// </summary>
    public static AreaLiftResult Choose(IReadOnlyList<double> strokesMm, IReadOnlyList<IReadOnlyList<ReachSeamSurvey>> table,
        double marginTargetDeg = ReachRobotModel.DefaultTraverseMarginTargetDeg)
    {
        var best = table.Select(BestOf).ToList();
        if (strokesMm.Count == 0) return new AreaLiftResult(0, best, best);
        var pick = 0;
        var top = StationOptimizer.Score(table.Select(r => r[0]).ToList(), marginTargetDeg);
        for (var k = 1; k < strokesMm.Count; k++)
        {
            var s = StationOptimizer.Score(table.Select(r => r[k]).ToList(), marginTargetDeg);
            var c = StationOptimizer.CompareScore(s, top);
            if (c > 0 || (c == 0 && strokesMm[k] < strokesMm[pick])) { pick = k; top = s; }
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
