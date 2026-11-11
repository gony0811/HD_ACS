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
/// <param name="StrokeMm">영역 리프트 고정 최적화에서 고른 공통 리프트 높이 [mm] (용접선별 리프트면 null).</param>
/// <param name="SafeClear">Clear 중 도면 순회 관절 여유가 목표(<see cref="ReachRobotModel.TraverseMarginTargetDeg"/>) 이상인 수(순회 판정을 안 하면 Clear 와 같다).</param>
/// <param name="TraverseMarginDeg">닿는 용접선들의 도면 순회 최소 관절 여유 [도](순회 판정을 안 했으면 null).</param>
public sealed record StationCandidate(
    double DistM, double LateralM, double YawDeg, ReachStation Station, double BodyClearanceM, bool Feasible,
    int Clear, int Collision, int Unreachable, double Robustness, IReadOnlyList<ReachSeamSurvey> Seams,
    double? StrokeMm = null, int SafeClear = 0, double? TraverseMarginDeg = null, double MarginTargetDeg = ReachRobotModel.DefaultTraverseMarginTargetDeg);

/// <summary>최적화 결과 — 기본 정차와 찾은 정차, 평가한 후보 수.</summary>
public sealed record StationOptimum(StationCandidate Baseline, StationCandidate Best, int Evaluated);

/// <summary>
/// 영역 정차점 최적화 — 후보 정차마다 영역의 모든 용접선을 공통 리프트 높이(<see cref="AreaLift"/>)로 조사해
/// ① 도면 순회 관절 여유를 목표 이상 확보하고 간섭 없이 닿는 용접선 수 ② 간섭 없이 닿는 수 ③ 순회 최소 관절 여유(목표까지)
/// ④ 면 간섭 여유(<see cref="StationCandidate.Robustness"/>) ⑤ 기본 정차에서 덜 옮긴 쪽 순으로 고른다.
/// 탐색 = 거리×좌우 성긴 격자(수평면은 네 방향 포함) → 최상위 몇 곳에서 세 축 패턴 탐색(정해진 간격). 같은 후보는 한 번만 계산한다.
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
        CancellationToken ct = default, string? wallCode = null, bool areaLift = true)
    {
        var cache = new Dictionary<(int, int, int), StationCandidate>();
        StationCandidate Eval(int di, int li, int yi)
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue((di, li, yi), out var hit)) return hit;
            var c = Evaluate(model, planes, space, di * space.DistStepM, li * space.LateralStepM, yi * space.YawStepDeg,
                seams, wallNormal, strokesMm, wallCode: wallCode, areaLift: areaLift);
            cache[(di, li, yi)] = c;
            return c;
        }

        int Idx(double v, double step) => (int)Math.Round(v / step);
        // 탐색 창 — 차체가 면에서 떨어지는 가장 가까운 (거리, 좌우)에서 시작해 거리 MaxDistM − MinDistM 폭·좌우 ±MaxLateralM.
        // 경사면 위쪽 영역은 중심이 벽 깊숙이 있고, 하부 경사면에 걸친 횡격벽 끝 영역은 옆으로 비켜야 차체가 빠진다.
        // 수평면(T·B, MinDistM < 0)은 영역 중심 주위 [MinDistM, MaxDistM] × ±MaxLateralM 와 전 방향(±180°)을 본다.
        var baseline = Evaluate(model, planes, space, space.BaselineDistM, space.BaselineLateralM, space.BaselineYawDeg,
            seams, wallNormal, strokesMm, surveyInfeasible: true, wallCode: wallCode, areaLift: areaLift);
        // 지금 정차가 이미 최상 점수(모두 여유 확보·순회 여유 목표·면 간섭 여유가 기구상 최대)면 탐색하지 않는다 — 어느 후보도 점수를 올릴 수 없다.
        // (같은 점수의 더 가운데 정차는 있을 수 있지만, 점수가 같으면 옮길 이유가 없다 — UI 도 옮길 영역으로 치지 않는다.)
        if (IsTop(baseline, seams.Count, model.TraverseMarginTargetDeg, RobustnessBound(model))) return new StationOptimum(baseline, baseline, 1);
        if (FirstBodyFeasible(model, planes, space) is not { } start) return new StationOptimum(baseline, baseline, 1);
        var dMin = space.MinDistM < 0
            ? Idx(start.DistM + space.MinDistM, space.DistStepM)
            : Idx(start.DistM, space.DistStepM);
        var dMax = space.MinDistM < 0
            ? Idx(start.DistM + space.MaxDistM, space.DistStepM)
            : dMin + Idx(space.MaxDistM - space.MinDistM, space.DistStepM);
        var lc = Idx(start.LateralM, space.LateralStepM);
        var lMax = Idx(space.MaxLateralM, space.LateralStepM);
        var yMax = Idx(space.MaxYawDeg, space.YawStepDeg);
        int WrapY(int y) => space.MaxYawDeg >= 180 && yMax > 0
            ? ((y + yMax - 1) % (2 * yMax) + 2 * yMax) % (2 * yMax) - yMax + 1
            : y;
        bool Inside((int D, int L, int Y) n) => n.D >= dMin && n.D <= dMax && Math.Abs(n.L - lc) <= lMax && Math.Abs(n.Y) <= yMax;

        var baseIdx = (Math.Clamp(Idx(space.BaselineDistM, space.DistStepM), dMin, dMax),
            Math.Clamp(Idx(space.BaselineLateralM, space.LateralStepM), lc - lMax, lc + lMax),
            Math.Clamp(Idx(space.BaselineYawDeg, space.YawStepDeg), -yMax, yMax));

        // ① 성긴 격자(벽면은 방향 보정 0, 수평면은 90° 간격 네 방향)
        var yStep90 = Math.Max(1, Idx(90, space.YawStepDeg));
        var coarseYaws = space.MaxYawDeg >= 90
            ? new[] { 0, yStep90, -yStep90, yMax }.Distinct().Where(y => Math.Abs(y) <= yMax).ToArray()
            : new[] { 0 };
        var cd = Math.Max(1, (int)CoarseFactor);
        for (var di = dMin; di <= dMax; di += cd)
            for (var li = lc - lMax; li <= lc + lMax; li += cd)
                foreach (var yi in coarseYaws)
                    Eval(di, li, yi);

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
                    var n = (cur.Item1 + dd, cur.Item2 + dl, WrapY(cur.Item3 + dy));
                    if (!Inside(n)) continue;
                    if (Ranking.Compare(Eval(n.Item1, n.Item2, n.Item3), Eval(best.Item1, best.Item2, best.Item3)) > 0) best = n;
                }
                if (best == cur) break;
                cur = best;
            }
        }

        var winner = cache.Values.Max(Ranking)!;
        // 순위가 나아질 때만 옮긴다 — 아무것도 닿지 않으면 옮길 이유가 없다(지금 정차 유지).
        var better = winner.Clear > 0 && Ranking.Compare(winner, baseline) > 0;
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
    /// 작은 순으로 본다(수평면 MinDistM &lt; 0 은 중심 (0,0) 에서 |거리| + |좌우| 순). 없으면 null.
    /// </summary>
    public static (double DistM, double LateralM)? FirstBodyFeasible(ReachRobotModel model, IReadOnlyList<ReachPlane> planes,
        StationSearchSpace space)
    {
        if (space.MinDistM < 0)
        {
            var ndc = (int)Math.Floor(MaxBodyScanLateralM / space.DistStepM + 1e-9);
            var nlc = (int)Math.Floor(MaxBodyScanLateralM / space.LateralStepM + 1e-9);
            var centered = from i in Enumerable.Range(-ndc, 2 * ndc + 1)
                           from j in Enumerable.Range(-nlc, 2 * nlc + 1)
                           let d = i * space.DistStepM
                           let l = j * space.LateralStepM
                           orderby Math.Abs(d) + Math.Abs(l), Math.Abs(l), Math.Abs(d)
                           select (d, l);
            foreach (var (d, l) in centered)
                if (ReachSimulator.BodyAt(model, planes, space.StationOf(d, l, 0)).ClearanceM >= space.MinBodyClearanceM)
                    return (d, l);
            return null;
        }
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
    /// <paramref name="areaLift"/> 가 true(기본)면 영역의 모든 용접선을 단일 공통 리프트 높이로 판정한다.
    /// </summary>
    public static StationCandidate Evaluate(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        double distM, double lateralM, double yawDeg,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm,
        bool surveyInfeasible = false, string? wallCode = null, bool areaLift = true)
    {
        var station = space.StationOf(distM, lateralM, yawDeg);
        var body = ReachSimulator.BodyAt(model, planes, station).ClearanceM;
        var feasible = body >= space.MinBodyClearanceM;
        if (!feasible && !surveyInfeasible)
            return new StationCandidate(distM, lateralM, yawDeg, station, body, false, 0, 0, seams.Count, 0,
                Array.Empty<ReachSeamSurvey>());

        double? strokeMm = null;
        IReadOnlyList<ReachSeamSurvey> results;
        if (areaLift)
        {
            (strokeMm, results) = AreaLift.EvaluateFixed(model, planes, station, seams, wallNormal, strokesMm, wallCode);
        }
        else
        {
            var arr = new ReachSeamSurvey[seams.Count];
            var stations = new[] { station };
            Parallel.For(0, seams.Count, i =>
                arr[i] = ReachSimulator.Survey(model, planes, stations, seams[i], wallNormal, strokesMm, wallCode));
            results = arr;
        }
        var clear = results.Where(r => r.Verdict == ReachVerdict.Clear).ToList();
        var cap = model.TraverseMarginTargetDeg;
        var margins = results.Where(r => r.Verdict != ReachVerdict.Unreachable && r.TraverseMarginDeg is not null)
            .Select(r => r.TraverseMarginDeg!.Value).ToList();
        return new StationCandidate(distM, lateralM, yawDeg, station, body, feasible,
            clear.Count, results.Count(r => r.Verdict == ReachVerdict.Collision), results.Count(r => r.Verdict == ReachVerdict.Unreachable),
            clear.Count == 0 ? 0 : clear.Average(r => Math.Min(r.MinClearanceM ?? 0, RobustCapM)), results, strokeMm,
            clear.Count(r => r.TraverseMarginDeg is not { } m || m >= cap), margins.Count == 0 ? null : margins.Min(), cap);
    }

    /// <summary>
    /// 영역 판정 점수 — (여유 확보 간섭 없음 수, 간섭 없음 수, 도달 수, 순회 최소 여유 키, 도달 불가 수). 여유 확보 = Clear 이고 도면 순회 여유 ≥ 목표
    /// (순회 판정이 없으면 Clear 그대로). 최소 여유는 닿는 용접선 중 최소를 목표에서 자르고 0.5° 로 반올림한다(<see cref="MarginKey"/>).
    /// </summary>
    public static (int Safe, int Clear, int Reach, double Margin, int Miss) Score(IReadOnlyList<ReachSeamSurvey> seams, double marginTargetDeg)
    {
        var margins = seams.Where(r => r.Verdict != ReachVerdict.Unreachable && r.TraverseMarginDeg is not null)
            .Select(r => r.TraverseMarginDeg!.Value).ToList();
        return (seams.Count(r => r.Verdict == ReachVerdict.Clear && (r.TraverseMarginDeg is not { } m || m >= marginTargetDeg)),
            seams.Count(r => r.Verdict == ReachVerdict.Clear), seams.Count(r => r.Verdict != ReachVerdict.Unreachable),
            MarginKey(margins.Count == 0 ? null : margins.Min(), marginTargetDeg),
            seams.Count(r => r.Verdict == ReachVerdict.Unreachable));
    }

    /// <summary>
    /// 더 올릴 수 없는 점수인가 — 차체 가능·모든 용접선 여유 확보·순회 최소 여유 키 = 목표·면 간섭 여유 = 도달 가능한 최대(<paramref name="robustBoundM"/>).
    /// 순위에서 이보다 앞서는 후보는 없다(남은 기준은 정차 위치뿐).
    /// </summary>
    /// <param name="robustBoundM">면 간섭 여유가 가질 수 있는 최대 — <see cref="RobustnessBound"/>. 없으면 <see cref="RobustCapM"/>.</param>
    public static bool IsTop(StationCandidate c, int seamCount, double marginTargetDeg, double robustBoundM = RobustCapM) =>
        c.Feasible && seamCount > 0 && c.SafeClear == seamCount
        && MarginKey(c.TraverseMarginDeg, marginTargetDeg) >= marginTargetDeg
        && Math.Round(c.Robustness, 3) >= Math.Round(Math.Min(robustBoundM, RobustCapM), 3);

    /// <summary>
    /// 면 간섭 여유(<see cref="StationCandidate.Robustness"/>)의 상한 [m] — 초점 자세는 TCP 가 용접선 면에서 초점 이격만큼 떨어지고 광축이 면에 수직이라
    /// 툴 상자의 가장 먼 끝(플랜지 z 최대)과 그 면 사이가 (공구 z + 초점 이격 − 툴 끝 z) 로 정해진다. 어느 정차도 이보다 큰 여유를 낼 수 없다
    /// (실기 모델 365 + 10 − 276.5 = 98.5 mm). <see cref="RobustCapM"/> 보다 크면 그 값.
    /// </summary>
    public static double RobustnessBound(ReachRobotModel m) =>
        m.ToolBoxes.Count == 0 || m.StageStandoffsMm.Length == 0 ? RobustCapM
        : Math.Min(RobustCapM, Math.Max(0, (m.ToolZMm + m.StageStandoffsMm[^1] - m.ToolBoxes.Max(b => b.Max[2])) / 1000.0));

    /// <summary>순위용 최소 여유 — 목표 이상은 같게, 0.5° 단위. 순회 판정이 없으면(null) 목표.</summary>
    public static double MarginKey(double? marginDeg, double targetDeg) =>
        marginDeg is { } m ? Math.Round(Math.Min(m, targetDeg) * 2, MidpointRounding.AwayFromZero) / 2 : targetDeg;

    /// <summary>
    /// 점수 사전식 비교 — 전부 도달(도달 불가 0) > 여유 확보 > 간섭 없음 > 도달 > 최소 여유.
    /// 전부 도달을 맨 앞에 두는 이유: HD_AMR 리치 사전 점검은 정차의 용접선 중 한 점이라도 불가면 그 정차(Order) 전체를 실행하지 않는다
    /// (2026-11-12 L4-R09C12 — 한 개 불가로 나머지 2개도 미실행). 닿는 수가 많아도 하나가 빠지는 정차는 실제로는 0 이다.
    /// </summary>
    public static int CompareScore((int Safe, int Clear, int Reach, double Margin, int Miss) a, (int Safe, int Clear, int Reach, double Margin, int Miss) b)
    {
        var c = (a.Miss == 0).CompareTo(b.Miss == 0);
        if (c != 0) return c;
        c = a.Safe.CompareTo(b.Safe);
        if (c != 0) return c;
        c = a.Clear.CompareTo(b.Clear);
        if (c != 0) return c;
        c = a.Reach.CompareTo(b.Reach);
        return c != 0 ? c : a.Margin.CompareTo(b.Margin);
    }

    /// <summary>
    /// 순위 — 가능 &gt; 전부 도달(도달 불가 0) &gt; 여유 확보 Clear 수 &gt; Clear 수 &gt; 순회 최소 관절 여유(목표까지, 0.5°) &gt; 면 간섭 여유(1 mm 단위) &gt; 덜 옮김(좌우·방향·거리).
    /// </summary>
    public static readonly IComparer<StationCandidate> Ranking = Comparer<StationCandidate>.Create((a, b) =>
    {
        var c = a.Feasible.CompareTo(b.Feasible);
        if (c != 0) return c;
        c = (a.Unreachable == 0).CompareTo(b.Unreachable == 0);
        if (c != 0) return c;
        c = a.SafeClear.CompareTo(b.SafeClear);
        if (c != 0) return c;
        c = a.Clear.CompareTo(b.Clear);
        if (c != 0) return c;
        c = MarginKey(a.TraverseMarginDeg, a.MarginTargetDeg).CompareTo(MarginKey(b.TraverseMarginDeg, b.MarginTargetDeg));
        if (c != 0) return c;
        c = Math.Round(a.Robustness, 3).CompareTo(Math.Round(b.Robustness, 3));
        if (c != 0) return c;
        c = (Math.Abs(b.LateralM) + Math.Abs(b.YawDeg) / 50).CompareTo(Math.Abs(a.LateralM) + Math.Abs(a.YawDeg) / 50);
        return c != 0 ? c : Math.Abs(b.DistM).CompareTo(Math.Abs(a.DistM));
    });
}
