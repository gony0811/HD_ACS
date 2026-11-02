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
