namespace HD.Acs.Core.Reach;

/// <summary>
/// set cover 탐색 조건.
/// GridStepM = 정차 후보 격자 간격, ReachRadiusM = 정차 중심에서 이 수평 거리 밖 용접선은 판정하지 않는다(팔이 닿지 않는 곳 — 계산 줄이기),
/// MaxStrokes = 리프트 후보 수 상한(양 끝 포함 고르게), LateralExtraM·DistExtraM = 용접선이 퍼진 범위 밖으로 더 볼 거리.
/// </summary>
public sealed record SetCoverOptions(
    double GridStepM = 0.3, double ReachRadiusM = 2.8, int MaxStrokes = 4,
    double LateralExtraM = 0.6, double DistExtraM = 1.2)
{
    public static SetCoverOptions Default { get; } = new();
}

/// <summary>
/// 고른 정차 하나 — 정차(도면)·리프트·맡은 용접선(입력 번호)·그 용접선들의 판정·묶음 창(면 u0, v0, u1, v1 [m]).
/// Tier = 맡은 용접선의 보장 수준 — Safe(간섭 없음 + 순회 여유 목표 이상) · Clear(간섭 없음, 여유 부족).
/// </summary>
public sealed record SetCoverStation(
    ReachStation Station, double StrokeMm, double DistM, double LateralM, double YawDeg,
    IReadOnlyList<int> Seams, IReadOnlyList<ReachSeamSurvey> Surveys, double[] Window, string Tier, double? MinMarginDeg);

/// <summary>
/// set cover 결과. Uncovered = 어느 후보로도 간섭 없이 닿지 못하는 용접선(입력 번호).
/// Cells = 정차 후보 수(방향 포함, 리프트 제외), Evaluations = 실제로 푼 (후보, 리프트, 용접선) 판정 수.
/// </summary>
public sealed record SetCoverPlan(IReadOnlyList<SetCoverStation> Stations, IReadOnlyList<int> Uncovered,
    int Cells, long Evaluations, int SafeCovered, int ClearCovered);

/// <summary>
/// 정차 묶음 최적화 — 탐욕 set cover. 면·층 하나의 용접선을 모두 덮는 정차(위치·방향·리프트) 수를 줄인다.
///
/// ① 후보: 용접선이 퍼진 범위 주변 정차 격자(면 안쪽 거리 × 좌우 × 방향, <see cref="SetCoverOptions.GridStepM"/> 간격) × 리프트.
///    차체가 면에 닿거나 벽 최소 거리를 어기는 자리는 뺀다(<see cref="StationSearchSpace.BodyAllowed"/>).
/// ② 판정표: 후보마다 수평 <see cref="SetCoverOptions.ReachRadiusM"/> 안의 용접선만 운영 판정(<see cref="ReachSimulator.Survey"/> —
///    세 자세·진입·도면 순회·간섭)으로 푼다(후보 병렬). 반경 밖은 팔이 닿지 않으므로 풀지 않는다.
/// ③ 탐욕: 단계 Safe(간섭 없음 + 순회 여유 목표 이상) → Clear(간섭 없음) 순으로, 아직 덮이지 않은 용접선을
///    <b>AREA 최대 크기 창 하나에 들어가는 만큼</b> 가장 많이 덮는 후보를 고른다(동점이면 여유 합이 큰 쪽).
///    덮는 수는 고를수록 줄기만 하므로 지연 평가(lazy greedy — 꺼낸 후보만 다시 센다)로 다시 세는 횟수를 줄인다.
/// 고른 후보 하나 = 영역 하나(정차 하나, 리프트 하나) — 맡은 용접선은 그 창 안에 있어 영역 크기 상한을 지킨다.
/// </summary>
public static class StationSetCover
{
    private const int TierNone = 0, TierClear = 2, TierSafe = 3;

    /// <param name="space">면의 탐색 좌표 — 중심(용접선 중심)·면 안쪽 수평 방향·정면 방향·층 바닥·방향 범위·벽 최소 거리. 바닥·천장은 MinDistM &lt; 0.</param>
    /// <param name="seams">용접선마다 판정 점(도면 m — 시작·중간·끝).</param>
    /// <param name="seamUv">용접선마다 면 (u, v) 점 — 창(영역 크기 상한) 판정용.</param>
    public static SetCoverPlan Plan(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        IReadOnlyList<IReadOnlyList<double[]>> seams, IReadOnlyList<IReadOnlyList<double[]>> seamUv,
        double maxSpanU, double maxSpanV, double[] wallNormal, IReadOnlyList<double> strokesMm, string? wallCode = null,
        SetCoverOptions? options = null, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var o = options ?? SetCoverOptions.Default;
        var n = seams.Count;
        if (n == 0) return new SetCoverPlan(Array.Empty<SetCoverStation>(), Array.Empty<int>(), 0, 0, 0, 0);
        var target = model.TraverseMarginTargetDeg;

        // ── ① 후보 ───────────────────────────────────────────────
        var cells = Cells(model, planes, space, seams, o);
        var strokes = Thin(strokesMm, o.MaxStrokes);
        var r2 = o.ReachRadiusM * o.ReachRadiusM;
        var near = cells.Select(c => Enumerable.Range(0, n)
            .Where(i => seams[i].All(p => Sq(p[0] - c.Station.X) + Sq(p[1] - c.Station.Y) <= r2)).ToArray()).ToArray();

        // ── ② 판정표 ─────────────────────────────────────────────
        // cand = (cell, stroke) — tier[cand][k] / survey[cand][k] 는 near[cell][k] 용접선의 판정
        var cands = new List<(int Cell, double Stroke)>();
        for (var c = 0; c < cells.Count; c++)
            if (near[c].Length > 0)
                foreach (var h in strokes) cands.Add((c, h));
        var surveys = new ReachSeamSurvey[cands.Count][];
        long evals = 0;
        var done = 0;
        Parallel.For(0, cands.Count, new ParallelOptions { CancellationToken = ct }, k =>
        {
            var (c, h) = cands[k];
            var idx = near[c];
            var row = new ReachSeamSurvey[idx.Length];
            for (var j = 0; j < idx.Length; j++)
                row[j] = ReachSimulator.Survey(model, planes, new[] { cells[c].Station }, seams[idx[j]], wallNormal, new[] { h }, wallCode);
            surveys[k] = row;
            Interlocked.Add(ref evals, idx.Length);
            var d = Interlocked.Increment(ref done);
            if (progress is not null && (d % 50 == 0 || d == cands.Count)) progress.Report((d, cands.Count));
        });
        int TierOf(ReachSeamSurvey s) => s.Verdict != ReachVerdict.Clear ? TierNone
            : s.TraverseMarginDeg is { } m && m < target ? TierClear : TierSafe;
        var tiers = surveys.Select(row => row.Select(TierOf).ToArray()).ToArray();

        // 용접선 창 판정용 (u, v) 외곽
        var box = seamUv.Select(p => (U0: p.Min(q => q[0]), V0: p.Min(q => q[1]), U1: p.Max(q => q[0]), V1: p.Max(q => q[1]))).ToArray();

        // ── ③ 탐욕(단계별, 지연 평가) ───────────────────────────────
        var covered = new bool[n];
        var chosen = new List<SetCoverStation>();
        int safeCovered = 0, clearCovered = 0;
        foreach (var tier in new[] { TierSafe, TierClear })
        {
            ct.ThrowIfCancellationRequested();
            // 이 단계 후보 — 덮을 수 있는 (아직 안 덮인) 용접선이 있는 후보만
            var heap = new PriorityQueue<int, (int Gain, double Margin)>(Comparer<(int Gain, double Margin)>.Create(
                (a, b) => a.Gain != b.Gain ? b.Gain.CompareTo(a.Gain) : b.Margin.CompareTo(a.Margin)));
            var first = new (int Gain, double Margin)[cands.Count];
            Parallel.For(0, cands.Count, new ParallelOptions { CancellationToken = ct }, k =>
            {
                var w = BestWindow(Members(k, tier), box, maxSpanU, maxSpanV);
                first[k] = (w.Seams.Count, MarginSum(k, w.Seams));
            });
            for (var k = 0; k < cands.Count; k++)
                if (first[k].Gain > 0) heap.Enqueue(k, first[k]);

            while (heap.TryDequeue(out var k, out _))
            {
                ct.ThrowIfCancellationRequested();
                var w = BestWindow(Members(k, tier), box, maxSpanU, maxSpanV);
                if (w.Seams.Count == 0) continue;
                var now = (w.Seams.Count, MarginSum(k, w.Seams));
                // 다시 센 값이 다음 후보의 (낡은) 값보다 작으면 다시 넣는다 — 낡은 값은 실제보다 크거나 같다
                if (heap.TryPeek(out _, out var next) && Compare(now, next) < 0) { heap.Enqueue(k, now); continue; }

                var (cell, h) = cands[k];
                var idx = near[cell];
                var pick = w.Seams.OrderBy(i => i).ToList();
                var rows = pick.Select(i => surveys[k][Array.IndexOf(idx, i)]).ToList();
                foreach (var i in pick) covered[i] = true;
                if (tier == TierSafe) safeCovered += pick.Count; else clearCovered += pick.Count;
                var margins = rows.Select(r => r.TraverseMarginDeg).OfType<double>().ToList();
                var c0 = cells[cell];
                chosen.Add(new SetCoverStation(c0.Station, h, c0.D, c0.L, c0.Y, pick, rows,
                    new[] { w.U0, w.V0, w.U0 + maxSpanU, w.V0 + maxSpanV },
                    tier == TierSafe ? "Safe" : "Clear", margins.Count > 0 ? margins.Min() : null));
            }
        }
        var uncovered = Enumerable.Range(0, n).Where(i => !covered[i]).ToList();
        return new SetCoverPlan(chosen, uncovered, cells.Count, evals, safeCovered, clearCovered);

        // 후보 k 가 단계 tier 이상으로 덮는, 아직 안 덮인 용접선
        List<int> Members(int k, int tier)
        {
            var idx = near[cands[k].Cell];
            var t = tiers[k];
            var list = new List<int>();
            for (var j = 0; j < idx.Length; j++)
                if (t[j] >= tier && !covered[idx[j]]) list.Add(idx[j]);
            return list;
        }
        double MarginSum(int k, IReadOnlyList<int> members)
        {
            var idx = near[cands[k].Cell];
            var s = 0.0;
            foreach (var i in members)
                s += Math.Min(surveys[k][Array.IndexOf(idx, i)].TraverseMarginDeg ?? target, 3 * target);
            return s;
        }
    }

    private static int Compare((int Gain, double Margin) a, (int Gain, double Margin) b) =>
        a.Gain != b.Gain ? a.Gain.CompareTo(b.Gain) : a.Margin.CompareTo(b.Margin);

    /// <summary>
    /// 창(u 폭 maxU × v 폭 maxV) 하나에 들어가는 용접선이 가장 많은 자리 — 창의 왼쪽 아래 모서리를 용접선 외곽의 u0·v0 조합에서 찾는다
    /// (최적 창은 항상 그런 자리로 옮길 수 있다). 용접선 하나가 창보다 크면 그 용접선만 담는 창을 따로 본다.
    /// </summary>
    public static (List<int> Seams, double U0, double V0) BestWindow(IReadOnlyList<int> members,
        IReadOnlyList<(double U0, double V0, double U1, double V1)> box, double maxU, double maxV)
    {
        var best = (Seams: new List<int>(), U0: 0.0, V0: 0.0);
        if (members.Count == 0) return best;
        const double eps = 1e-6;
        var us = members.Select(i => box[i].U0).Distinct().ToList();
        var vs = members.Select(i => box[i].V0).Distinct().ToList();
        foreach (var u0 in us)
        {
            var inU = members.Where(i => box[i].U0 >= u0 - eps && box[i].U1 <= u0 + maxU + eps).ToList();
            if (inU.Count <= best.Seams.Count) continue;
            foreach (var v0 in vs)
            {
                var c = 0;
                foreach (var i in inU) if (box[i].V0 >= v0 - eps && box[i].V1 <= v0 + maxV + eps) c++;
                if (c > best.Seams.Count)
                    best = (inU.Where(i => box[i].V0 >= v0 - eps && box[i].V1 <= v0 + maxV + eps).ToList(), u0, v0);
            }
        }
        if (best.Seams.Count == 0)   // 창보다 큰 용접선 — 하나씩
            best = (new List<int> { members[0] }, box[members[0]].U0, box[members[0]].V0);
        return best;
    }

    /// <summary>정차 후보 격자 — (정차, 안쪽 거리, 좌우, 방향 보정 °). 차체가 설 수 없는 자리는 뺀다.</summary>
    private static List<(ReachStation Station, double D, double L, double Y)> Cells(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        IReadOnlyList<IReadOnlyList<double[]>> seams, SetCoverOptions o)
    {
        double ix = space.InwardH[0], iy = space.InwardH[1], lx = -iy, ly = ix;
        var pts = seams.SelectMany(s => s).ToList();
        double Lat(double[] p) => (p[0] - space.CenterXY[0]) * lx + (p[1] - space.CenterXY[1]) * ly;
        double Dep(double[] p) => (p[0] - space.CenterXY[0]) * ix + (p[1] - space.CenterXY[1]) * iy;
        var lLo = pts.Min(Lat) - o.LateralExtraM;
        var lHi = pts.Max(Lat) + o.LateralExtraM;
        double dLo, dHi;
        if (space.MinDistM < 0)
        {   // 바닥·천장 — 용접선이 퍼진 범위 그대로(정차가 용접선 아래·위에 선다)
            dLo = pts.Min(Dep) - o.DistExtraM;
            dHi = pts.Max(Dep) + o.DistExtraM;
        }
        else
        {   // 벽 — 중심(용접선 중심, 면 위)에서 안쪽으로. 경사면은 용접선이 안쪽으로도 퍼져 있어(머리 위 챔퍼) 그 범위를 더한다.
            // 면 너머 자리는 차체 판정(BodyAllowed)이 뺀다.
            dLo = Math.Min(space.MinDistM, pts.Min(Dep));
            if (space.MinWallDistM > 0) dLo = Math.Max(dLo, space.MinWallDistM);
            dHi = Math.Max(0, pts.Max(Dep)) + space.MaxDistM;
        }
        var yaws = space.MaxYawDeg >= 90 ? new[] { 0.0, 90, 180, -90 }
            : new[] { 0.0, -10, 10 }.Where(y => Math.Abs(y) <= space.MaxYawDeg + 1e-9).ToArray();
        var grid = new List<(double D, double L, double Y)>();
        for (var d = dLo; d <= dHi + 1e-9; d += o.GridStepM)
            for (var l = lLo; l <= lHi + 1e-9; l += o.GridStepM)
                foreach (var y in yaws) grid.Add((d, l, y));
        var ok = new bool[grid.Count];
        Parallel.For(0, grid.Count, k => ok[k] = space.BodyAllowed(model, planes, grid[k].D, grid[k].L, grid[k].Y, out _));
        return grid.Where((_, k) => ok[k]).Select(g => (space.StationOf(g.D, g.L, g.Y), g.D, g.L, g.Y)).ToList();
    }

    /// <summary>리프트 후보를 많아야 <paramref name="max"/> 개로 — 양 끝 포함 고르게.</summary>
    public static IReadOnlyList<double> Thin(IReadOnlyList<double> strokes, int max)
    {
        if (strokes.Count <= Math.Max(1, max)) return strokes;
        if (max <= 1) return new[] { strokes[0] };
        return Enumerable.Range(0, max).Select(i => strokes[(int)Math.Round(i * (strokes.Count - 1) / (double)(max - 1))]).Distinct().ToList();
    }

    private static double Sq(double x) => x * x;
}
