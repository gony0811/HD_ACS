using System.Globalization;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 현장 실적 대조 — AMR 로그에서 뽑은 검사 실적(<see cref="AmrFieldLog"/>)을 그때의 실제 정차 자세·리프트로 시뮬레이터에 다시 넣어
/// 판정이 현장 결과와 같은지 본다(조회 전용). 정차는 로그의 도착 자세(없으면 지령 자세)를 그 층의 유효 T_W_D 로 도면 좌표로 바꿔 쓴다 —
/// 저장된 정차가 아니라 로봇이 실제로 섰던 자리다. 용접선은 taskId(없으면 jobRef — 배차 기록 우선)로 지금 DB 의 좌표를 쓴다.
/// 일치 판정은 성공(Ok)과 도달 실패(Reach)만 한다: 성공인데 시뮬레이터가 막으면 "과잉", 도달 실패인데 가능으로 보면 "놓침".
/// 센서·운영 실패와 규칙 거부는 시뮬레이터가 맞힐 대상이 아니라 판정만 싣는다.
/// </summary>
public sealed partial class ReachSimService
{
    public async Task<FieldCompareResult?> FieldCompareAsync(string tankId, IReadOnlyList<FieldAttempt> attempts, string? wallCode,
        CancellationToken ct, IReadOnlyList<string>? parseWarnings = null)
    {
        _model = _baseModel;   // 운영 가지 규칙
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var areas = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId).ToListAsync(ct);
        var byTask = areas.SelectMany(a => a.Tasks.Select(t => (a, t))).ToDictionary(x => x.t.TaskId);
        var byJob = new Dictionary<string, (InspectionAreaEntity a, AreaTaskEntity t)>(StringComparer.Ordinal);
        foreach (var (a, t) in byTask.Values) byJob[$"JOB-{tankId}-L{a.Level}-{a.WallCode}-{a.Name}-{t.Seq}"] = (a, t);
        // 로그에 taskId 가 없는 줄(주행 전 거부 등)은 jobRef 로 찾는다. jobRef 는 영역 이름+순번이라 영역을 다시 나누면 다른 용접선을 가리킨다 —
        // 그때 실제로 보낸 배차 기록(work_item 액션의 jobRef → taskId)을 먼저 보고, 한 jobRef 가 여러 용접선으로 나갔으면 추측하지 않는다.
        var dispatched = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var json in await _db.WorkItems.AsNoTracking().Where(w => w.MapId.StartsWith(tankId + "-L") && w.Actions != null)
                     .Select(w => w.Actions!).ToListAsync(ct))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("jobRef", out var jr) && jr.GetString() is { } job
                        && el.TryGetProperty("taskId", out var ti) && Guid.TryParse(ti.GetString(), out var tid))
                        (dispatched.TryGetValue(job, out var set) ? set : dispatched[job] = new HashSet<Guid>()).Add(tid);
            }
            catch (System.Text.Json.JsonException) { }
            catch (InvalidOperationException) { }
        }

        // 층별 유효 T_W_D — 없거나 맵버전이 다르면 그 층 실적은 자세를 도면으로 바꿀 수 없다
        var transforms = new Dictionary<int, DrawingTransform?>();
        foreach (var map in await _db.Maps.AsNoTracking().Where(m => m.TankId == tankId).ToListAsync(ct))
        {
            var cal = await _db.MapCalibrations.AsNoTracking().Where(c => c.MapId == map.MapId)
                .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync(ct);
            try
            {
                transforms[map.Level] = WeldInspectionPayload.ResolveTransform(map.Version, cal?.MapVersion,
                    cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0);
            }
            catch (Exception) { transforms[map.Level] = null; }
        }

        var rows = new FieldCompareRow[attempts.Count];
        var model = _model;
        Parallel.For(0, attempts.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var f = attempts[i];
            (InspectionAreaEntity a, AreaTaskEntity t)? found = null;
            string? resolvedBy = null;
            if (f.TaskId is { } id && byTask.TryGetValue(id, out var hit)) (found, resolvedBy) = (hit, "taskId");
            else if (f.TaskId is null && dispatched.TryGetValue(f.JobRef, out var sent))
            {
                if (sent.Count > 1)
                {
                    rows[i] = FieldCompareRow.Unresolved(f, f.WallCode,
                        $"jobRef 가 배차 기록에서 용접선 {sent.Count}개를 가리킵니다(영역을 다시 나눔) — 로그에 taskId 가 없어 정할 수 없습니다");
                    return;
                }
                if (byTask.TryGetValue(sent.First(), out var viaSent)) (found, resolvedBy) = (viaSent, "jobRef(배차 기록)");
            }
            else if (f.TaskId is null && byJob.TryGetValue(f.JobRef, out var j)) (found, resolvedBy) = (j, "jobRef(현재 계획)");
            if (found is not { } x)
            {
                rows[i] = FieldCompareRow.Unresolved(f, f.WallCode, "용접선을 찾지 못했습니다(taskId·jobRef 가 지금 계획에 없음)");
                return;
            }
            var (area, task) = x;
            if (!ctx.Walls.TryGetValue(area.WallCode, out var wall))
            {
                rows[i] = FieldCompareRow.Unresolved(f, area.WallCode, "면이 없습니다");
                return;
            }
            var pose = f.Arrived ?? f.Target;
            if (pose is null)
            {
                rows[i] = FieldCompareRow.Unresolved(f, area.WallCode, "로그에 정차 자세가 없습니다", area, task, resolvedBy);
                return;
            }
            if (!transforms.TryGetValue(area.Level, out var tw) || tw is null)
            {
                rows[i] = FieldCompareRow.Unresolved(f, area.WallCode, $"L{area.Level} 유효 캘리브레이션(T_W_D)이 없습니다", area, task, resolvedBy);
                return;
            }
            var floorZ = area.Level >= 1 && area.Level <= ctx.LevelZ.Length ? ctx.LevelZ[area.Level - 1] : 0;
            var (sx, sy) = tw.MapToDrawing(pose.X, pose.Y);
            var station = new ReachStation(sx, sy, tw.MapYawToDrawing(pose.Theta), floorZ);
            var wp = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
            var pts = SeamPoints(wp, task);
            var lift = Math.Clamp(f.LiftMm, 0, model.StrokeMaxMm);
            var s = ReachSimulator.Survey(model, ReachSimulator.PlanesForLevel(ctx.WallPlanes, floorZ, area.Level),
                new[] { station }, pts, Json<double[]>(wall.Normal), new[] { lift }, area.WallCode);

            double? devXy = null, devDeg = null;
            string? atTarget = null;
            if (f.Arrived is { } arr && f.Target is { } tgt)
            {
                // 지령 자세였다면 판정이 어땠을지 — 도착 오차가 판정을 바꾸는지 본다
                var (tx, ty) = tw.MapToDrawing(tgt.X, tgt.Y);
                atTarget = ReachSimulator.Survey(model, ReachSimulator.PlanesForLevel(ctx.WallPlanes, floorZ, area.Level),
                    new[] { new ReachStation(tx, ty, tw.MapYawToDrawing(tgt.Theta), floorZ) }, pts, Json<double[]>(wall.Normal),
                    new[] { lift }, area.WallCode).Verdict.ToString();
                devXy = Math.Round(Math.Sqrt(Math.Pow(arr.X - tgt.X, 2) + Math.Pow(arr.Y - tgt.Y, 2)) * 1000);
                devDeg = Math.Round(Math.Abs(Math.Atan2(Math.Sin(arr.Theta - tgt.Theta), Math.Cos(arr.Theta - tgt.Theta))) * 180 / Math.PI, 1);
            }
            var reach = pts.Max(p => Math.Sqrt(Math.Pow(p[0] - sx, 2) + Math.Pow(p[1] - sy, 2)));
            var clear = s.Verdict == ReachVerdict.Clear;
            var match = f.Outcome switch
            {
                FieldOutcome.Ok => clear ? FieldMatch.Agree : FieldMatch.SimTooStrict,
                FieldOutcome.Reach => clear ? FieldMatch.SimMissed : FieldMatch.Agree,
                _ => FieldMatch.NotJudged,
            };
            rows[i] = new FieldCompareRow(f.Time, f.JobRef, task.TaskId, area.AreaId, area.Name, area.WallCode, area.Level, task.Seq,
                task.SeamType, f.Attempt, lift, f.Outcome.ToString(), f.Step, f.Reason, f.Arrived is not null ? "도착" : "지령",
                Math.Round(sx, 3), Math.Round(sy, 3), Math.Round(station.Yaw * 180 / Math.PI, 1), devXy, devDeg, Math.Round(reach, 3),
                s.Verdict.ToString(), s.Blocker, Round1(s.TraverseMarginDeg), s.MinClearanceM is { } c ? Math.Round(c, 4) : null,
                match.ToString(), resolvedBy, null, atTarget);
        });

        var list = (wallCode is { Length: > 0 }
            ? rows.Where(r => string.Equals(r.WallCode, wallCode, StringComparison.OrdinalIgnoreCase)) : rows).ToList();
        return new FieldCompareResult(tankId, wallCode, Summarize(list), list, parseWarnings ?? Array.Empty<string>());
    }

    private static FieldCompareSummary Summarize(IReadOnlyList<FieldCompareRow> rows)
    {
        var judged = rows.Where(r => r.SimVerdict is not null).ToList();
        int Count(string outcome, Func<FieldCompareRow, bool>? pred = null) =>
            judged.Count(r => r.Outcome == outcome && (pred is null || pred(r)));
        static bool Clear(FieldCompareRow r) => r.SimVerdict == nameof(ReachVerdict.Clear);

        var cells = judged.GroupBy(r => (r.Outcome, r.SimVerdict!))
            .Select(g => new FieldCompareCell(g.Key.Outcome, g.Key.Item2, g.Count()))
            .OrderBy(c => c.Outcome).ThenBy(c => c.SimVerdict).ToList();
        // 도달 실패를 단계별로 — 시뮬레이터가 어느 단계의 실패를 놓치는지 본다
        var steps = judged.Where(r => r.Outcome == nameof(FieldOutcome.Reach)).GroupBy(r => r.Step ?? "(단계 없음)")
            .Select(g => new FieldCompareStep(g.Key, g.Count(), g.Count(r => !Clear(r)))).OrderByDescending(x => x.Count).ToList();
        // 성공인데 시뮬레이터가 막은 사유(앞 20자 기준 묶음) — 어느 규칙이 과한지 본다
        var strict = judged.Where(r => r.Match == nameof(FieldMatch.SimTooStrict))
            .GroupBy(r => BlockerKey(r.SimBlocker ?? r.SimVerdict!))
            .Select(g => new FieldCompareStep(g.Key, g.Count(), 0)).OrderByDescending(x => x.Count).ToList();
        var devs = rows.Where(r => r.ArrivalDevMm is not null).Select(r => r.ArrivalDevMm!.Value).OrderBy(v => v).ToList();
        return new FieldCompareSummary(rows.Count, rows.Count - judged.Count,
            Count(nameof(FieldOutcome.Ok)), Count(nameof(FieldOutcome.Ok), Clear),
            Count(nameof(FieldOutcome.Reach)), Count(nameof(FieldOutcome.Reach), r => !Clear(r)),
            cells, steps, strict,
            devs.Count == 0 ? null : devs[devs.Count / 2], devs.Count == 0 ? null : devs[^1],
            judged.Count(r => r.SimVerdictAtTarget is not null && r.SimVerdictAtTarget != r.SimVerdict));
    }

    /// <summary>불가 사유를 묶는 열쇠 — 숫자를 지워 같은 종류끼리 모은다.</summary>
    private static string BlockerKey(string blocker)
    {
        var head = blocker.Split('—', '(', ':')[0].Trim();
        var s = System.Text.RegularExpressions.Regex.Replace(head, @"[-+]?\d+(\.\d+)?", "n");
        return s.Length <= 40 ? s : s[..40];
    }

    public static string FieldCompareCsvHeader => CsvLine(new[]
    {
        "시각", "jobRef", "taskId", "영역", "면", "층", "순번", "유형", "attempt", "리프트mm", "현장결과", "현장단계", "현장사유",
        "정차자세출처", "정차X", "정차Y", "정차방향deg", "도착오차mm", "도착오차deg", "정차–용접선m",
        "시뮬판정", "시뮬사유", "순회여유deg", "간섭여유m", "대조", "용접선찾은키", "비고", "지령자세판정",
    });

    public static string FieldCompareCsvRow(FieldCompareRow r)
    {
        static string? N(double? v) => v?.ToString(CultureInfo.InvariantCulture);
        return CsvLine(new[]
        {
            r.Time, r.JobRef, r.TaskId?.ToString(), r.AreaName, r.WallCode, r.Level?.ToString(), r.Seq?.ToString(), r.SeamType,
            r.Attempt?.ToString(), N(r.LiftMm), r.Outcome, r.Step, r.Reason, r.PoseSource, N(r.StationX), N(r.StationY), N(r.StationYawDeg),
            N(r.ArrivalDevMm), N(r.ArrivalDevDeg), N(r.ReachM), r.SimVerdict, r.SimBlocker, N(r.TraverseMarginDeg), N(r.MinClearanceM),
            r.Match, r.ResolvedBy, r.Note, r.SimVerdictAtTarget,
        });
    }
}

/// <summary>대조 결과 — Agree 일치 · SimTooStrict 현장은 성공인데 시뮬레이터가 막음 · SimMissed 현장은 도달 실패인데 시뮬레이터는 가능 · NotJudged 대조 대상 아님.</summary>
public enum FieldMatch { Agree, SimTooStrict, SimMissed, NotJudged }

/// <summary>
/// 실적 한 건의 대조. 정차 X·Y·방향 = 도면 좌표(로그 자세에 T_W_D 역변환), PoseSource = "도착"(실제 도착 자세) / "지령"(도착 전 거부 등).
/// ArrivalDev = 도착 자세와 지령 자세의 차. SimVerdictAtTarget = 지령 자세였다면의 판정(도착 자세가 있을 때만). ReachM = 정차에서 용접선 가장 먼 점까지 수평 거리. SimVerdict 가 null 이면 시뮬레이션하지 못한 것(Note 에 사유).
/// </summary>
public sealed record FieldCompareRow(string Time, string JobRef, Guid? TaskId, Guid? AreaId, string? AreaName, string? WallCode, int? Level,
    int? Seq, string? SeamType, int? Attempt, double LiftMm, string Outcome, string? Step, string? Reason, string? PoseSource,
    double? StationX, double? StationY, double? StationYawDeg, double? ArrivalDevMm, double? ArrivalDevDeg, double? ReachM,
    string? SimVerdict, string? SimBlocker, double? TraverseMarginDeg, double? MinClearanceM, string Match, string? ResolvedBy, string? Note,
    string? SimVerdictAtTarget = null)
{
    public static FieldCompareRow Unresolved(FieldAttempt f, string? wallCode, string note, InspectionAreaEntity? area = null,
        AreaTaskEntity? task = null, string? resolvedBy = null) =>
        new(f.Time, f.JobRef, task?.TaskId ?? f.TaskId, area?.AreaId, area?.Name, wallCode, area?.Level, task?.Seq, task?.SeamType, f.Attempt,
            f.LiftMm, f.Outcome.ToString(), f.Step, f.Reason, null, null, null, null, null, null, null, null, null, null, null,
            nameof(FieldMatch.NotJudged), resolvedBy, note);
}

public sealed record FieldCompareCell(string Outcome, string SimVerdict, int Count);

/// <summary>단계(또는 사유)별 건수 — Caught = 그중 시뮬레이터도 불가로 본 수.</summary>
public sealed record FieldCompareStep(string Step, int Count, int Caught);

/// <summary>
/// 대조 요약. Ok/OkClear = 현장 성공 건수 / 그중 시뮬레이터도 가능. Reach/ReachCaught = 현장 도달 실패 건수 / 그중 시뮬레이터도 불가.
/// ArrivalFlips = 도착 자세와 지령 자세에서 판정이 달라지는 건수. Cells = 현장 결과 × 시뮬레이터 판정 건수, ReachSteps = 도달 실패 단계별, TooStrict = 성공을 막은 시뮬레이터 사유별.
/// </summary>
public sealed record FieldCompareSummary(int Rows, int NotSimulated, int Ok, int OkClear, int Reach, int ReachCaught,
    IReadOnlyList<FieldCompareCell> Cells, IReadOnlyList<FieldCompareStep> ReachSteps, IReadOnlyList<FieldCompareStep> TooStrict,
    double? ArrivalDevMedianMm, double? ArrivalDevMaxMm, int ArrivalFlips);

public sealed record FieldCompareResult(string TankId, string? WallCode, FieldCompareSummary Summary,
    IReadOnlyList<FieldCompareRow> Rows, IReadOnlyList<string> ParseWarnings);
