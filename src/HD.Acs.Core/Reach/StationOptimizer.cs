namespace HD.Acs.Core.Reach;

/// <summary>
/// 정차점 탐색 범위 — 영역 중심(수평 투영)에서 면 안쪽 수평 방향으로 <c>Dist</c>, 그 직각(차체 좌측 +)으로 <c>Lateral</c>,
/// 면 정면 방향에 <c>Yaw</c> 보정. 지금 정차점(배차 식·영역 덮어쓰기 반영)은 Baseline* 이다.
/// </summary>
/// <param name="CenterXY">영역 중심의 도면 x·y [m].</param>
/// <param name="InwardH">면 안쪽 법선의 수평 단위벡터(도면 x·y).</param>
/// <param name="FacingYaw">면 정면 방향(차체가 벽을 보는 방향) [rad].</param>
/// <param name="MinDistM">거리 탐색 시작 하한. 실제 시작은 차체가 면에서 떨어지는 첫 거리이고, 폭은 MaxDistM − MinDistM 이다.</param>
public sealed record StationSearchSpace(
    double[] CenterXY, double[] InwardH, double FacingYaw, double FloorZ,
    double BaselineDistM, double BaselineLateralM = 0, double BaselineYawDeg = 0,
    double MinDistM = 0.4, double MaxDistM = 2.4, double DistStepM = 0.1,
    double MaxLateralM = 0.8, double LateralStepM = 0.1,
    double MaxYawDeg = 15, double YawStepDeg = 5,
    double MinBodyClearanceM = 0.05)
{
    /// <summary>후보 좌표 → 정차(도면). 좌측 = 정면 방향을 반시계 90°.</summary>
    public ReachStation StationOf(double distM, double lateralM, double yawDeg)
    {
        var lx = -InwardH[1];
        var ly = InwardH[0];
        return new ReachStation(
            CenterXY[0] + InwardH[0] * distM + lx * lateralM,
            CenterXY[1] + InwardH[1] * distM + ly * lateralM,
            FacingYaw + yawDeg * Math.PI / 180, FloorZ);
    }
}

/// <summary>후보 정차 하나의 영역 조사 결과. 차체가 면에 너무 가까우면 <see cref="Feasible"/> = false 이고 조사하지 않는다.</summary>
/// <param name="Robustness">Clear 용접선 여유(최대 <see cref="StationOptimizer.RobustCapM"/>로 자름)의 평균 [m] — 같은 개수일 때 순위.</param>
public sealed record StationCandidate(
    double DistM, double LateralM, double YawDeg, ReachStation Station, double BodyClearanceM, bool Feasible,
    int Clear, int Collision, int Unreachable, double Robustness, IReadOnlyList<ReachSeamSurvey> Seams);

/// <summary>최적화 결과 — 기본 정차와 찾은 정차, 평가한 후보 수.</summary>
public sealed record StationOptimum(StationCandidate Baseline, StationCandidate Best, int Evaluated);

/// <summary>
/// 영역 정차점 최적화 — 후보 정차마다 영역의 모든 용접선을 <see cref="ReachSimulator.Survey"/>(리프트 전 범위)로 조사해
/// ① 간섭 없이 닿는 용접선 수 ② 여유(<see cref="StationCandidate.Robustness"/>) ③ 기본 정차에서 덜 옮긴 쪽 순으로 고른다.
/// 탐색 = 거리×좌우 성긴 격자(방향 보정 0) → 최상위 몇 곳에서 세 축 패턴 탐색(정해진 간격). 같은 후보는 한 번만 계산한다.
/// </summary>
public static class StationOptimizer
{
    /// <summary>여유 순위에서 이보다 큰 여유는 같게 본다 [m].</summary>
    public const double RobustCapM = 0.15;

    private const double CoarseFactor = 3;   // 성긴 격자 = 간격 × 3
    private const int Seeds = 3;
    private const int MaxIterations = 60;

    public static StationOptimum Optimize(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm,
        CancellationToken ct = default)
    {
        var cache = new Dictionary<(int, int, int), StationCandidate>();
        StationCandidate Eval(int di, int li, int yi)
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue((di, li, yi), out var hit)) return hit;
            var c = Evaluate(model, planes, space, di * space.DistStepM, li * space.LateralStepM, yi * space.YawStepDeg,
                seams, wallNormal, strokesMm);
            cache[(di, li, yi)] = c;
            return c;
        }

        int Idx(double v, double step) => (int)Math.Round(v / step);
        // 탐색 창 — 차체가 면에서 떨어지는 가장 가까운 (거리, 좌우)에서 시작해 거리 MaxDistM − MinDistM 폭·좌우 ±MaxLateralM.
        // 경사면 위쪽 영역은 중심이 벽 깊숙이 있고, 하부 경사면에 걸친 횡격벽 끝 영역은 옆으로 비켜야 차체가 빠진다.
        var baseline = Evaluate(model, planes, space, space.BaselineDistM, space.BaselineLateralM, space.BaselineYawDeg,
            seams, wallNormal, strokesMm, surveyInfeasible: true);
        if (FirstBodyFeasible(model, planes, space) is not { } start) return new StationOptimum(baseline, baseline, 1);
        var dMin = Idx(start.DistM, space.DistStepM);
        var dMax = dMin + Idx(space.MaxDistM - space.MinDistM, space.DistStepM);
        var lc = Idx(start.LateralM, space.LateralStepM);
        var lMax = Idx(space.MaxLateralM, space.LateralStepM);
        var yMax = Idx(space.MaxYawDeg, space.YawStepDeg);
        bool Inside((int D, int L, int Y) n) => n.D >= dMin && n.D <= dMax && Math.Abs(n.L - lc) <= lMax && Math.Abs(n.Y) <= yMax;

        var baseIdx = (Math.Clamp(Idx(space.BaselineDistM, space.DistStepM), dMin, dMax),
            Math.Clamp(Idx(space.BaselineLateralM, space.LateralStepM), lc - lMax, lc + lMax),
            Math.Clamp(Idx(space.BaselineYawDeg, space.YawStepDeg), -yMax, yMax));

        // ① 성긴 격자(방향 보정 0)
        var cd = Math.Max(1, (int)CoarseFactor);
        for (var di = dMin; di <= dMax; di += cd)
            for (var li = lc - lMax; li <= lc + lMax; li += cd)
                Eval(di, li, 0);

        // ② 상위 후보에서 패턴 탐색 — 세 축 ±1 간격 이웃 중 더 나은 곳으로, 나아지지 않으면 멈춤
        var seeds = cache.OrderByDescending(kv => kv.Value, Ranking).Take(Seeds).Select(kv => kv.Key).ToList();
        if (!seeds.Contains(baseIdx)) seeds.Add(baseIdx);
        foreach (var seed in seeds)
        {
            var cur = seed;
            for (var it = 0; it < MaxIterations; it++)
            {
                var best = cur;
                foreach (var (dd, dl, dy) in Neighbors)
                {
                    var n = (cur.Item1 + dd, cur.Item2 + dl, cur.Item3 + dy);
                    if (!Inside(n)) continue;
                    if (Ranking.Compare(Eval(n.Item1, n.Item2, n.Item3), Eval(best.Item1, best.Item2, best.Item3)) > 0) best = n;
                }
                if (best == cur) break;
                cur = best;
            }
        }

        var winner = cache.Values.Max(Ranking)!;
        // 닿는 용접선이 늘지 않으면(같은 수면 여유가 나아질 때만) 지금 정차를 그대로 둔다 — 옮겨도 검사할 수 없으면 옮길 이유가 없다.
        var better = winner.Clear > baseline.Clear
                     || (winner.Clear == baseline.Clear && winner.Clear > 0 && Ranking.Compare(winner, baseline) > 0);
        if (!better) winner = baseline;
        return new StationOptimum(baseline, winner, cache.Count);
    }

    private static readonly (int, int, int)[] Neighbors =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
        (1, 1, 0), (1, -1, 0), (-1, 1, 0), (-1, -1, 0),
    };

    /// <summary>
    /// 방향 보정 0 에서 차체가 면에서 <see cref="StationSearchSpace.MinBodyClearanceM"/> 이상 떨어지는 가장 가까운 정차 —
    /// 거리(MinDistM 부터 <see cref="MaxBodyScanM"/>)·좌우(±<see cref="MaxBodyScanLateralM"/>)를 (거리 − MinDistM) + |좌우| 가
    /// 작은 순으로 본다. 없으면 null.
    /// </summary>
    public static (double DistM, double LateralM)? FirstBodyFeasible(ReachRobotModel model, IReadOnlyList<ReachPlane> planes,
        StationSearchSpace space)
    {
        var nd = (int)Math.Floor((MaxBodyScanM - space.MinDistM) / space.DistStepM + 1e-9);
        var nl = (int)Math.Floor(MaxBodyScanLateralM / space.LateralStepM + 1e-9);
        var order = from i in Enumerable.Range(0, nd + 1)
                    from j in Enumerable.Range(-nl, 2 * nl + 1)
                    let d = space.MinDistM + i * space.DistStepM
                    let l = j * space.LateralStepM
                    orderby (d - space.MinDistM) + Math.Abs(l), Math.Abs(l)
                    select (d, l);
        foreach (var (d, l) in order)
            if (ReachSimulator.BodyAt(model, planes, space.StationOf(d, l, 0)).ClearanceM >= space.MinBodyClearanceM)
                return (d, l);
        return null;
    }

    /// <summary>차체가 면에서 떨어지는 정차를 찾을 때 볼 최대 거리·좌우 [m].</summary>
    public const double MaxBodyScanM = 8;
    public const double MaxBodyScanLateralM = 3;

    /// <summary>
    /// 후보 하나를 조사한다 — 차체 여유가 모자라면 불가(<see cref="StationCandidate.Feasible"/> = false)이고,
    /// <paramref name="surveyInfeasible"/> 가 아니면 조사 없이 모두 도달불가로 둔다(지금 정차는 실제 판정을 보여 주려고 조사한다).
    /// </summary>
    public static StationCandidate Evaluate(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        double distM, double lateralM, double yawDeg,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm,
        bool surveyInfeasible = false)
    {
        var station = space.StationOf(distM, lateralM, yawDeg);
        var body = ReachSimulator.BodyAt(model, planes, station).ClearanceM;
        var feasible = body >= space.MinBodyClearanceM;
        if (!feasible && !surveyInfeasible)
            return new StationCandidate(distM, lateralM, yawDeg, station, body, false, 0, 0, seams.Count, 0,
                Array.Empty<ReachSeamSurvey>());

        var results = new ReachSeamSurvey[seams.Count];
        var stations = new[] { station };
        Parallel.For(0, seams.Count, i =>
            results[i] = ReachSimulator.Survey(model, planes, stations, seams[i], wallNormal, strokesMm));
        var clear = results.Where(r => r.Verdict == ReachVerdict.Clear).ToList();
        return new StationCandidate(distM, lateralM, yawDeg, station, body, feasible,
            clear.Count, results.Count(r => r.Verdict == ReachVerdict.Collision), results.Count(r => r.Verdict == ReachVerdict.Unreachable),
            clear.Count == 0 ? 0 : clear.Average(r => Math.Min(r.MinClearanceM ?? 0, RobustCapM)), results);
    }

    /// <summary>순위 — 가능 &gt; Clear 수 &gt; 여유(1 mm 단위) &gt; 덜 옮김(좌우·방향).</summary>
    public static readonly IComparer<StationCandidate> Ranking = Comparer<StationCandidate>.Create((a, b) =>
    {
        var c = a.Feasible.CompareTo(b.Feasible);
        if (c != 0) return c;
        c = a.Clear.CompareTo(b.Clear);
        if (c != 0) return c;
        c = Math.Round(a.Robustness, 3).CompareTo(Math.Round(b.Robustness, 3));
        if (c != 0) return c;
        return (Math.Abs(b.LateralM) + Math.Abs(b.YawDeg) / 50).CompareTo(Math.Abs(a.LateralM) + Math.Abs(a.YawDeg) / 50);
    });
}
