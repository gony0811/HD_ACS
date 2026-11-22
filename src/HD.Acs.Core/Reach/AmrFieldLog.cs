using System.Globalization;
using System.Text.RegularExpressions;

namespace HD.Acs.Core.Reach;

/// <summary>
/// 현장 검사 한 건의 결과 분류.
/// Ok = 성공 · Reach = 팔이 닿지 못함(시뮬레이터가 맞혀야 하는 실패) · Rule = AMR 이 규칙(최소 플랜지 뻗음)으로 출발 전에 거부(움직여 보지 않음) ·
/// Sensor = 레이저·카메라 정렬·측정 실패 · Operation = 비상정지·임무 실패 등 운영 원인 · Other = 그 밖 ·
/// NotRun = 같은 정차의 다른 용접선 때문에 실행하지 않음 · Unknown = 결과 줄이 로그에 없음.
/// </summary>
public enum FieldOutcome { Ok, Reach, Rule, Sensor, Operation, Other, NotRun, Unknown }

/// <summary>AMR 맵 좌표 자세 [m]·[rad].</summary>
public sealed record FieldPose(double X, double Y, double Theta);

/// <summary>
/// AMR 로그에서 뽑은 용접선 검사 한 건. Target = 지령 정차, Arrived = 노드 도달 직전 마지막 보고 자세(도착하지 않았으면 null).
/// TaskId 는 로그에 있을 때만(없으면 JobRef 로 찾는다).
/// </summary>
public sealed record FieldAttempt(string Time, string JobRef, Guid? TaskId, int? Attempt, string? WallCode, string? Recipe,
    double LiftMm, FieldPose? Target, FieldPose? Arrived, FieldOutcome Outcome, string? Step, string? Reason, string? OrderId);

public sealed record AmrFieldLogResult(IReadOnlyList<FieldAttempt> Attempts, IReadOnlyList<string> Warnings);

/// <summary>
/// HD_AMR 앱 로그(app-yyyy-MM-dd.log)에서 startWeldInspection 실적을 뽑는다 — 정차(지령·실제 도착 자세)·리프트·용접선·결과·실패 단계와 사유.
/// 시뮬레이터 판정과 현장 실적을 대조하는 자료(<c>ReachSimService.FieldCompareAsync</c>)이며, 로그 문구가 바뀌면 여기만 고친다.
/// 관련 없는 줄은 무시한다. 해석하지 못한 실패 줄은 Warnings 에 남긴다(조용히 버리지 않는다).
/// </summary>
public static class AmrFieldLog
{
    private const string Num = @"(-?\d+(?:\.\d+)?)";
    private static readonly Regex Head = new(@"^\[(\d\d:\d\d:\d\d)[.\d]*\] \[[^\]]*\] \[[^\]]*\] (.*)$", RegexOptions.Compiled);
    private static readonly Regex Order = new($@"^VDA5050 order 수리: (\S+?), node=\S+ → \({Num}, {Num}, θ={Num}\)", RegexOptions.Compiled);
    private static readonly Regex WaitStart = new($@"도착 대기 시작: 목표=\({Num}, {Num}, θ={Num}\)", RegexOptions.Compiled);
    private static readonly Regex Pose = new($@"pose=\({Num}, {Num}, θ={Num}\)", RegexOptions.Compiled);
    private static readonly Regex Bundle = new(@"^TASK 묶음 (?:Leader|Follower): (JOB-\S+) .*?wall=(\w+) lift=(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex Accept = new(
        @"^startWeldInspection 수리: jobRef=(JOB-[^,\s]+), seamType=\w+, wall=(\w+) → recipe=(\w+) \(.*?liftStroke=(\d+(?:\.\d+)?)mm(?:, taskId=([0-9a-fA-F-]{36}))?(?:, attempt=(\d+))?",
        RegexOptions.Compiled);
    private static readonly Regex Done = new(@"^VDA5050 액션 완료: startWeldInspection .*?jobRef=(JOB-\S+)", RegexOptions.Compiled);
    private static readonly Regex Fail = new(@"^VDA5050 액션 실패: startWeldInspection\s*(?:\([^)]*\))?\s*— (.*)$", RegexOptions.Compiled);
    private static readonly Regex FailStep = new(@"^(?:recipe=(\w+) )?step=(\w+) fail: (.*)$", RegexOptions.Compiled);
    private static readonly Regex JobIn = new(@"jobRef=(JOB-[^,\s)]+)", RegexOptions.Compiled);
    private static readonly Regex Precheck = new(@"^코봇 도달 사전 점검 실패: (.*)$", RegexOptions.Compiled);
    private static readonly Regex PrecheckJob = new(@"(JOB-\S+) (\S+): (\S+) \((.*?)\) — 차체", RegexOptions.Compiled);
    private static readonly Regex PrecheckLift = new(@"리프트 (\d+(?:\.\d+)?) mm", RegexOptions.Compiled);
    private static readonly Regex MissionFail = new(@"^VDA5050 임무 실패: (\S+) — (.*)$", RegexOptions.Compiled);

    private static readonly Regex ReachWords = new(@"도달 불가|진입 경로 없음|IK|rc=112|안전 범위|자세한계|특이점|관절|간섭|작업영역", RegexOptions.Compiled);
    private static readonly Regex SensorWords = new(@"레이저|측정|Peak|재정렬|WD |비전|카메라|depth", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OperationWords = new(@"비상정지|취소|cancel|저전력|통신 두절|장치", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] SensorSteps = { "flatSurfaceAlign", "cameraAlign", "laser", "peak", "bead" };

    /// <summary>실패 단계·사유 → 분류. 규칙 거부(뻗음 부족) → 운영 → 도달 → 센서 순으로 본다.</summary>
    public static FieldOutcome Classify(string? step, string? reason)
    {
        var r = reason ?? "";
        if (r.Contains("뻗음부족") || r.Contains("플랜지 뻗음")) return FieldOutcome.Rule;
        if (OperationWords.IsMatch(r)) return FieldOutcome.Operation;
        if (ReachWords.IsMatch(r)) return FieldOutcome.Reach;
        if (SensorWords.IsMatch(r) || (step is not null && SensorSteps.Any(s => step.StartsWith(s, StringComparison.OrdinalIgnoreCase))))
            return FieldOutcome.Sensor;
        return FieldOutcome.Other;
    }

    private sealed class Stop
    {
        public string? OrderId;
        public FieldPose? Target, Last, Arrived;
        public readonly List<(string Job, string Wall, double Lift)> Planned = new();
        public readonly HashSet<string> Recorded = new(StringComparer.Ordinal);
    }

    private sealed class Open
    {
        public required string Time, Job;
        public Guid? TaskId;
        public int? Attempt;
        public string? Wall, Recipe;
        public double Lift;
    }

    public static AmrFieldLogResult Parse(IEnumerable<string> lines, string? datePrefix = null)
    {
        var list = new List<FieldAttempt>();
        var warnings = new List<string>();
        var taskOf = new Dictionary<string, Guid>(StringComparer.Ordinal);   // jobRef → 마지막으로 본 taskId
        var stop = new Stop();
        Open? open = null;
        var prefix = string.IsNullOrWhiteSpace(datePrefix) ? "" : datePrefix!.Trim() + " ";

        void Close(string time, FieldOutcome outcome, string? step, string? reason)
        {
            if (open is null) return;
            list.Add(new FieldAttempt(prefix + open.Time, open.Job, open.TaskId, open.Attempt, open.Wall, open.Recipe, open.Lift,
                stop.Target, stop.Arrived ?? stop.Last, outcome, step, Trim(reason), stop.OrderId));
            stop.Recorded.Add(open.Job);
            open = null;
        }

        foreach (var raw in lines)
        {
            var h = Head.Match(raw);
            if (!h.Success) continue;
            var (time, msg) = (h.Groups[1].Value, h.Groups[2].Value);

            if (Order.Match(msg) is { Success: true } o)
            {
                Close(time, FieldOutcome.Unknown, null, "결과 줄 없음 — 다음 지령이 먼저 왔습니다");
                stop = new Stop { OrderId = o.Groups[1].Value, Target = PoseOf(o, 2) };
                continue;
            }
            if (WaitStart.Match(msg) is { Success: true } w)
            {
                stop.Target ??= PoseOf(w, 1);
                continue;
            }
            if (msg.StartsWith("VDA5050 노드 도달", StringComparison.Ordinal))
            {
                stop.Arrived = stop.Last;
                continue;
            }
            if (msg.StartsWith("VDA5050", StringComparison.Ordinal) && Pose.Match(msg) is { Success: true } p)
            {
                stop.Last = PoseOf(p, 1);
                continue;
            }
            if (Bundle.Match(msg) is { Success: true } b)
            {
                stop.Planned.Add((b.Groups[1].Value, b.Groups[2].Value, D(b.Groups[3].Value)));
                continue;
            }
            if (Accept.Match(msg) is { Success: true } a)
            {
                Close(time, FieldOutcome.Unknown, null, "결과 줄 없음 — 다음 검사가 먼저 시작됐습니다");
                open = new Open
                {
                    Time = time, Job = a.Groups[1].Value, Wall = a.Groups[2].Value, Recipe = a.Groups[3].Value, Lift = D(a.Groups[4].Value),
                    TaskId = Guid.TryParse(a.Groups[5].Value, out var id) ? id : null,
                    Attempt = int.TryParse(a.Groups[6].Value, out var n) ? n : null,
                };
                if (open.TaskId is { } tid) taskOf[open.Job] = tid;
                continue;
            }
            if (Done.Match(msg) is { Success: true } d)
            {
                if (open is not null && open.Job == d.Groups[1].Value) Close(time, FieldOutcome.Ok, null, null);
                else warnings.Add($"{time} 완료 줄의 검사를 찾지 못했습니다: {d.Groups[1].Value}");
                continue;
            }
            if (Fail.Match(msg) is { Success: true } f)
            {
                var body = f.Groups[1].Value;
                var s = FailStep.Match(body);
                var (step, reason) = s.Success ? (s.Groups[2].Value, s.Groups[3].Value) : ((string?)null, body);
                var job = JobIn.Match(body) is { Success: true } j ? j.Groups[1].Value : null;
                if (open is not null && (job is null || job == open.Job)) Close(time, Classify(step, reason), step, reason);
                else if (job is not null)
                {
                    // 시작(수리) 줄 없이 끝난 액션 — AMR 이 실행하지 않고 실패로 종결한 것
                    list.Add(new FieldAttempt(prefix + time, job, taskOf.TryGetValue(job, out var t) ? t : null, null,
                        stop.Planned.FirstOrDefault(x => x.Job == job).Wall, null, stop.Planned.FirstOrDefault(x => x.Job == job).Lift,
                        stop.Target, stop.Arrived, Classify(step, reason), step, Trim(reason), stop.OrderId));
                    stop.Recorded.Add(job);
                }
                else warnings.Add($"{time} 실패 줄의 용접선을 알 수 없습니다: {Trim(body, 120)}");
                continue;
            }
            if (Precheck.Match(msg) is { Success: true } pc)
            {
                var text = pc.Groups[1].Value;
                var jobs = PrecheckJob.Matches(text).GroupBy(m => m.Groups[1].Value)
                    .ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(m => $"{m.Groups[2].Value} {m.Groups[3].Value} ({m.Groups[4].Value})").Distinct()));
                var lift = PrecheckLift.Match(text) is { Success: true } pl ? D(pl.Groups[1].Value) : (double?)null;
                if (open is not null)
                {
                    // 도착한 뒤 그 용접선을 시작하면서 한 점검
                    var reason = jobs.TryGetValue(open.Job, out var mine) ? mine : Trim(text, 300);
                    Close(time, Classify("precheck", reason), "precheck", reason);
                    continue;
                }
                // 주행 전 점검 — 정차 전체를 실행하지 않는다. 사유가 적힌 용접선만 도달 실패, 나머지는 실행하지 않음.
                var planned = stop.Planned.Count > 0 ? stop.Planned.Select(x => (x.Job, (string?)x.Wall, x.Lift)).ToList()
                    : jobs.Keys.Select(k => (k, (string?)null, lift ?? 0)).ToList();
                foreach (var (job, wall, l) in planned)
                {
                    if (!stop.Recorded.Add(job)) continue;   // 같은 지령의 점검이 두 번 찍히는 경우
                    var named = jobs.TryGetValue(job, out var why);
                    list.Add(new FieldAttempt(prefix + time, job, taskOf.TryGetValue(job, out var t) ? t : null, null, wall, null, lift ?? l,
                        stop.Target, null, named ? Classify("precheck", why) : FieldOutcome.NotRun, named ? "precheck" : null,
                        named ? why : "같은 정차의 다른 용접선이 닿지 않아 실행하지 않음", stop.OrderId));
                }
                foreach (var job in jobs.Keys.Where(k => stop.Recorded.Add(k)))   // 묶음 목록에 없던 용접선
                    list.Add(new FieldAttempt(prefix + time, job, taskOf.TryGetValue(job, out var t) ? t : null, null, null, null, lift ?? 0,
                        stop.Target, null, Classify("precheck", jobs[job]), "precheck", jobs[job], stop.OrderId));
                continue;
            }
            if (MissionFail.Match(msg) is { Success: true } mf)
            {
                var reason = mf.Groups[2].Value;
                if (reason.Contains("코봇 도달")) continue;   // 사전 점검 줄이 이미 처리했다
                Close(time, FieldOutcome.Operation, null, "임무 실패: " + reason);
                foreach (var (job, wall, l) in stop.Planned.Where(x => stop.Recorded.Add(x.Job)))
                    list.Add(new FieldAttempt(prefix + time, job, taskOf.TryGetValue(job, out var t) ? t : null, null, wall, null, l,
                        stop.Target, stop.Arrived, FieldOutcome.Operation, null, Trim("임무 실패: " + reason), stop.OrderId));
                continue;
            }
            if (msg.StartsWith("비상정지 이행", StringComparison.Ordinal))
                Close(time, FieldOutcome.Operation, null, "비상정지");
        }
        Close("", FieldOutcome.Unknown, null, "결과 줄 없음 — 로그가 끝났습니다");
        return new AmrFieldLogResult(list, warnings);
    }

    private static FieldPose PoseOf(Match m, int first) =>
        new(D(m.Groups[first].Value), D(m.Groups[first + 1].Value), D(m.Groups[first + 2].Value));

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string? Trim(string? s, int max = 400) => s is null ? null : s.Length <= max ? s : s[..max] + "…";
}
