namespace HD.Acs.Core.Reach;

/// <summary>영역 재분할 그룹 하나 — 용접선 번호(입력 순서)와 그 그룹을 맡을 정차(공통 리프트·판정 포함).</summary>
/// <param name="Seams">이 그룹 용접선 번호(입력 목록 기준, 오름차순).</param>
/// <param name="Best">정차·리프트·용접선별 판정(<see cref="StationCandidate.Seams"/> 는 <paramref name="Seams"/> 순서).</param>
public sealed record AreaSplitGroup(IReadOnlyList<int> Seams, StationCandidate Best);

/// <summary>
/// 영역 재분할 결과. <see cref="Groups"/> 가 1개면 분리하지 않아도 된다(한 정차가 최선).
/// <see cref="Hopeless"/> 는 판정 격자의 어느 정차·리프트에서도 순회 여유를 확보하지 못한 용접선 — 분할로 해결되지 않는다.
/// </summary>
public sealed record AreaSplitPlan(IReadOnlyList<AreaSplitGroup> Groups, IReadOnlyList<int> Hopeless, int Unsafe, int GridCells);

/// <summary>
/// 영역 재분할 — 한 정차로 영역의 모든 용접선에 도면 순회 관절 여유를 확보하지 못할 때, 용접선을 몇 묶음으로 나눠 묶음마다 정차를 둔다.
///
/// ① 판정표: 영역 주변 정차 격자(면 안쪽 거리 × 좌우 × 방향, <see cref="GridStepM"/> 간격) × 리프트(<see cref="GridStrokeStepMm"/> 간격)마다
///    용접선별로 운영 판정(<see cref="ReachSimulator.Survey"/> — 세 자세·진입·도면 순회)을 한 번 계산한다(용접선 병렬).
/// ② 분할 탐색: 용접선을 면 (u, v) 축 방향 직선으로 잘라 나누는 분할(재귀 이분, 최대 <c>maxGroups</c> 묶음)을 모두 보고
///    (여유 미확보 용접선 수 → 도달 못 하는 수 → 묶음 수 → 최소 순회 여유) 가 가장 좋은 분할을 고른다. 묶음 점수는 판정표에서
///    그 묶음에 가장 좋은 (정차, 리프트) — 묶음 전체가 한 정차·한 리프트를 쓴다(영역 리프트 고정과 같다). 묶음의 (u, v) 폭은 AREA 최대 크기 안.
///    어느 칸에서도 여유를 확보하지 못하는 용접선(<see cref="AreaSplitPlan.Hopeless"/>)은 미확보 수에서 뺀다 — 나눠도 해결되지 않으므로 묶음을 늘릴 이유가 아니다.
/// ③ 다듬기: 고른 묶음마다 그 묶음 중심에서 <see cref="StationOptimizer.Optimize"/> 를 돌려 격자보다 나은 정차로 바꾼다(격자 칸이 시작점).
/// </summary>
public static class AreaSplitPlanner
{
    public const double GridStepM = 0.2;
    public const double GridStrokeStepMm = 200;
    /// <summary>격자 좌우 범위 — 용접선이 퍼진 폭 + 이만큼.</summary>
    public const double GridLateralExtraM = 0.6;

    /// <param name="space">영역 단위 탐색 좌표(App 의 정차 최적화와 같다 — 중심·면 안쪽·정면 방향·층 바닥·탐색 폭).</param>
    /// <param name="seamUv">용접선마다 면 (u, v) 점들(시작·끝·가지) — 자르는 축 정렬과 묶음 크기 판정용.</param>
    /// <param name="maxSpanU">묶음 u 폭 상한 [m](AREA 최대 크기).</param>
    /// <param name="maxSpanV">묶음 v 폭 상한 [m].</param>
    /// <param name="refine">고른 묶음의 정차를 <see cref="StationOptimizer.Optimize"/> 로 다듬을지.</param>
    public static AreaSplitPlan Plan(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        IReadOnlyList<IReadOnlyList<double[]>> seams, IReadOnlyList<IReadOnlyList<double[]>> seamUv,
        double maxSpanU, double maxSpanV, double[] wallNormal, IReadOnlyList<double> strokesMm,
        string? wallCode = null, int maxGroups = 4, bool refine = true, CancellationToken ct = default)
    {
        var n = seams.Count;
        if (n == 0) return new AreaSplitPlan(Array.Empty<AreaSplitGroup>(), Array.Empty<int>(), 0, 0);
        if (n > 20) throw new ArgumentOutOfRangeException(nameof(seams), "영역 재분할은 용접선 20개까지만 다룬다.");
        var cap = model.TraverseMarginTargetDeg;

        // ── ① 판정표 ───────────────────────────────────────────────
        var cells = GridStations(model, planes, space, seams)
            .SelectMany(s => GridStrokes(strokesMm).Select(h => (s.Station, s.D, s.L, s.Y, H: h)))
            .ToList();
        var table = new ReachSeamSurvey[n][];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var row = new ReachSeamSurvey[cells.Count];
            for (var c = 0; c < cells.Count; c++)
                row[c] = ReachSimulator.Survey(model, planes, new[] { cells[c].Station }, seams[i], wallNormal, new[] { cells[c].H }, wallCode);
            table[i] = row;
        });
        bool Safe(ReachSeamSurvey s) => s.Verdict == ReachVerdict.Clear && (s.TraverseMarginDeg is not { } m || m >= cap);
        var hopeless = Enumerable.Range(0, n).Where(i => !table[i].Any(Safe)).ToHashSet();
        var unreachable = Enumerable.Range(0, n).Where(i => table[i].All(s => s.Verdict == ReachVerdict.Unreachable)).ToHashSet();

        // 묶음 점수 — 판정표에서 묶음 전체가 같은 칸(정차·리프트)을 쓸 때 가장 좋은 칸.
        var groupMemo = new Dictionary<int, (int Cell, (int Safe, int Clear, int Reach, double Margin) Score)>();
        (int Cell, (int Safe, int Clear, int Reach, double Margin) Score) Group(int mask)
        {
            if (groupMemo.TryGetValue(mask, out var hit)) return hit;
            var idx = Members(mask);
            var best = (Cell: -1, Score: (Safe: -1, Clear: -1, Reach: -1, Margin: double.NegativeInfinity));
            for (var c = 0; c < cells.Count; c++)
            {
                var s = StationOptimizer.Score(idx.Select(i => table[i][c]).ToList(), cap);
                if (StationOptimizer.CompareScore(s, best.Score) > 0) best = (c, s);
            }
            return groupMemo[mask] = best;
        }
        bool Fits(int mask)
        {
            var pts = Members(mask).SelectMany(i => seamUv[i]).ToList();
            return pts.Max(p => p[0]) - pts.Min(p => p[0]) <= maxSpanU + 1e-9 && pts.Max(p => p[1]) - pts.Min(p => p[1]) <= maxSpanV + 1e-9;
        }

        // ── ② 분할 탐색(재귀 이분, 묶음 수 상한) ───────────────────────
        // 점수 = (여유 미확보[가망 없는 용접선 제외], 도달 못 함[어디서도 못 닿는 용접선 제외], 묶음 수, −최소 여유) — 작을수록 좋다.
        var centers = Enumerable.Range(0, n).Select(i => (U: seamUv[i].Average(p => p[0]), V: seamUv[i].Average(p => p[1]))).ToArray();
        var memo = new Dictionary<(int, int), Part?>();
        Part? Best(int mask, int k)
        {
            if (memo.TryGetValue((mask, k), out var hit)) return hit;
            Part? best = null;
            if (Fits(mask))
            {
                var (cell, s) = Group(mask);
                var idx = Members(mask);
                var uns = idx.Count(i => !hopeless.Contains(i)) - Math.Min(s.Safe, idx.Count(i => !hopeless.Contains(i)));
                var unr = idx.Count(i => !unreachable.Contains(i)) - Math.Min(s.Reach, idx.Count(i => !unreachable.Contains(i)));
                best = new Part(new[] { (mask, cell) }, uns, unr, 1, s.Margin);
            }
            if (k > 1)
                foreach (var (a, b) in Cuts(mask, centers))
                    for (var ka = 1; ka < k; ka++)
                        if (Best(a, ka) is { } pa && Best(b, k - ka) is { } pb)
                        {
                            var p = new Part(pa.Groups.Concat(pb.Groups).ToArray(), pa.Unsafe + pb.Unsafe, pa.Unreach + pb.Unreach,
                                pa.Count + pb.Count, Math.Min(pa.Margin, pb.Margin));
                            if (best is null || p.CompareTo(best) < 0) best = p;
                        }
            return memo[(mask, k)] = best;
        }
        var all = (1 << n) - 1;
        var plan = Best(all, Math.Max(1, maxGroups)) ?? throw new InvalidOperationException("영역이 AREA 최대 크기를 넘어 한 묶음으로도 잡을 수 없다.");

        // ── ③ 묶음 정차 다듬기 ──────────────────────────────────────
        var groups = new List<AreaSplitGroup>();
        foreach (var (mask, cell) in plan.Groups.OrderBy(g => Members(g.Mask).Min()))
        {
            ct.ThrowIfCancellationRequested();
            var idx = Members(mask);
            var sub = idx.Select(i => seams[i]).ToList();
            var gc = GroupCenter(space, sub);
            var c = cells[cell];
            var (d, l, y) = Relative(space with { CenterXY = gc }, c.Station);
            var gspace = space with { CenterXY = gc, BaselineDistM = d, BaselineLateralM = l, BaselineYawDeg = y };
            StationCandidate best;
            if (refine)
                best = StationOptimizer.Optimize(model, planes, gspace, sub, wallNormal, strokesMm, ct, wallCode).Best;
            else
                best = StationOptimizer.Evaluate(model, planes, gspace, d, l, y, sub, wallNormal, strokesMm, surveyInfeasible: true, wallCode: wallCode);
            groups.Add(new AreaSplitGroup(idx, best));
        }
        var unsafeCount = groups.Sum(g => g.Seams.Where((i, k) => !hopeless.Contains(i) && !Safe(g.Best.Seams[k])).Count());
        return new AreaSplitPlan(groups, hopeless.OrderBy(i => i).ToList(), unsafeCount, cells.Count);
    }

    /// <summary>분할 후보 — 점수는 작을수록 좋다(미확보 → 못 닿음 → 묶음 수 → 최소 여유 큰 쪽).</summary>
    private sealed record Part((int Mask, int Cell)[] Groups, int Unsafe, int Unreach, int Count, double Margin)
    {
        public int CompareTo(Part o)
        {
            var c = Unsafe.CompareTo(o.Unsafe);
            if (c != 0) return c;
            c = Unreach.CompareTo(o.Unreach);
            if (c != 0) return c;
            c = Count.CompareTo(o.Count);
            return c != 0 ? c : o.Margin.CompareTo(Margin);
        }
    }

    private static List<int> Members(int mask) => Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0).ToList();

    /// <summary>묶음을 u 또는 v 축 방향 직선 하나로 둘로 나누는 모든 방법(중심 좌표가 다른 곳에서만 자른다).</summary>
    private static IEnumerable<(int A, int B)> Cuts(int mask, (double U, double V)[] centers)
    {
        var idx = Members(mask);
        if (idx.Count < 2) yield break;
        foreach (var key in new Func<int, double>[] { i => centers[i].U, i => centers[i].V })
        {
            var sorted = idx.OrderBy(key).ToList();
            for (var k = 1; k < sorted.Count; k++)
            {
                if (key(sorted[k]) - key(sorted[k - 1]) < 1e-6) continue;
                var a = sorted.Take(k).Aggregate(0, (m, i) => m | (1 << i));
                yield return (a, mask & ~a);
            }
        }
    }

    /// <summary>판정 격자 정차 — 탐색 좌표 기준 (거리, 좌우, 방향). 차체가 면에 닿는 정차는 뺀다.</summary>
    private static List<(ReachStation Station, double D, double L, double Y)> GridStations(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space, IReadOnlyList<IReadOnlyList<double[]>> seams)
    {
        // 좌우 범위 — 용접선이 중심에서 좌우로 퍼진 만큼 + 여유(묶음이 중심에서 벗어난다).
        double lx = -space.InwardH[1], ly = space.InwardH[0];
        var spread = seams.SelectMany(s => s).Max(p => Math.Abs((p[0] - space.CenterXY[0]) * lx + (p[1] - space.CenterXY[1]) * ly));
        var lMax = spread + GridLateralExtraM;
        double dLo, dHi;
        if (space.MinDistM < 0) { dLo = space.MinDistM; dHi = space.MaxDistM; }
        else
        {
            var first = StationOptimizer.FirstBodyFeasible(model, planes, space);
            dLo = first?.DistM ?? space.MinDistM;
            dHi = dLo + (space.MaxDistM - space.MinDistM);
        }
        var yaws = space.MaxYawDeg >= 90 ? new[] { 0.0, 90, 180, -90 }
            : new[] { 0.0, -10, 10 }.Where(y => Math.Abs(y) <= space.MaxYawDeg + 1e-9).ToArray();
        var list = new List<(ReachStation, double, double, double)>();
        for (var d = dLo; d <= dHi + 1e-9; d += GridStepM)
            for (var l = -lMax; l <= lMax + 1e-9; l += GridStepM)
                foreach (var y in yaws)
                {
                    var st = space.StationOf(d, l, y);
                    if (ReachSimulator.BodyAt(model, planes, st).ClearanceM >= space.MinBodyClearanceM) list.Add((st, d, l, y));
                }
        return list;
    }

    private static IReadOnlyList<double> GridStrokes(IReadOnlyList<double> strokesMm)
    {
        if (strokesMm.Count <= 2) return strokesMm;
        double lo = strokesMm.Min(), hi = strokesMm.Max();
        var list = new List<double>();
        for (var h = lo; h <= hi + 1e-9; h += GridStrokeStepMm) list.Add(h);
        return list;
    }

    /// <summary>묶음 중심(수평) — 용접선 점들의 평균.</summary>
    private static double[] GroupCenter(StationSearchSpace space, IReadOnlyList<IReadOnlyList<double[]>> seams)
    {
        var pts = seams.SelectMany(s => s).ToList();
        return new[] { pts.Average(p => p[0]), pts.Average(p => p[1]) };
    }

    /// <summary>정차 → 탐색 좌표(중심에서 안쪽 거리·좌측·방향 보정 °).</summary>
    private static (double D, double L, double Y) Relative(StationSearchSpace s, ReachStation st)
    {
        double dx = st.X - s.CenterXY[0], dy = st.Y - s.CenterXY[1];
        double nx = s.InwardH[0], ny = s.InwardH[1];
        var yaw = Math.Atan2(Math.Sin(st.Yaw - s.FacingYaw), Math.Cos(st.Yaw - s.FacingYaw)) * 180 / Math.PI;
        return (dx * nx + dy * ny, -dx * ny + dy * nx, yaw);
    }
}
