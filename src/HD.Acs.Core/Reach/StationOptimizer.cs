namespace HD.Acs.Core.Reach;

/// <summary>
/// 정차점 탐색 범위 — 영역 중심(수평 투영)에서 면 안쪽 수평 방향으로 <c>Dist</c>, 그 직각(차체 좌측 +)으로 <c>Lateral</c>,
/// 면 정면 방향에 <c>Yaw</c> 보정. 지금 정차점(배차 식·영역 덮어쓰기 반영)은 Baseline* 이다.
/// </summary>
/// <param name="CenterXY">영역 중심의 도면 x·y [m].</param>
/// <param name="InwardH">면 안쪽 법선의 수평 단위벡터(도면 x·y).</param>
/// <param name="FacingYaw">면 정면 방향(차체가 벽을 보는 방향) [rad].</param>
/// <param name="MinDistM">거리 탐색 시작 하한. 실제 시작은 차체가 면에서 떨어지는 첫 거리이고, 폭은 MaxDistM − MinDistM 이다.</param>
/// <param name="MinWallDistM">차체 중심과 검사 면 사이 최소 수평 거리 [m]. 0 보다 크면 그보다 가까운 정차는 쓰지 않는다
/// (영역 중심에서 면 안쪽 수평 방향으로 잰 거리 — 수직벽이면 벽면까지 거리). 수평면(MinDistM &lt; 0)에는 쓰지 않는다.</param>
public sealed record StationSearchSpace(
    double[] CenterXY, double[] InwardH, double FacingYaw, double FloorZ,
    double BaselineDistM, double BaselineLateralM = 0, double BaselineYawDeg = 0,
    double MinDistM = 0.4, double MaxDistM = 2.4, double DistStepM = 0.1,
    double MaxLateralM = 0.8, double LateralStepM = 0.1,
    double MaxYawDeg = 15, double YawStepDeg = 5,
    double MinBodyClearanceM = 0.05, double MinWallDistM = 0)
{
    /// <summary>차체가 설 수 있는 자리인가 — 면에서 <see cref="MinBodyClearanceM"/> 이상 떨어지고,
    /// <see cref="MinWallDistM"/> 가 있으면 차체 중심이 검사 면에서 그 거리 이상 떨어져야 한다.</summary>
    public bool BodyAllowed(ReachRobotModel model, IReadOnlyList<ReachPlane> planes, double distM, double lateralM, double yawDeg,
        out double clearanceM)
    {
        clearanceM = ReachSimulator.BodyAt(model, planes, StationOf(distM, lateralM, yawDeg)).ClearanceM;
        if (clearanceM < MinBodyClearanceM) return false;
        return MinWallDistM <= 0 || MinDistM < 0 || distM >= MinWallDistM - 1e-9;
    }

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
/// <param name="Error">정차 위치 오차 표본에서의 판정(<see cref="StationOptimizer.WithPoseError"/> 를 거친 후보만, 아니면 null).</param>
/// <param name="MarginCapDeg">순위에서 여유를 비교할 때의 상한(<see cref="ReachRobotModel.MarginRankCapDeg"/>).</param>
public sealed record StationCandidate(
    double DistM, double LateralM, double YawDeg, ReachStation Station, double BodyClearanceM, bool Feasible,
    int Clear, int Collision, int Unreachable, double Robustness, IReadOnlyList<ReachSeamSurvey> Seams,
    double? StrokeMm = null, int SafeClear = 0, double? TraverseMarginDeg = null, double MarginTargetDeg = ReachRobotModel.DefaultTraverseMarginTargetDeg,
    PoseErrorStats? Error = null, double MarginCapDeg = double.PositiveInfinity)
{
    /// <summary>
    /// 보장 여유 [도] — 제자리(오차 0)와 위치 오차 표본 전체에서 가장 작은 순회 관절 여유. 오차 판정이 없으면 제자리 여유 그대로.
    /// </summary>
    public double? GuaranteedMarginDeg => Error is null ? TraverseMarginDeg
        : (TraverseMarginDeg, Error.MinMarginDeg) switch
        {
            ({ } a, { } b) => Math.Min(a, b),
            ({ } a, null) => a,
            (null, var b) => b,
        };
}

/// <summary>
/// 정차 위치 오차(SLAM·도착 오차) 조건 — 고른 정차에서 반경 <paramref name="RadiusM"/> 의 8방향(전후·좌우·대각선)과
/// 방향 ±<paramref name="YawDeg"/> 로 어긋난 자리에서도 판정한다. 현장에서 SLAM 오차 300 mm 이상이 관측됐다(2026-11).
/// </summary>
/// <param name="RadiusM">위치 오차 반경 [m] (0 = 위치 표본 없음).</param>
/// <param name="YawDeg">방향 오차 [도] (0 = 방향 표본 없음).</param>
/// <param name="TopCandidates">영역 최적화에서 오차 판정을 할 상위 후보 수(한 묶음) — 계산량 때문에 모든 후보에 하지 않는다.</param>
/// <param name="MaxBatches">상위 묶음에 오차를 견디는 후보가 없으면 다음 묶음을 더 본다 — 그 최대 묶음 수.</param>
public sealed record PoseErrorOptions(double RadiusM = 0.3, double YawDeg = 3, int TopCandidates = 6, int MaxBatches = 3)
{
    public bool Enabled => RadiusM > 0 || YawDeg > 0;
}

/// <summary>위치 오차 표본에서의 판정 — 표본 중 가장 나쁜 값.</summary>
/// <param name="Samples">본 표본 수(8방향 + 방향 ±).</param>
/// <param name="WorstMiss">표본 중 도달 불가 용접선 수의 최댓값(0 = 어느 표본에서도 전부 도달).</param>
/// <param name="WorstCollision">표본 중 간섭 용접선 수의 최댓값(차체가 면에 닿는 표본은 전부 간섭으로 센다).</param>
/// <param name="MinMarginDeg">표본 전체에서 닿는 용접선들의 순회 최소 관절 여유 [도](순회 판정이 없으면 null).</param>
public sealed record PoseErrorStats(int Samples, int WorstMiss, int WorstCollision, double? MinMarginDeg, double RadiusM, double YawDeg);

/// <summary>최적화 결과 — 기본 정차와 찾은 정차, 평가한 후보 수.</summary>
public sealed record StationOptimum(StationCandidate Baseline, StationCandidate Best, int Evaluated);

/// <summary>배차 직전 정차 선택 조건.</summary>
/// <param name="RobustM">도착 오차 [m] — 고른 정차에서 이 반경의 8방향(전후·좌우·대각선)으로 어긋나도 전부 닿아야 한다(0 = 위치 표본 없음 — 방향 오차까지 0 이어야 검사하지 않는다).</param>
/// <param name="MaxEvaluations">조사할 후보 수 상한(계산 시간 상한).</param>
/// <param name="StepFactor">후보 격자 간격 = 탐색 간격 × 이 값.</param>
/// <param name="InPlace">제자리 회전 후보(로봇 현재 위치 그대로, 방향만 바꿈)를 볼지.</param>
/// <param name="InPlaceMaxM">로봇이 영역 중심에서 이보다 멀면 제자리 회전 후보를 보지 않는다 [m].</param>
/// <param name="RobustYawDeg">도착 방향 오차 [도] — 이만큼 틀어져도 전부 닿아야 한다(0 = 방향은 보지 않음).</param>
public sealed record NearestOptions(double RobustM = 0.3, int MaxEvaluations = 600, int StepFactor = 2,
    bool InPlace = true, double InPlaceMaxM = 2.5, double RobustYawDeg = 3);

/// <summary>
/// 배차 직전 정차 선택 결과. Full = 전부 간섭 없음·순회 여유 확보(false 면 전부 도달만 만족). Travel = 로봇 현재 위치에서의 직선 거리 [m].
/// </summary>
public sealed record NearestStation(StationCandidate Pick, StationCandidate Baseline, bool Full, int Evaluated,
    double TravelM, double BaselineTravelM);

/// <summary>
/// 영역 정차점 최적화 — 후보 정차마다 영역의 모든 용접선을 공통 리프트 높이(<see cref="AreaLift"/>)로 조사해
/// ① 도면 순회 관절 여유를 목표 이상 확보하고 간섭 없이 닿는 용접선 수 ② 간섭 없이 닿는 수 ③ 순회 최소 관절 여유(목표까지)
/// ④ 면 간섭 여유(<see cref="StationCandidate.Robustness"/>) ⑤ 기본 정차에서 덜 옮긴 쪽 순으로 고른다.
/// 탐색 = 거리×좌우 성긴 격자(수평면은 네 방향 포함) → 최상위 몇 곳에서 세 축 패턴 탐색(정해진 간격). 같은 후보는 한 번만 계산한다.
/// 위치 오차 조건(<see cref="PoseErrorOptions"/>)을 주면 상위 후보를 오차 표본에서 다시 판정해, 오차에서도 전부 닿고 보장 여유
/// (제자리·오차 표본 중 최소 순회 여유)가 큰 정차를 고른다 — 순위 전체는 <see cref="Ranking"/>.
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
        CancellationToken ct = default, string? wallCode = null, bool areaLift = true, PoseErrorOptions? poseError = null)
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
        // 지금 정차가 이미 최상 점수(모두 여유 확보·순회 여유가 비교 상한·면 간섭 여유가 기구상 최대)면 탐색하지 않는다 — 어느 후보도 점수를 올릴 수 없다.
        // 여유 비교 상한이 없거나(기본) 위치 오차를 볼 때는 "더 올릴 수 없음"을 알 수 없어 항상 탐색한다.
        if (poseError is not { Enabled: true } && !double.IsInfinity(model.MarginRankCapDeg)
            && IsTop(baseline, seams.Count, model.MarginRankCapDeg, RobustnessBound(model))) return new StationOptimum(baseline, baseline, 1);
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
        var evaluated = cache.Count;
        // ③ 위치 오차 — 상위 후보(와 지금 정차)를 오차 표본에서 다시 판정해 오차를 견디는 쪽을 고른다. 계산량 때문에 상위 묶음만 보고,
        // 그 묶음에 오차에서도 전부 닿는 후보가 없으면 다음 묶음을 더 본다(최대 MaxBatches 묶음).
        if (poseError is { Enabled: true } pe)
        {
            StationCandidate Robust(StationCandidate c)
            {
                var r = WithPoseError(model, planes, space, c, seams, wallNormal, strokesMm, pe, ct, wallCode, areaLift);
                evaluated += r.Error?.Samples ?? 0;
                return r;
            }
            baseline = Robust(baseline);
            var ranked = cache.Values.Where(c => c.Feasible && c.Clear > 0).OrderByDescending(c => c, Ranking).ToList();
            var scored = new List<StationCandidate>();
            var batch = Math.Max(1, pe.TopCandidates);
            for (var b = 0; b < Math.Max(1, pe.MaxBatches) && b * batch < ranked.Count; b++)
            {
                scored.AddRange(ranked.Skip(b * batch).Take(batch).Select(Robust));
                if (scored.Any(c => c.Unreachable == 0 && c.Error is { WorstMiss: 0 })) break;
            }
            winner = scored.Count > 0 ? scored.Max(Ranking)! : baseline;
        }
        // 순위가 나아질 때만 옮긴다 — 아무것도 닿지 않으면 옮길 이유가 없다(지금 정차 유지).
        var better = winner.Clear > 0 && Ranking.Compare(winner, baseline) > 0;
        if (!better) winner = baseline;
        return new StationOptimum(baseline, winner, evaluated);
    }

    /// <summary>
    /// 위치 오차 표본 — (거리, 좌우, 방향) 변위 목록. 반경 R 의 8방향(전후·좌우 ±R, 대각선 ±R/√2)과 제자리 방향 ±yaw.
    /// </summary>
    public static IReadOnlyList<(double DistM, double LateralM, double YawDeg)> PoseErrorSamples(PoseErrorOptions o)
    {
        var list = new List<(double, double, double)>(10);
        if (o.RadiusM > 0)
        {
            var d = o.RadiusM / Math.Sqrt(2);
            list.AddRange(new[]
            {
                (o.RadiusM, 0.0, 0.0), (-o.RadiusM, 0.0, 0.0), (0.0, o.RadiusM, 0.0), (0.0, -o.RadiusM, 0.0),
                (d, d, 0.0), (d, -d, 0.0), (-d, d, 0.0), (-d, -d, 0.0),
            });
        }
        if (o.YawDeg > 0)
        {
            list.Add((0, 0, o.YawDeg));
            list.Add((0, 0, -o.YawDeg));
        }
        return list;
    }

    /// <summary>
    /// 후보 정차를 위치 오차 표본마다 다시 판정해 <see cref="StationCandidate.Error"/> 를 채운다 — 도달뿐 아니라 간섭과 순회 최소 여유까지.
    /// 리프트는 후보가 고른 높이로 고정한다(AMR 은 지령받은 높이로 선다). 차체 여유·면 최소 거리 규칙은 오차 표본에는 적용하지 않고
    /// (계획 규칙이지 도착 오차의 판정이 아니다), 차체가 실제로 면에 닿는 표본만 전부 간섭으로 센다.
    /// </summary>
    public static StationCandidate WithPoseError(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space, StationCandidate c,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm,
        PoseErrorOptions options, CancellationToken ct = default, string? wallCode = null, bool areaLift = true)
    {
        var samples = PoseErrorSamples(options);
        if (samples.Count == 0 || seams.Count == 0) return c;
        var strokes = c.StrokeMm is { } s ? new[] { s } : strokesMm;
        int miss = 0, collision = 0;
        double? margin = null;
        foreach (var (dd, dl, dy) in samples)
        {
            ct.ThrowIfCancellationRequested();
            var n = Evaluate(model, planes, space, c.DistM + dd, c.LateralM + dl, c.YawDeg + dy, seams, wallNormal, strokes,
                surveyInfeasible: true, wallCode: wallCode, areaLift: areaLift);
            miss = Math.Max(miss, n.Unreachable);
            collision = Math.Max(collision, n.BodyClearanceM < 0 ? seams.Count : n.Collision);
            if (n.TraverseMarginDeg is { } m) margin = margin is { } cur ? Math.Min(cur, m) : m;
        }
        return c with { Error = new PoseErrorStats(samples.Count, miss, collision, margin, options.RadiusM, options.YawDeg) };
    }

    /// <summary>
    /// 배차 직전 정차 선택 — 로봇 현재 위치(<paramref name="fromXY"/>, 도면 x·y)에서 **가장 가까운** 정차 중 AMR 이 실제로 수행할 수 있는 곳을 고른다.
    /// 후보(저장된 정차 + 탐색 창 격자)를 가까운 순으로 보며 처음 통과하는 곳에서 멈춘다. 통과 조건:
    /// ① 차체 가능 ② 용접선 전부 도달 ③ 전부 간섭 없음·순회 여유 목표 이상 ④ 도착 오차만큼(8방향·방향 ±) 어긋나도(같은 리프트) 전부 도달.
    /// ③ 을 만족하는 곳이 없으면, 저장된 정차가 전부 도달이 아닐 때에 한해 ①②④ 만 만족하는 가장 가까운 곳을 쓴다.
    /// 그것도 없으면 null(저장된 정차 그대로).
    /// </summary>
    public static NearestStation? Nearest(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, StationSearchSpace space,
        IReadOnlyList<IReadOnlyList<double[]>> seams, double[] wallNormal, IReadOnlyList<double> strokesMm,
        double[] fromXY, NearestOptions? options = null, CancellationToken ct = default, string? wallCode = null,
        double? fromYaw = null)
    {
        if (seams.Count == 0) return null;
        var o = options ?? new NearestOptions();
        double Travel(ReachStation s) => Math.Sqrt(Math.Pow(s.X - fromXY[0], 2) + Math.Pow(s.Y - fromXY[1], 2));

        var baseline = Evaluate(model, planes, space, space.BaselineDistM, space.BaselineLateralM, space.BaselineYawDeg,
            seams, wallNormal, strokesMm, surveyInfeasible: true, wallCode: wallCode);
        var evaluated = 1;

        // 후보 = 탐색 창 격자(최적화와 같은 창, 간격 × StepFactor). 수평면은 90° 간격 네 방향, 벽면은 정면만. 저장된 방향도 함께 본다.
        var grid = new List<(double D, double L, double Y)>();
        if (FirstBodyFeasible(model, planes, space) is { } start)
        {
            var dLo = space.MinDistM < 0 ? start.DistM + space.MinDistM : start.DistM;
            var dHi = space.MinDistM < 0 ? start.DistM + space.MaxDistM : start.DistM + (space.MaxDistM - space.MinDistM);
            var ds = space.DistStepM * Math.Max(1, o.StepFactor);
            var ls = space.LateralStepM * Math.Max(1, o.StepFactor);
            var yaws = (space.MaxYawDeg >= 90 ? new[] { 0.0, 90, -90, 180, space.BaselineYawDeg } : new[] { 0.0, space.BaselineYawDeg })
                .Select(y => Math.Round(y, 3)).Distinct().ToArray();
            for (var d = dLo; d <= dHi + 1e-9; d += ds)
                for (var l = start.LateralM - space.MaxLateralM; l <= start.LateralM + space.MaxLateralM + 1e-9; l += ls)
                    foreach (var y in yaws)
                        grid.Add((d, l, y));
        }
        // 제자리 회전 후보 — 로봇이 지금 서 있는 자리 그대로, 방향만 바꾼다(주행 0). 영역에서 너무 멀면 보지 않는다.
        var relX = fromXY[0] - space.CenterXY[0];
        var relY = fromXY[1] - space.CenterXY[1];
        if (o.InPlace && Math.Sqrt(relX * relX + relY * relY) <= o.InPlaceMaxM && space.YawStepDeg > 0)
        {
            var hereD = relX * space.InwardH[0] + relY * space.InwardH[1];
            var hereL = -relX * space.InwardH[1] + relY * space.InwardH[0];
            var span = Math.Min(space.MaxYawDeg, 180);
            for (var y = -span; y <= span + 1e-9; y += space.YawStepDeg)
                if (y > -180 + 1e-9 || span < 180) grid.Add((hereD, hereL, Math.Round(y, 3)));   // −180 = +180
        }
        // 같은 거리면 덜 도는 쪽(로봇 방향을 알 때) — 모르면 저장된 방향에 가까운 쪽
        double Turn((double D, double L, double Y) c)
        {
            if (fromYaw is not { } fy) return Math.Abs(c.Y - space.BaselineYawDeg);
            var d = space.FacingYaw + c.Y * Math.PI / 180 - fy;
            return Math.Abs(Math.Atan2(Math.Sin(d), Math.Cos(d)));
        }
        var ordered = grid
            .Select(c => (C: c, S: space.StationOf(c.D, c.L, c.Y), IsBase: false))
            .Append((C: (baseline.DistM, baseline.LateralM, baseline.YawDeg), S: baseline.Station, IsBase: true))
            .OrderBy(x => Math.Round(Travel(x.S), 3)).ThenBy(x => Turn(x.C));

        bool Robust(StationCandidate c)
        {
            // 도착 오차 — 반경 RobustM 의 8방향(전후·좌우·대각선)과 방향 ±RobustYawDeg 에서도 전부 닿아야 한다.
            // 위치 반경이 0 이어도 방향 오차가 있으면 그 표본은 본다(둘 다 0 이하일 때만 검사하지 않는다).
            var samples = PoseErrorSamples(new PoseErrorOptions(Math.Max(0, o.RobustM), Math.Max(0, o.RobustYawDeg)));
            if (samples.Count == 0) return true;
            var strokes = c.StrokeMm is { } s ? new[] { s } : strokesMm;
            foreach (var (dd, dl, dy) in samples)
            {
                ct.ThrowIfCancellationRequested();
                var n = Evaluate(model, planes, space, c.DistM + dd, c.LateralM + dl, c.YawDeg + dy, seams, wallNormal, strokes, wallCode: wallCode);
                if (n.Feasible) evaluated++;
                if (!n.Feasible || n.Unreachable > 0) return false;
            }
            return true;
        }

        NearestStation Result(StationCandidate c, bool full) =>
            new(c, baseline, full, evaluated, Travel(c.Station), Travel(baseline.Station));

        StationCandidate? relaxed = null;
        foreach (var x in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (evaluated >= o.MaxEvaluations) break;
            StationCandidate c;
            if (x.IsBase) c = baseline;
            else
            {
                c = Evaluate(model, planes, space, x.C.Item1, x.C.Item2, x.C.Item3, seams, wallNormal, strokesMm, wallCode: wallCode);
                if (c.Feasible) evaluated++;
            }
            if (!c.Feasible || c.Unreachable > 0) continue;
            var full = c.Collision == 0 && c.SafeClear == seams.Count;
            if (!full && relaxed is not null) continue;
            if (!Robust(c)) continue;
            if (full) return Result(c, true);
            relaxed = c;
        }
        // 조건을 다 채우는 곳이 없다 — 저장된 정차가 전부 도달이면 그대로 두고, 아니면 전부 닿는 가장 가까운 곳으로.
        var baselineOk = baseline.Feasible && baseline.Unreachable == 0;
        return relaxed is not null && !baselineOk ? Result(relaxed, false) : null;
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
                if (space.BodyAllowed(model, planes, d, l, 0, out _))
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
            if (space.BodyAllowed(model, planes, d, l, 0, out _))
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
        var feasible = space.BodyAllowed(model, planes, distM, lateralM, yawDeg, out var body);
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
            clear.Count(r => r.TraverseMarginDeg is not { } m || m >= cap), margins.Count == 0 ? null : margins.Min(), cap,
            MarginCapDeg: model.MarginRankCapDeg);
    }

    /// <summary>
    /// 영역 판정 점수 — (여유 확보 간섭 없음 수, 간섭 없음 수, 도달 수, 순회 최소 여유 키, 도달 불가 수). 여유 확보 = Clear 이고 도면 순회 여유 ≥ 목표
    /// (순회 판정이 없으면 Clear 그대로). 최소 여유는 닿는 용접선 중 최소를 0.5° 로 반올림한다(<see cref="MarginKey"/> — 상한 <paramref name="marginCapDeg"/> 이상은 같게).
    /// </summary>
    public static (int Safe, int Clear, int Reach, double Margin, int Miss) Score(IReadOnlyList<ReachSeamSurvey> seams, double marginTargetDeg,
        double marginCapDeg = double.PositiveInfinity)
    {
        var margins = seams.Where(r => r.Verdict != ReachVerdict.Unreachable && r.TraverseMarginDeg is not null)
            .Select(r => r.TraverseMarginDeg!.Value).ToList();
        return (seams.Count(r => r.Verdict == ReachVerdict.Clear && (r.TraverseMarginDeg is not { } m || m >= marginTargetDeg)),
            seams.Count(r => r.Verdict == ReachVerdict.Clear), seams.Count(r => r.Verdict != ReachVerdict.Unreachable),
            MarginKey(margins.Count == 0 ? null : margins.Min(), marginTargetDeg, marginCapDeg),
            seams.Count(r => r.Verdict == ReachVerdict.Unreachable));
    }

    /// <summary>
    /// 더 올릴 수 없는 점수인가 — 차체 가능·모든 용접선 여유 확보·순회 최소 여유 ≥ 비교 상한(<paramref name="marginCapDeg"/>)·면 간섭 여유 = 도달 가능한 최대(<paramref name="robustBoundM"/>).
    /// 순위에서 이보다 앞서는 후보는 없다(남은 기준은 정차 위치뿐). 비교 상한이 없으면(무한대) 여유는 늘 더 커질 수 있어 false 다.
    /// </summary>
    /// <param name="robustBoundM">면 간섭 여유가 가질 수 있는 최대 — <see cref="RobustnessBound"/>. 없으면 <see cref="RobustCapM"/>.</param>
    public static bool IsTop(StationCandidate c, int seamCount, double marginCapDeg, double robustBoundM = RobustCapM) =>
        c.Feasible && seamCount > 0 && c.SafeClear == seamCount && !double.IsInfinity(marginCapDeg)
        && MarginKey(c.TraverseMarginDeg, marginCapDeg, marginCapDeg) >= marginCapDeg
        && Math.Round(c.Robustness, 3) >= Math.Round(Math.Min(robustBoundM, RobustCapM), 3);

    /// <summary>
    /// 면 간섭 여유(<see cref="StationCandidate.Robustness"/>)의 상한 [m] — 초점 자세는 TCP 가 용접선 면에서 초점 이격만큼 떨어지고 광축이 면에 수직이라
    /// 툴 상자의 가장 먼 끝(플랜지 z 최대)과 그 면 사이가 (공구 z + 초점 이격 − 툴 끝 z) 로 정해진다. 어느 정차도 이보다 큰 여유를 낼 수 없다
    /// (실기 모델 365 + 10 − 276.5 = 98.5 mm). <see cref="RobustCapM"/> 보다 크면 그 값.
    /// </summary>
    public static double RobustnessBound(ReachRobotModel m) =>
        m.ToolBoxes.Count == 0 || m.StageStandoffsMm.Length == 0 ? RobustCapM
        : Math.Min(RobustCapM, Math.Max(0, (m.ToolZMm + m.StageStandoffsMm[^1] - m.ToolBoxes.Max(b => b.Max[2])) / 1000.0));

    /// <summary>순위용 최소 여유 — 0.5° 단위, 상한(<paramref name="capDeg"/>, 기본 없음) 이상은 같게. 순회 판정이 없으면(null) 목표.</summary>
    public static double MarginKey(double? marginDeg, double targetDeg, double capDeg = double.PositiveInfinity) =>
        marginDeg is { } m
            ? (double.IsInfinity(Math.Min(m, capDeg)) ? Math.Min(m, capDeg) : Math.Round(Math.Min(m, capDeg) * 2, MidpointRounding.AwayFromZero) / 2)
            : Math.Min(targetDeg, capDeg);

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
    /// 순위 — 가능 &gt; 전부 도달(도달 불가 0) &gt; <b>위치 오차에서도 전부 도달</b> &gt; 여유 확보 Clear 수 &gt; Clear 수 &gt;
    /// <b>오차에서 도달 불가가 적은 쪽</b> &gt; <b>오차에서 간섭 없음</b> &gt; <b>보장 여유</b>(제자리·오차 표본 중 최소, 0.5°) &gt;
    /// 제자리 순회 최소 여유(0.5°, 상한 <see cref="StationCandidate.MarginCapDeg"/>) &gt; 면 간섭 여유(1 mm 단위) &gt; 덜 옮김(좌우·방향·거리).
    /// 굵은 기준은 두 후보 모두 오차 판정(<see cref="StationCandidate.Error"/>)이 있을 때만 쓴다.
    /// </summary>
    public static readonly IComparer<StationCandidate> Ranking = Comparer<StationCandidate>.Create((a, b) =>
    {
        var c = a.Feasible.CompareTo(b.Feasible);
        if (c != 0) return c;
        c = (a.Unreachable == 0).CompareTo(b.Unreachable == 0);
        if (c != 0) return c;
        var both = a.Error is not null && b.Error is not null;
        if (both)
        {
            c = (a.Error!.WorstMiss == 0).CompareTo(b.Error!.WorstMiss == 0);
            if (c != 0) return c;
        }
        c = a.SafeClear.CompareTo(b.SafeClear);
        if (c != 0) return c;
        c = a.Clear.CompareTo(b.Clear);
        if (c != 0) return c;
        if (both)
        {
            c = b.Error!.WorstMiss.CompareTo(a.Error!.WorstMiss);
            if (c != 0) return c;
            c = (a.Error.WorstCollision == 0).CompareTo(b.Error.WorstCollision == 0);
            if (c != 0) return c;
            c = MarginKey(a.GuaranteedMarginDeg, a.MarginTargetDeg, a.MarginCapDeg).CompareTo(MarginKey(b.GuaranteedMarginDeg, b.MarginTargetDeg, b.MarginCapDeg));
            if (c != 0) return c;
        }
        c = MarginKey(a.TraverseMarginDeg, a.MarginTargetDeg, a.MarginCapDeg).CompareTo(MarginKey(b.TraverseMarginDeg, b.MarginTargetDeg, b.MarginCapDeg));
        if (c != 0) return c;
        c = Math.Round(a.Robustness, 3).CompareTo(Math.Round(b.Robustness, 3));
        if (c != 0) return c;
        c = (Math.Abs(b.LateralM) + Math.Abs(b.YawDeg) / 50).CompareTo(Math.Abs(a.LateralM) + Math.Abs(a.YawDeg) / 50);
        return c != 0 ? c : Math.Abs(b.DistM).CompareTo(Math.Abs(a.DistM));
    });
}
