using System.Globalization;
using System.Text;
using HD.Acs.Core.Reach;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 정차·리치 시뮬레이션 보고서 백데이터(CSV) — 면·영역·TASK 마다 운영 조건(배차와 같은 정차점, 저장된 영역 리프트 — 없으면 0)에서
/// 검사 가능 여부·불가 사유·개선 제안과 세 자세의 관절각, IK 해 전부를 낸다. 조회 전용 — DB·코봇을 바꾸지 않는다.
/// </summary>
public sealed partial class ReachSimService
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly string[] PointNames = { "시작", "중간", "끝" };

    /// <summary>TASK 요약 CSV 에 관절각을 싣는 판정 자세 — (용접선 점, 자세). 시작점은 세 자세, 중간·끝점은 초점 자세만 판정한다.</summary>
    private static readonly (int Point, ReachStage Stage)[] JudgedPoses =
    {
        (0, ReachStage.Approach), (0, ReachStage.Align), (0, ReachStage.Focus), (1, ReachStage.Focus), (2, ReachStage.Focus),
    };

    public static readonly string[] ReportTaskColumns = new[]
        {
            "선창", "층", "면", "영역", "영역ID", "TASK순번", "TASK이름", "TASKID", "용접유형",
            "정차X[m]", "정차Y[m]", "정차방향[deg]", "정차지정", "방향후보수", "선택방향순번", "리프트[mm]", "리프트출처",
            "시작X[m]", "시작Y[m]", "시작Z[m]", "끝X[m]", "끝Y[m]", "끝Z[m]", "시작u[m]", "시작v[m]", "끝u[m]", "끝v[m]",
            "길이[mm]", "바닥위높이min[m]", "바닥위높이max[m]",
            "검사가능", "판정코드", "불가사유분류", "불가사유상세", "개선제안",
            "최소간섭여유[mm]", "최근접부품", "최근접대상", "최근접자세", "차체여유[mm]",
            "진입경로", "진입경로상세", "순회판정", "순회최소여유[deg]", "순회제한항목", "순회비틀림[deg]",
            "순회최악지점[mm]", "순회최악기울기[deg]",
        }
        .Concat(JudgedPoses.SelectMany(p => Enumerable.Range(1, 6).Select(k => $"{PointNames[p.Point]}-{StageName(p.Stage)} J{k}[deg]")))
        .Concat(new[]
        {
            "시작-② 접근 IK해수", "시작-② 접근 허용해수",
            "권장영역리프트[mm]", "권장영역리프트판정", "용접선단독최적리프트[mm]", "용접선단독최적판정",
            "제안정차X[m]", "제안정차Y[m]", "제안정차방향[deg]", "제안리프트[mm]", "제안정차판정", "제안정차순회여유[deg]",
        }).ToArray();

    public static readonly string[] ReportIkColumns =
    {
        "선창", "층", "면", "영역", "TASK순번", "TASK이름", "TASKID", "정차X[m]", "정차Y[m]", "정차방향[deg]", "리프트[mm]",
        "용접선점", "자세", "면이격[mm]", "판정대상", "TCP X[m]", "TCP Y[m]", "TCP Z[m]",
        "해번호", "운영선택", "J1[deg]", "J2[deg]", "J3[deg]", "J4[deg]", "J5[deg]", "J6[deg]",
        "자세허용", "자세여유[deg]", "제한항목", "최소간섭여유[mm]", "최근접부품", "최근접대상", "비고",
    };

    /// <summary>
    /// 보고서 백데이터를 만든다. 선창이 없으면 null.
    /// <paramref name="optimize"/> 면 검사 가능이 아닌 용접선이 있는 영역마다 정차점 최적화를 돌려 제안 정차를 싣는다(영역당 수 초~수십 초).
    /// </summary>
    public async Task<ReachReport?> ReportAsync(string tankId, int? level, string? wallCode, Guid? areaId, bool optimize,
        CancellationToken ct, ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = _baseModel with { BranchRule = branch };
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (areaId is { } id) q = q.Where(a => a.AreaId == id);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        var taskRows = new StringBuilder();
        var ikRows = new StringBuilder();
        int nAreas = 0, nTasks = 0, ok = 0, caution = 0, bad = 0;
        foreach (var a in areas)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall) || a.Tasks.Count == 0) continue;
            nAreas++;
            foreach (var status in ReportArea(a, wall, Setup(ctx, a, wall), optimize, taskRows, ikRows, ct))
            {
                nTasks++;
                if (status == StatusOk) ok++;
                else if (status == StatusCaution) caution++;
                else bad++;
            }
        }
        return new ReachReport(CsvLine(ReportTaskColumns), taskRows.ToString(), CsvLine(ReportIkColumns), ikRows.ToString(),
            nAreas, nTasks, ok, caution, bad);
    }

    private const string StatusOk = "가능";
    private const string StatusCaution = "가능(주의)";
    private const string StatusBad = "불가";

    /// <summary>용접선 하나를 운영 조건에서 푼 결과 — 조사 판정, 점별 세 자세, 도면 순회.</summary>
    private sealed record TaskEval(AreaTaskEntity Task, double[][] Pts, ReachSeamSurvey Survey, ReachStation Station,
        ReachPointResult[] Points, ReachTraversal? Traversal, string Status, string Category, string Reason,
        (double Mm, string Part, string Plane, string Pose)? Nearest);

    private List<string> ReportArea(InspectionAreaEntity a, WallEntity wall, AreaSetup setup, bool optimize,
        StringBuilder taskRows, StringBuilder ikRows, CancellationToken ct)
    {
        var lift = Math.Clamp(a.StationLiftMm ?? 0, 0, _model.StrokeMaxMm);
        var tasks = a.Tasks.OrderBy(t => t.Seq).ToList();
        var evals = new TaskEval[tasks.Count];
        Parallel.For(0, tasks.Count, new ParallelOptions { CancellationToken = ct }, i => evals[i] = EvaluateTask(a, setup, tasks[i], lift));

        // 가능이 아닌 용접선이 있을 때만 대안을 푼다 — 리프트 전 높이(정차 그대로), 선택 시 정차점 최적화.
        AreaLiftResult? alt = null;
        StationOptimum? opt = null;
        if (evals.Any(e => e.Status != StatusOk))
        {
            var seams = evals.Select(e => (IReadOnlyList<double[]>)e.Pts).ToList();
            alt = AreaLift.Evaluate(_model, setup.Planes, setup.Stations, seams, setup.Normal, Strokes, a.WallCode);
            if (optimize && SearchSpace(a, wall, setup) is { } space)
                opt = StationOptimizer.Optimize(_model, setup.Planes, space, seams, setup.Normal, Strokes, ct, a.WallCode);
        }
        var moved = opt is not null && (Math.Abs(opt.Best.Station.X - opt.Baseline.Station.X) > 1e-6
            || Math.Abs(opt.Best.Station.Y - opt.Baseline.Station.Y) > 1e-6 || Math.Abs(opt.Best.Station.Yaw - opt.Baseline.Station.Yaw) > 1e-6
            || opt.Best.StrokeMm is { } bs && Math.Abs(bs - lift) > 1e-6);

        var manual = a.StationX is not null || a.StationY is not null || a.StationTheta is not null;
        for (var i = 0; i < evals.Length; i++)
        {
            var e = evals[i];
            var t = e.Task;
            var st = e.Station;
            var fixedAlt = alt?.Fixed[i];
            var bestAlt = alt?.Best[i];
            var optSeam = moved && i < opt!.Best.Seams.Count ? opt.Best.Seams[i] : null;
            var suggestion = e.Status == StatusOk ? "" : Suggest(e, lift, alt, i, opt, optSeam, optimize);
            var entry = e.Points[0].Entry;
            var trav = e.Traversal;
            var approach = e.Points[0].Stages.FirstOrDefault(s => s.Stage == ReachStage.Approach);

            var row = new List<string?>
            {
                a.TankId, $"L{a.Level}", a.WallCode, a.Name, a.AreaId.ToString(), t.Seq.ToString(Ci), t.Name, t.TaskId.ToString(), t.SeamType,
                N(st.X, 3), N(st.Y, 3), N(Deg(st.Yaw), 1), manual ? "수동" : "자동", setup.Stations.Count.ToString(Ci),
                (e.Survey.YawIndex + 1).ToString(Ci), N(lift, 0), a.StationLiftMm is null ? "기본 0" : "저장값",
                N(e.Pts[0][0], 4), N(e.Pts[0][1], 4), N(e.Pts[0][2], 4), N(e.Pts[2][0], 4), N(e.Pts[2][1], 4), N(e.Pts[2][2], 4),
                N(t.StartU, 4), N(t.StartV, 4), N(t.EndU, 4), N(t.EndV, 4),
                N(Math.Sqrt(Math.Pow(t.EndU - t.StartU, 2) + Math.Pow(t.EndV - t.StartV, 2)) * 1000, 0),
                N(e.Pts.Min(p => p[2]) - setup.FloorZ, 3), N(e.Pts.Max(p => p[2]) - setup.FloorZ, 3),
                e.Status, e.Survey.Verdict.ToString(), e.Category, e.Reason, suggestion,
                e.Nearest is { } n ? N(n.Mm, 1) : "", e.Nearest?.Part, e.Nearest?.Plane, e.Nearest?.Pose,
                N(e.Points[0].Body.ClearanceM * 1000, 1),
                entry is null ? "해당 없음" : entry.Ok ? "있음" : "없음", entry?.Summary,
                trav is null ? "미판정" : trav.Ok ? "통과" : "불가",
                trav is { } tv && double.IsFinite(tv.MinMarginDeg) ? N(tv.MinMarginDeg, 1) : "", trav?.Limiting,
                trav is null ? "" : N(trav.TwistDeg, 0), trav?.Worst is { } w ? N(w.AlongMm, 0) : "", trav?.Worst is { } w2 ? N(w2.TiltDeg, 1) : "",
            };
            foreach (var (p, stage) in JudgedPoses)
            {
                var j = e.Points[p]?.Stages.FirstOrDefault(s => s.Stage == stage)?.Chosen?.JointsDeg;
                for (var k = 0; k < 6; k++) row.Add(j is { Length: > 5 } ? N(j[k], 2) : "");
            }
            row.Add(approach?.Solutions.Count.ToString(Ci) ?? "0");
            row.Add(approach?.Solutions.Count(s => s.PostureOk).ToString(Ci) ?? "0");
            row.Add(alt is null ? "" : N(alt.StrokeMm, 0));
            row.Add(fixedAlt is null ? "" : VerdictText(fixedAlt));
            row.Add(bestAlt?.StrokeMm is { } bh ? N(bh, 0) : "");
            row.Add(bestAlt is null ? "" : VerdictText(bestAlt));
            if (optSeam is not null)
            {
                var b = opt!.Best;
                row.AddRange(new[] { N(b.Station.X, 3), N(b.Station.Y, 3), N(Deg(b.Station.Yaw), 1),
                    b.StrokeMm is { } h ? N(h, 0) : "", VerdictText(optSeam), "" });
                row[^1] = optSeam.TraverseMarginDeg is { } m && double.IsFinite(m) ? N(m, 1) : "";
            }
            else row.AddRange(Enumerable.Repeat("", 6));
            taskRows.Append(CsvLine(row)).Append("\r\n");

            AppendIkRows(ikRows, a, e, lift);
        }
        return evals.Select(e => e.Status).ToList();
    }

    /// <summary>용접선 하나 — 운영 리프트에서 조사 판정(정본)과 같은 순서로 시작·중간·끝 점의 세 자세·진입·순회를 형상까지 푼다.</summary>
    private TaskEval EvaluateTask(InspectionAreaEntity a, AreaSetup setup, AreaTaskEntity t, double lift)
    {
        var pts = SeamPoints(setup.Pose, t);
        var survey = ReachSimulator.Survey(_model, setup.Planes, setup.Stations, pts, setup.Normal, new[] { lift }, a.WallCode);
        var st = setup.Stations[Math.Clamp(survey.YawIndex, 0, setup.Stations.Count - 1)];
        var points = new ReachPointResult[pts.Length];
        double[]? seed = null;
        for (var i = 0; i < pts.Length; i++)
        {
            points[i] = ReachSimulator.Evaluate(_model, setup.Planes, st, pts[i], setup.Normal, lift, wallCode: a.WallCode, seedJoints: seed);
            seed = ReachSimulator.ScanSeed(points[i]);
            if (seed is null)
            {
                // 앞 점에서 막히면 운영도 더 가지 않는다 — 뒤 점은 참고로 독립 평가(진입 검사 없이)해 IK 해 목록만 싣는다.
                for (var k = i + 1; k < pts.Length; k++)
                    points[k] = ReachSimulator.Evaluate(_model, setup.Planes, st, pts[k], setup.Normal, lift, checkEntry: false);
                break;
            }
        }
        var trav = ReachSimulator.TraverseSeam(_model, st, lift, pts, setup.Normal, ReachSimulator.TraverseSeed(points[0]));

        // 가장 가까운 부품 — 차체와 판정 자세 운영 선택 해의 부품.
        (double Mm, string Part, string Plane, string Pose)? nearest =
            (points[0].Body.ClearanceM * 1000, "AMR 차체", points[0].Body.NearestPlane, "정차");
        for (var i = 0; i < points.Length; i++)
            foreach (var s in points[i].JudgedStages)
                foreach (var p in s.Chosen?.Parts ?? Array.Empty<ReachPart>())
                    if (p.ClearanceM * 1000 < nearest.Value.Mm)
                        nearest = (p.ClearanceM * 1000, p.Name, p.NearestPlane, $"{PointNames[i]}-{StageName(s.Stage)}");

        var (category, reason) = Reason(survey, points, trav, nearest.Value, setup.Stations.Count);
        // 도달 불가 사유와 별개로 간섭도 있으면 함께 적는다(사유 하나만 풀어서는 가능해지지 않는다).
        if (survey.Verdict == ReachVerdict.Unreachable && nearest.Value.Mm < 0)
            reason += $" / 추가로 {nearest.Value.Pose} 자세에서 {nearest.Value.Part} 이(가) {nearest.Value.Plane} 을(를) {N(-nearest.Value.Mm, 0)} mm 침범";
        var margin = survey.TraverseMarginDeg;
        var status = survey.Verdict != ReachVerdict.Clear ? StatusBad
            : margin is { } m && m < _model.TraverseMarginTargetDeg ? StatusCaution
            : nearest.Value.Mm < _model.WarnClearanceM * 1000 ? StatusCaution
            : StatusOk;
        if (status == StatusCaution)
        {
            category = margin is { } m2 && m2 < _model.TraverseMarginTargetDeg ? "순회 관절 여유 부족" : "간섭 여유 부족";
            reason = category == "순회 관절 여유 부족"
                ? $"도면 순회 최소 관절 여유 {N(margin!.Value, 1)}° < 목표 {N(_model.TraverseMarginTargetDeg, 0)}° ({trav?.Limiting}) — 정차 위치 오차에 취약"
                : $"{nearest.Value.Pose} 자세에서 {nearest.Value.Part} ↔ {nearest.Value.Plane} 여유 {N(nearest.Value.Mm, 0)} mm < 경고 {N(_model.WarnClearanceM * 1000, 0)} mm";
        }
        return new TaskEval(t, pts, survey, st, points, trav, status, category, reason, nearest);
    }

    /// <summary>불가 사유 — 운영이 막히는 첫 단계(점 순서 → 세 자세 → 가지 전환 → 진입 경로 → 도면 순회 → 간섭).</summary>
    private static (string Category, string Reason) Reason(ReachSeamSurvey survey, ReachPointResult[] points, ReachTraversal? trav,
        (double Mm, string Part, string Plane, string Pose) nearest, int yawCount)
    {
        if (survey.Verdict == ReachVerdict.Clear) return ("", "");
        var prefix = yawCount > 1 && survey.Verdict == ReachVerdict.Unreachable ? $"차체 방향 후보 {yawCount}개 모두 불가(첫 방향 기준) — " : "";
        for (var i = 0; i < points.Length; i++)
        {
            var r = points[i];
            if (r.Reachable) continue;
            var at = PointNames[i] + "점";
            if (r.JudgedStages.FirstOrDefault(s => s.Chosen is not { PostureOk: true }) is { } stage)
            {
                var name = $"{at} {StageName(stage.Stage)}(면 이격 {N(stage.StandoffMm, 0)} mm)";
                if (stage.Solutions.Count == 0)
                    return ("IK 해 없음(리치 밖)", $"{prefix}{name}: IK 해가 없음 — 목표 TCP 가 코봇 작업 반경 밖");
                var allowed = stage.Solutions.Count(s => s.PostureOk);
                var c = stage.Chosen;
                return ("관절 한계·특이점", $"{prefix}{name}: " + (c is null ? "운영 선택 해 없음"
                    : $"운영 선택 해가 {c.Limiting}(자세 여유 {N(c.MarginDeg, 1)}°)") +
                    $" — IK 해 {stage.Solutions.Count}개 중 자세 허용 {allowed}개" +
                    (allowed > 0 ? "(허용 해는 운영 출발 자세·앞 자세와 다른 가지)" : ""));
            }
            if (r.BranchChange is { } bc) return ("가지 전환", $"{prefix}{at}: {bc}");
            if (r.Entry is { Ok: false } en) return ("진입 경로 없음", $"{prefix}{at}: {en.Summary}");
        }
        if (trav is { Ok: false }) return ("도면 순회 불가", prefix + (trav.Blocker ?? "도면 순회 경유점 사슬이 이어지지 않음"));
        if (survey.Verdict == ReachVerdict.Collision)
            return (nearest.Part == "AMR 차체" ? "차체 간섭" : "코봇·툴 간섭",
                $"{nearest.Pose} 자세에서 {nearest.Part} 이(가) {nearest.Plane} 을(를) {N(-nearest.Mm, 0)} mm 침범");
        return ("도달 불가", prefix + (survey.Blocker ?? "사유 미상"));
    }

    /// <summary>개선 제안 — 계산으로 확인한 대안(영역 리프트 변경 → 용접선 단독 리프트 → 정차 이동)을 먼저, 없으면 사유별 일반 조치.</summary>
    private string Suggest(TaskEval e, double lift, AreaLiftResult? alt, int i, StationOptimum? opt, ReachSeamSurvey? optSeam, bool optimize)
    {
        var target = _model.TraverseMarginTargetDeg;
        bool Clear(ReachSeamSurvey? s) => s is { Verdict: ReachVerdict.Clear };
        bool Safe(ReachSeamSurvey? s) => Clear(s) && (s!.TraverseMarginDeg is not { } m || m >= target);
        string Margin(ReachSeamSurvey s) => s.TraverseMarginDeg is { } m && double.IsFinite(m) ? $"순회 여유 {N(m, 1)}°" : "순회 여유 미판정";
        // 지금보다 나아지는 대안만 제안한다 — 불가면 간섭 없이 닿으면 되고, 주의면 여유까지 확보해야 한다.
        bool Better(ReachSeamSurvey? s) => e.Status == StatusBad ? Clear(s) : Safe(s);

        var list = new List<string>();
        if (alt is not null)
        {
            var (f, b) = (alt.Fixed[i], alt.Best[i]);
            if (Math.Abs(alt.StrokeMm - lift) > 1e-6 && Better(f))
                list.Add($"영역 리프트를 {N(lift, 0)} → {N(alt.StrokeMm, 0)} mm 로 변경(정차 그대로)하면 가능 — {Margin(f)}" +
                         (Safe(f) ? "" : $"(목표 {N(target, 0)}° 미만)") +
                         $", 영역 전체 간섭 없음 {alt.FixedClear}/{alt.Fixed.Count}");
            else if (b.StrokeMm is { } bh && Math.Abs(bh - lift) > 1e-6 && Better(b))
                list.Add($"이 용접선만 리프트 {N(bh, 0)} mm 면 가능({Margin(b)}) — 영역 공통 리프트로는 다른 용접선과 양립하지 않으므로 영역 분리(리프트별 묶음) 검토");
        }
        if (optSeam is not null && Better(optSeam))
        {
            var b = opt!.Best;
            list.Add($"정차를 도면 ({N(b.Station.X, 3)}, {N(b.Station.Y, 3)}) · 방향 {N(Deg(b.Station.Yaw), 1)}°" +
                     (b.StrokeMm is { } h ? $" · 리프트 {N(h, 0)} mm" : "") +
                     $" 로 옮기면 가능 — {Margin(optSeam)}, 영역 전체 간섭 없음 {b.Clear}/{b.Seams.Count} (리치·간섭 ▸ 정차점 최적화 적용)");
        }
        if (list.Count > 0) return string.Join(" / ", list);

        var general = e.Category switch
        {
            "IK 해 없음(리치 밖)" => "정차점을 용접선 쪽으로 가깝게 옮기거나 리프트 높이를 조정",
            "관절 한계·특이점" => "정차 거리·좌우·방향을 조정해 해당 관절 여유를 확보",
            "가지 전환" => "출발 자세와 같은 가지로 닿도록 정차 좌우·거리를 조정",
            "진입 경로 없음" => "정차를 옮겨 출발 자세 TCP 와 접근점의 면 방향 거리를 줄이고 진입 중 자기 간섭을 피함",
            "도면 순회 불가" or "순회 관절 여유 부족" => "용접선이 정차 정면에 오도록 정차를 좌우로 옮기거나 영역을 나눠 한 정차의 용접선 묶음을 줄임",
            "차체 간섭" => $"정차를 면에서 {N(Math.Max(0, -(e.Nearest?.Mm ?? 0)) + _model.WarnClearanceM * 1000, 0)} mm 이상 안쪽으로 이동",
            "코봇·툴 간섭" or "간섭 여유 부족" => "간섭 부품이 면에서 떨어지도록 정차 거리·리프트를 조정",
            _ => "정차 위치·리프트 조정",
        };
        return opt is not null || optimize
            ? $"{general} — 현재 영역 범위에서는 정차점 최적화·리프트 변경으로 해결되지 않음: 영역 분리 제안(리치·간섭 ▸ 선택 영역 분리 제안)을 확인하고, 그래도 불가면 실기 확인 대상"
            : $"{general} — 리프트 변경만으로는 해결되지 않음: 정차점 최적화 → 영역 분리 제안 순으로 확인(보고서를 '정차 최적화 제안 포함'으로 출력하면 구체 정차를 싣는다)";
    }

    private void AppendIkRows(StringBuilder sb, InspectionAreaEntity a, TaskEval e, double lift)
    {
        var t = e.Task;
        var head = new[]
        {
            a.TankId, $"L{a.Level}", a.WallCode, a.Name, t.Seq.ToString(Ci), t.Name, t.TaskId.ToString(),
            N(e.Station.X, 3), N(e.Station.Y, 3), N(Deg(e.Station.Yaw), 1), N(lift, 0),
        };
        for (var i = 0; i < e.Points.Length; i++)
        {
            var r = e.Points[i];
            foreach (var s in r.Stages)
            {
                var judged = r.JudgedStages.Contains(s) ? "판정" : "참고";
                var pose = new[]
                {
                    PointNames[i], StageName(s.Stage), N(s.StandoffMm, 0), judged,
                    N(s.TcpTarget[0], 4), N(s.TcpTarget[1], 4), N(s.TcpTarget[2], 4),
                };
                if (s.Solutions.Count == 0)
                {
                    sb.Append(CsvLine(head.Concat(pose).Concat(new[] { "0", "" }).Concat(Enumerable.Repeat("", 13))
                        .Append("IK 해 없음"))).Append("\r\n");
                    continue;
                }
                for (var k = 0; k < s.Solutions.Count; k++)
                {
                    var x = s.Solutions[k];
                    var near = x.Parts.Count > 0 ? x.Parts.MinBy(p => p.ClearanceM) : null;
                    sb.Append(CsvLine(head.Concat(pose)
                        .Append((k + 1).ToString(Ci)).Append(k == s.ChosenIndex ? "Y" : "N")
                        .Concat(Enumerable.Range(0, 6).Select(n => x.JointsDeg.Length > n ? N(x.JointsDeg[n], 2) : ""))
                        .Append(x.PostureOk ? "Y" : "N").Append(N(x.MarginDeg, 1)).Append(x.Limiting)
                        .Append(near is null ? "" : N(near.ClearanceM * 1000, 1)).Append(near?.Name).Append(near?.NearestPlane)
                        .Append(""))).Append("\r\n");
                }
            }
        }
    }

    private static string VerdictText(ReachSeamSurvey s) => s.Verdict switch
    {
        ReachVerdict.Clear => "간섭 없음",
        ReachVerdict.Collision => "간섭",
        _ => "도달 불가",
    };

    private static string StageName(ReachStage s) => s switch
    {
        ReachStage.Approach => "② 접근",
        ReachStage.Align => "정렬 준비",
        _ => "초점 검사",
    };

    private static double Deg(double rad) => rad * 180 / Math.PI;

    private static string N(double v, int digits) =>
        !double.IsFinite(v) ? ""
        : Math.Round(v, digits) is var r && r == 0 ? "0"   // -0 방지
        : r.ToString(digits == 0 ? "0" : "0." + new string('#', digits), Ci);

    /// <summary>CSV 한 줄(RFC 4180) — 쉼표·따옴표·줄바꿈이 있으면 따옴표로 감싼다.</summary>
    public static string CsvLine(IEnumerable<string?> cells) => string.Join(",", cells.Select(c =>
        c is null ? "" : c.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c));
}

/// <summary>
/// 보고서 백데이터 — TASK 요약 표와 IK 해 목록 표. Header 는 CSV 한 줄(줄바꿈 없음), Rows 는 줄마다 CRLF 로 끝나는 CSV 본문이라
/// 여러 영역 응답의 Rows 를 그대로 이어 붙일 수 있다. Possible·Caution·Impossible = 검사 가능 / 가능(주의) / 불가 TASK 수.
/// </summary>
public sealed record ReachReport(string TaskHeader, string TaskRows, string IkHeader, string IkRows,
    int Areas, int Tasks, int Possible, int Caution, int Impossible);
