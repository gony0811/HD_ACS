using System.Diagnostics;
using HD.Acs.Core.Reach;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 면·층 정차 집합 덮개(set cover) — 영역 구분 없이 그 면·층의 용접선 전부를 놓고,
/// 정차 후보 격자(도면 x·y × 방향)마다 운영 판정(<see cref="ReachSimulator.Survey"/> — 세 자세·진입·도면 순회·간섭)으로
/// 간섭 없이 닿는 용접선 집합을 구한 뒤, 탐욕법으로 가장 적은 정차가 가장 많은 용접선을 덮도록 고른다(조회 전용, DB 무변경).
/// 리프트는 현재 설정(<c>Acs:Lift</c>)의 높이 하나로 고정한다(Off = 0, Fixed = 고정값, Auto = 0).
/// </summary>
public sealed partial class ReachSimService
{
    public async Task<CoverPlan?> CoverPlanAsync(string tankId, int level, string wallCode, double stepM, double reachM,
        int yawCount, CancellationToken ct, bool skipEntry = false, double minMarginDeg = 0, string? dumpPath = null)
    {
        var sw = Stopwatch.StartNew();
        _model = await BaseAsync(tankId, ct);
        // skipEntry: 운영 출발 자세(홈 위치_2 등)·진입 경로 판정을 빼고 "툴이 닿는가"만 본다(비교용)
        if (skipEntry)
            _model = _model with { StartJointsByWall = _model.StartJointsByWall
                .Where(kv => !kv.Key.Equals(wallCode, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value) };
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(wallCode, out var wall)) return null;
        var areas = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks)
            .Where(a => a.TankId == tankId && a.Level == level && a.WallCode == wallCode).ToListAsync(ct);

        var seams = new List<CoverSeamIn>();
        IReadOnlyList<ReachPlane>? planes = null;
        double[]? normal = null;
        double floorZ = 0;
        foreach (var a in areas)
        {
            var setup = Setup(ctx, a, wall);
            planes ??= setup.Planes;
            normal ??= setup.Normal;
            floorZ = setup.FloorZ;
            foreach (var t in a.Tasks.Where(t => t.Enabled).OrderBy(t => t.Seq))
            {
                var pts = SeamPoints(setup.Pose, t);
                seams.Add(new CoverSeamIn(t.TaskId, a.Name, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, pts,
                    (pts[0][0] + pts[^1][0]) / 2, (pts[0][1] + pts[^1][1]) / 2));
            }
        }
        if (seams.Count == 0 || planes is null || normal is null) return null;
        var lift = _model.LiftStrokeMm(_model.FixedStrokeMm ?? 0);
        var strokes = new[] { lift };

        // 후보 격자 — 용접선 중점 범위 ± 도달 반경, 차체가 면에서 5 cm 이상 떨어지는 자리만
        double x0 = seams.Min(s => s.Mx) - reachM, x1 = seams.Max(s => s.Mx) + reachM;
        double y0 = seams.Min(s => s.My) - reachM, y1 = seams.Max(s => s.My) + reachM;
        var cands = new List<ReachStation>();
        for (var x = x0; x <= x1 + 1e-9; x += stepM)
            for (var y = y0; y <= y1 + 1e-9; y += stepM)
                for (var k = 0; k < yawCount; k++)
                {
                    var st = new ReachStation(Math.Round(x, 3), Math.Round(y, 3), -Math.PI + 2 * Math.PI * k / yawCount, floorZ);
                    if (ReachSimulator.BodyAt(_model, planes, st).ClearanceM >= 0.05) cands.Add(st);
                }

        // 도달표 — 후보마다 반경 안 용접선만 판정
        var cover = new List<int>[cands.Count];
        var margin = new Dictionary<int, double?>[cands.Count];
        var blockers = new System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<string, int>>();
        var verdictCount = new int[3];
        long surveys = 0;
        var examples = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var nearExamples = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var model = _model;
        Parallel.For(0, cands.Count, new ParallelOptions { CancellationToken = ct }, ci =>
        {
            var st = cands[ci];
            var list = new List<int>();
            var mg = new Dictionary<int, double?>();
            var stations = new[] { st };
            for (var si = 0; si < seams.Count; si++)
            {
                var s = seams[si];
                if (Math.Sqrt((s.Mx - st.X) * (s.Mx - st.X) + (s.My - st.Y) * (s.My - st.Y)) > reachM) continue;
                Interlocked.Increment(ref surveys);
                var r = ReachSimulator.Survey(model, planes, stations, s.Points, normal, strokes, wallCode);
                Interlocked.Increment(ref verdictCount[(int)r.Verdict]);
                if (r.Verdict == ReachVerdict.Clear && (r.TraverseMarginDeg ?? 99) >= minMarginDeg) { list.Add(si); mg[si] = r.TraverseMarginDeg; }
                else if (r.Verdict == ReachVerdict.Clear) blockers.GetOrAdd(si, _ => new()).AddOrUpdate("순회 여유 부족", 1, (_, n) => n + 1);
                else
                {
                    var key = Category(r.Verdict == ReachVerdict.Collision ? "간섭" : r.Blocker);
                    examples.TryAdd(key, $"seam {s.Area}#{s.Seq} @({st.X:F2},{st.Y:F2},{st.Yaw * 180 / Math.PI:F0}°) d={Math.Sqrt((s.Mx - st.X) * (s.Mx - st.X) + (s.My - st.Y) * (s.My - st.Y)):F2}: {r.Blocker}");
                    if (Math.Sqrt((s.Mx - st.X) * (s.Mx - st.X) + (s.My - st.Y) * (s.My - st.Y)) < 0.8)
                        nearExamples.AddOrUpdate(key, 1, (_, n) => n + 1);
                    blockers.GetOrAdd(si, _ => new()).AddOrUpdate(key, 1, (_, n) => n + 1);
                }
            }
            cover[ci] = list;
            margin[ci] = mg;
        });

        // 도달표 파일(정수계획 풀이용) — 후보·용접선·순회 여유
        if (!string.IsNullOrWhiteSpace(dumpPath))
        {
            var sb = new System.Text.StringBuilder("#cand\tx\ty\tyawDeg\tseam\tmarginDeg\n");
            for (var ci = 0; ci < cands.Count; ci++)
                foreach (var si in cover[ci])
                    sb.Append(System.FormattableString.Invariant(
                        $"{ci}\t{cands[ci].X}\t{cands[ci].Y}\t{Math.Round(cands[ci].Yaw * 180 / Math.PI, 1)}\t{si}\t{margin[ci][si] ?? 99:F1}\n"));
            File.WriteAllText(dumpPath + ".tsv", sb.ToString());
            File.WriteAllText(dumpPath + ".seams.json", System.Text.Json.JsonSerializer.Serialize(seams.Select((s, i) =>
                new { i, taskId = s.TaskId, area = s.Area, seq = s.Seq, su = s.Su, sv = s.Sv, eu = s.Eu, ev = s.Ev })));
        }

        // 탐욕 set cover — 덮지 않은 용접선을 가장 많이 덮는 정차, 같으면 순회 최소 여유가 큰 쪽
        var uncovered = new HashSet<int>(Enumerable.Range(0, seams.Count).Where(i => cover.Any(c => c.Contains(i))));
        var coverable = uncovered.Count;
        var picks = new List<CoverStation>();
        var assigned = new int[seams.Count];
        Array.Fill(assigned, -1);
        while (uncovered.Count > 0)
        {
            int best = -1, bestGain = 0;
            double bestMg = double.NegativeInfinity;
            for (var ci = 0; ci < cands.Count; ci++)
            {
                var gain = 0;
                var mg = double.PositiveInfinity;
                foreach (var si in cover[ci])
                    if (uncovered.Contains(si)) { gain++; mg = Math.Min(mg, margin[ci][si] ?? 99); }
                if (gain > bestGain || (gain == bestGain && gain > 0 && mg > bestMg)) { best = ci; bestGain = gain; bestMg = mg; }
            }
            if (best < 0) break;
            var got = cover[best].Where(uncovered.Contains).ToList();
            foreach (var si in got) { uncovered.Remove(si); assigned[si] = picks.Count; }
            var st = cands[best];
            picks.Add(new CoverStation(picks.Count + 1, st.X, st.Y, Math.Round(st.Yaw * 180 / Math.PI, 1), lift,
                got.Count, cover[best].Count,
                got.Select(si => margin[best][si]).Where(m => m is not null).Select(m => m!.Value).DefaultIfEmpty(double.NaN).Min() is var m0 && double.IsNaN(m0) ? null : Math.Round(m0, 1),
                got.Select(si => seams[si].TaskId).ToList()));
        }

        var outSeams = seams.Select((s, i) => new CoverSeam(s.TaskId, s.Area, s.Seq, s.Su, s.Sv, s.Eu, s.Ev,
            assigned[i] >= 0 ? assigned[i] + 1 : null,
            assigned[i] >= 0 ? null
                : blockers.TryGetValue(i, out var b) ? b.OrderByDescending(kv => kv.Value).First().Key : "반경 안 후보 없음")).ToList();
        var reasonSummary = outSeams.Where(s => s.Station is null).GroupBy(s => s.Reason!)
            .ToDictionary(g => g.Key, g => g.Count());
        return new CoverPlan(tankId, level, wallCode, model.MountZMm, lift, stepM, reachM, yawCount, cands.Count, surveys,
            seams.Count, coverable, picks.Count, areas.Count, sw.ElapsedMilliseconds, reasonSummary, picks, outSeams,
            new { clear = verdictCount[(int)ReachVerdict.Clear], collision = verdictCount[(int)ReachVerdict.Collision],
                  unreachable = verdictCount[(int)ReachVerdict.Unreachable], examples, nearCounts = nearExamples });
    }

    /// <summary>막힘 사유 문장을 분류 이름으로.</summary>
    private static string Category(string? blocker)
    {
        if (blocker is null) return "기타";
        if (blocker == "간섭") return "간섭";
        string[] keys = { "진입", "도면 순회", "가지 전환", "팔 뻗음", "플랜지", "IK", "관절 한계", "특이점", "J6", "손목", "차체" };
        foreach (var k in keys) if (blocker.Contains(k)) return k;
        return blocker.Length > 30 ? blocker[..30] : blocker;
    }

    private sealed record CoverSeamIn(Guid TaskId, string Area, int Seq, double Su, double Sv, double Eu, double Ev,
        double[][] Points, double Mx, double My);
}

public sealed record CoverPlan(string TankId, int Level, string WallCode, double MountZMm, double LiftMm, double StepM, double ReachM,
    int YawCount, int Candidates, long Surveys, int Seams, int Coverable, int StationCount, int CurrentAreaCount, long ElapsedMs,
    Dictionary<string, int> UncoveredReasons, List<CoverStation> Stations, List<CoverSeam> SeamList, object VerdictTotals);

public sealed record CoverStation(int No, double X, double Y, double YawDeg, double LiftMm, int Covers, int CanReach,
    double? MinTraverseMarginDeg, List<Guid> TaskIds);

public sealed record CoverSeam(Guid TaskId, string Area, int Seq, double StartU, double StartV, double EndU, double EndV,
    int? Station, string? Reason);
