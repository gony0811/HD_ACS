using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Hubs;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// AMR 이 factsheet(§7.3) 확장 <c>hdAmr</c> 로 보고한 값 — 해석 결과.
/// StartJointsByWall = 면 코드 → 운영 ② 출발 관절각, MountPose = T_A_B [x,y,z,rx,ry,rz](미보정이면 null),
/// SafeJ6 = 티칭과 함께 온 J6 안전 범위·대기(홈) 각도(구버전 AMR 이면 null).
/// JointMinDeg·JointMaxDeg = AMR 도달 사전 점검이 쓰는 관절 사용 범위 J1~J6(구버전 AMR 이면 null).
/// </summary>
public sealed record AmrFactsheet(
    int ConfigVersion, string? ChangedAt, IReadOnlyList<string> Changed, IReadOnlyDictionary<string, string> Fingerprints,
    string? TeachingLiftMode, IReadOnlyDictionary<string, double[]> StartJointsByWall,
    string? LiftMode, double? FixedStrokeMm, double[]? MountPose, double[]? LinksMm, double? MaxEntryJointTravelDeg,
    AmrSafeJ6? SafeJ6 = null, double[]? JointMinDeg = null, double[]? JointMaxDeg = null)
{
    /// <summary>factsheet 원문에서 hdAmr 확장을 읽는다. 확장이 없거나 형식이 틀리면 null(무시).</summary>
    public static AmrFactsheet? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root?["hdAmr"] is not JsonObject hd) return null;
        static double[]? Arr(JsonNode? n, int len) =>
            n is JsonArray a && a.Count == len && a.All(v => v is JsonValue jv && jv.TryGetValue<double>(out var d) && double.IsFinite(d))
                ? a.Select(v => v!.GetValue<double>()).ToArray() : null;
        static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        var starts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (hd["teaching"]?["startJointsByWall"] is JsonObject sj)
            foreach (var (code, v) in sj)
                if (Arr(v, 6) is { } j) starts[code] = j;
        var fps = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hd["fingerprints"] is JsonObject f)
            foreach (var (k, v) in f)
                if (Str(v) is { } s) fps[k] = s;
        return new AmrFactsheet(
            (int)(Num(hd["configVersion"]) ?? 0), Str(hd["changedAt"]),
            hd["changed"] is JsonArray ch ? ch.Select(Str).OfType<string>().ToList() : new List<string>(),
            fps, Str(hd["teaching"]?["liftMode"]), starts,
            Str(hd["lift"]?["mode"]), Num(hd["lift"]?["fixedStrokeMm"]),
            Arr(hd["mount"]?["tAB"], 6), Arr(hd["cobot"]?["linksMm"], 6), Num(hd["cobot"]?["maxEntryJointTravelDeg"]),
            hd["teaching"]?["safeJ6"] is JsonObject j6
            && Num(j6["minDeg"]) is { } lo && Num(j6["maxDeg"]) is { } hi && Num(j6["standbyDeg"]) is { } sb
                ? new AmrSafeJ6(lo, hi, sb) : null,
            Arr(hd["cobot"]?["jointMinDeg"], 6), Arr(hd["cobot"]?["jointMaxDeg"], 6));
    }
}

/// <summary>AMR 의 J6 안전 범위 [도]·대기(홈) 자세 J6 [도] — HD_AMR 파라미터 <c>inspection.safeJ6MinDeg/MaxDeg/StandbyDeg</c> 적용값.</summary>
public sealed record AmrSafeJ6(double MinDeg, double MaxDeg, double StandbyDeg)
{
    /// <summary>
    /// 툴 장착 보정 [도, (−180, 180]] — 대기 자세에서 센서 헤드가 용접검사 기준(J6 −180°, 장착 0°)과 같은 쪽을 향한다고 보고
    /// J6 + 장착 보정 ≡ −180° 가 되게 잡는다(대기 0° → 180°, 대기 −180° → 0° — 가스검사 설정 <c>Kinds:GAS</c> 와 같은 규칙).
    /// </summary>
    public double ToolMountRollDeg
    {
        get
        {
            var r = (-180 - StandbyDeg) % 360;
            if (r <= -180) r += 360;
            if (r > 180) r -= 360;
            return Math.Abs(r) < 1e-9 ? 0 : r;
        }
    }
}

/// <summary>
/// AMR factsheet(§7.3) 수신·반영 — AMR 이 보고한 티칭 출발 자세·리프트 운영·장착 보정·코봇 값을 설정 층
/// <see cref="FileName"/>(appsettings.json 다음, 화면 설정 <see cref="ReachRuntimeSettings.FileName"/> 앞)에 써서 바로 다시 읽는다.
/// 우선순위: appsettings.json &lt; AMR 보고 &lt; 리치·간섭 화면 설정. 진행 중 run 이 있으면 끝날 때까지 미뤄 둔다.
/// 설정 버전(configVersion)이 직전에 반영한 값보다 올라가면 <c>AMR_CONFIG_CHANGED</c> 알람.
/// 설정 <c>Acs:AmrFactsheet:Enabled</c>(기본 true) · <c>Sections</c>(반영할 묶음, 기본 teaching·lift·mount·cobot).
/// </summary>
public sealed class AmrFactsheetService : BackgroundService
{
    public const string FileName = "appsettings.amr-reported.json";
    public static readonly string[] AllSections = { "teaching", "lift", "mount", "cobot" };

    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AmrFactsheetService> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _latest = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }

    /// <summary>로봇별 최신 보고 — Applied = 설정 층에 반영했는지(false = 진행 중 run 때문에 대기).</summary>
    public sealed record Entry(string RobotId, string SerialNumber, AmrFactsheet Factsheet, DateTimeOffset ReceivedAt,
        bool Applied, IReadOnlyDictionary<string, string> Keys, string? Note);

    public AmrFactsheetService(IConfiguration config, IServiceScopeFactory scopes, ILogger<AmrFactsheetService> log, string path)
    {
        _config = config;
        _scopes = scopes;
        _log = log;
        Path = path;
    }

    public IReadOnlyList<Entry> All => _latest.Values.OrderBy(e => e.RobotId).ToList();
    public Entry? Get(string robotId) => _latest.TryGetValue(robotId, out var e) ? e : null;

    private bool Enabled => _config.GetValue("Acs:AmrFactsheet:Enabled", true);

    private HashSet<string> Sections()
    {
        var list = _config.GetSection("Acs:AmrFactsheet:Sections").Get<string[]>();
        return new HashSet<string>(list is { Length: > 0 } ? list : AllSections, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 보고 → 설정 키(콜론 경로) → 값. 리프트는 <c>Acs:Lift</c>, 나머지는 <paramref name="reachPrefix"/>(기본 <c>Acs:Reach:</c>,
    /// 가스검사 AMR 이면 <c>Acs:Reach:Kinds:GAS:</c>). 범위 밖 값은 싣지 않는다(설정 읽기와 같은 범위).
    /// </summary>
    public static Dictionary<string, string> ConfigKeys(AmrFactsheet f, ISet<string> sections, string reachPrefix = "Acs:Reach:")
    {
        var ci = CultureInfo.InvariantCulture;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string N(double v) => v.ToString("R", ci);
        if (sections.Contains("teaching"))
        {
            foreach (var (code, j) in f.StartJointsByWall)
                for (var i = 0; i < 6; i++) d[$"{reachPrefix}StartJoints:{code}:{i}"] = N(j[i]);
            // J6 안전 범위·대기 자세 — 출발 자세 J6 와 짝이므로 티칭과 함께 반영한다. 판정 자세 J6 는 대기 자세(HD_AMR 수직 검사 J6 = 대기),
            // 툴 장착 보정은 대기 자세 기준으로 맞춘다(appsettings 의 OperationJ6Deg·ToolMountRollDeg 가 남아 가리지 않게 명시).
            // 범위가 대기 자세를 포함하지 않는 등 잘못된 값이면 싣지 않는다(설정 읽기도 기본값으로 되돌리므로).
            if (f.SafeJ6 is { } j6 && ReachRobotModel.ResolveSafeJ6Range(j6.MinDeg, j6.MaxDeg, j6.StandbyDeg).Ok)
            {
                d[$"{reachPrefix}SafeJ6MinDeg"] = N(j6.MinDeg);
                d[$"{reachPrefix}SafeJ6MaxDeg"] = N(j6.MaxDeg);
                d[$"{reachPrefix}SafeJ6StandbyDeg"] = N(j6.StandbyDeg);
                d[$"{reachPrefix}OperationJ6Deg"] = N(j6.StandbyDeg);
                d[$"{reachPrefix}ToolMountRollDeg"] = N(j6.ToolMountRollDeg);
            }
        }
        if (sections.Contains("lift") && f.LiftMode is { } m && Enum.TryParse<LiftMode>(m, true, out var mode) && Enum.IsDefined(mode))
        {
            if (mode == Services.LiftMode.Fixed && !(f.FixedStrokeMm is { } fx && fx >= 0 && fx <= LiftSettings.MaxStrokeMm))
            { /* 높이 없는 고정 — 싣지 않는다 */ }
            else
            {
                d["Acs:Lift:Mode"] = mode.ToString();
                if (mode == Services.LiftMode.Fixed) d["Acs:Lift:FixedStrokeMm"] = N(f.FixedStrokeMm!.Value);
            }
        }
        if (sections.Contains("mount") && f.MountPose is { } t)
        {
            if (t[2] >= 300 && t[2] <= 3000) d[$"{reachPrefix}MountZMm"] = N(t[2]);
            d[$"{reachPrefix}MountYawDeg"] = N(t[5]);
        }
        if (sections.Contains("cobot"))
        {
            if (f.LinksMm is { } l)
                for (var i = 0; i < 6; i++) d[$"{reachPrefix}LinksMm:{i}"] = N(l[i]);
            if (f.MaxEntryJointTravelDeg is { } e && e >= 30 && e <= 360) d[$"{reachPrefix}MaxEntryJointTravelDeg"] = N(e);
            // 관절 사용 범위(J1~J5) — AMR 사전 점검과 같은 범위로 정차를 골라야 AMR 이 거부하지 않는다(J6 은 따로 판정).
            if (ReachSimService.JointLimitsFrom(f.JointMinDeg, f.JointMaxDeg) is { } lim)
                for (var i = 0; i < lim.Min.Length; i++)
                {
                    d[$"{reachPrefix}JointMinDeg:{i}"] = N(lim.Min[i]);
                    d[$"{reachPrefix}JointMaxDeg:{i}"] = N(lim.Max[i]);
                }
        }
        return d;
    }

    /// <summary>
    /// 여러 AMR 의 키를 합친다 — 같은 키에 값이 다르면(예: 두 AMR 의 리프트 방식이 다름) 그 키는 싣지 않고 충돌 목록에 넣는다.
    /// </summary>
    public static (Dictionary<string, string> Merged, List<string> Conflicts) Merge(IEnumerable<IReadOnlyDictionary<string, string>> robots)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in robots)
            foreach (var (k, v) in r)
            {
                if (conflicts.Contains(k)) continue;
                if (merged.TryGetValue(k, out var old) && old != v) { merged.Remove(k); conflicts.Add(k); }
                else merged[k] = v;
            }
        return (merged, conflicts.OrderBy(k => k).ToList());
    }

    /// <summary>콜론 경로 키 → 중첩 JSON(배열 인덱스는 숫자 키 객체로 — 설정 원본은 같은 키로 읽는다).</summary>
    public static JsonObject ToJson(IReadOnlyDictionary<string, string> keys)
    {
        var root = new JsonObject();
        foreach (var (key, value) in keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var parts = key.Split(':');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is not JsonObject next) { next = new JsonObject(); node[parts[i]] = next; }
                node = next;
            }
            node[parts[^1]] = value;
        }
        return root;
    }

    /// <summary>factsheet 수신(MQTT) — 해석·보관, 진행 중 run 이 없으면 바로 반영.</summary>
    public async Task HandleAsync(RobotRef robot, string json, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var f = AmrFactsheet.Parse(json);
        if (f is null)
        {
            _log.LogWarning("AMR factsheet 에 hdAmr 확장이 없거나 형식 오류 — 무시 (robot={Robot})", robot.RobotId);
            return;
        }
        var kind = ReachSimService.KindForSource(_config, robot.SerialNumber) ?? ReachSimService.KindForSource(_config, robot.RobotId);
        var prefix = kind is null || kind == InspectionKinds.Weld ? "Acs:Reach:" : $"{ReachSimService.KindsSection}:{kind}:";
        var keys = ConfigKeys(f, Sections(), prefix);
        _latest[robot.RobotId] = new Entry(robot.RobotId, robot.SerialNumber, f, DateTimeOffset.UtcNow, false, keys, null);
        await TryApplyAsync(ct);
    }

    /// <summary>대기 중인 보고를 반영한다(진행 중 run 이 있으면 그대로 둔다). 반영했으면 true.</summary>
    public async Task<bool> TryApplyAsync(CancellationToken ct = default)
    {
        var pending = _latest.Values.Where(e => !e.Applied).ToList();
        if (pending.Count == 0) return false;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        var active = await db.ScenarioRuns.AsNoTracking()
            .Where(r => r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER").Select(r => r.RunId).FirstOrDefaultAsync(ct);
        if (active != Guid.Empty)
        {
            foreach (var e in pending)
                _latest[e.RobotId] = e with { Note = $"진행 중 run({active}) — 끝나면 반영" };
            return false;
        }

        var prevMeta = ReadMeta();
        var (merged, conflicts) = Merge(_latest.Values.Select(e => e.Keys));
        var overridden = new ReachRuntimeSettings(_config, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ReachRuntimeSettings.FileName))
            .Overrides().Keys.Where(k => merged.ContainsKey(k)).ToList();
        var json = ToJson(merged);
        var meta = new JsonObject();
        foreach (var e in _latest.Values)
            meta[e.RobotId] = new JsonObject { ["configVersion"] = e.Factsheet.ConfigVersion, ["receivedAt"] = e.ReceivedAt.ToString("O") };
        json["HdAmrFactsheet"] = meta;   // 설정 키로도 읽히지만 쓰는 곳 없음 — 재시작 뒤 버전 비교용
        lock (_gate)
        {
            // 내용이 같으면(retained 재수신·재접속) 파일을 다시 쓰지 않는다 — 시각 필드는 비교에서 뺀다
            var body = json.DeepClone().AsObject();
            body.Remove("HdAmrFactsheet");
            JsonObject? old = null;
            try { old = File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject : null; } catch (JsonException) { }
            old?.Remove("HdAmrFactsheet");
            if (old is not null && JsonNode.DeepEquals(old, body) && pending.All(e => prevMeta.TryGetValue(e.RobotId, out var pv) && pv == e.Factsheet.ConfigVersion))
                goto Written;
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
            if (_config is IConfigurationRoot root) root.Reload();
        Written:;
        }

        foreach (var e in pending)
        {
            _latest[e.RobotId] = e with { Applied = true, Note = conflicts.Count > 0 ? $"AMR 끼리 값이 달라 반영 안 함: {string.Join(", ", conflicts)}" : null };
            int? prev = prevMeta.TryGetValue(e.RobotId, out var pv) ? pv : null;
            var changed = prev is { } p && e.Factsheet.ConfigVersion > p;
            if (prev == e.Factsheet.ConfigVersion) continue;   // 같은 버전 재수신 — 기록 없음
            db.AuditLogs.Add(new AuditLogEntity
            {
                UserId = "AMR", Action = "AMR_FACTSHEET_APPLY", Target = e.RobotId,
                Detail = JsonSerializer.Serialize(new
                {
                    configVersion = e.Factsheet.ConfigVersion, previousVersion = prev, changed = e.Factsheet.Changed,
                    keys = e.Keys, conflicts, shadowedByPage = overridden,
                }),
            });
            if (changed)
            {
                var what = e.Factsheet.Changed.Count > 0 ? string.Join("·", e.Factsheet.Changed.Select(SectionName)) : "설정";
                db.Alarms.Add(new AlarmEntity
                {
                    AlarmId = Guid.NewGuid(), AlarmCode = "AMR_CONFIG_CHANGED", RobotId = e.RobotId,
                    Detail = JsonSerializer.Serialize(new
                    {
                        severity = "WARNING",
                        title = $"AMR 설정 변경({what}) — v{prev} → v{e.Factsheet.ConfigVersion}, 리치 시뮬레이션·배차에 반영. 영역 정차·리프트 재확인 필요",
                        changed = e.Factsheet.Changed, shadowedByPage = overridden,
                    }),
                    RaisedAt = DateTimeOffset.UtcNow,
                });
            }
            _log.LogInformation("AMR factsheet 반영 — robot={Robot} v{Version}{Changed} 키 {Count}개{Shadow}{Conflict}",
                e.RobotId, e.Factsheet.ConfigVersion, changed ? $" (v{prev}에서 변경: {string.Join(",", e.Factsheet.Changed)})" : "",
                e.Keys.Count, overridden.Count > 0 ? $" — 화면 설정이 가리는 키: {string.Join(", ", overridden)}" : "",
                conflicts.Count > 0 ? $" — AMR 끼리 충돌로 뺀 키: {string.Join(", ", conflicts)}" : "");
        }
        await db.SaveChangesAsync(ct);
        var hub = scope.ServiceProvider.GetService<IHubContext<MonitoringHub>>();
        if (hub is not null)
            await hub.Clients.All.SendAsync("AmrConfigApplied", pending.Select(e => new { e.RobotId, e.Factsheet.ConfigVersion, e.Factsheet.Changed }), ct);
        return true;
    }

    private static string SectionName(string s) => s switch
    {
        "teaching" => "티칭", "lift" => "리프트", "mount" => "장착 보정", "cobot" => "코봇 값", _ => s,
    };

    /// <summary>마지막으로 반영한 로봇별 설정 버전(파일의 HdAmrFactsheet) — 재시작 뒤에도 변경을 알아채려고.</summary>
    private Dictionary<string, int> ReadMeta()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Path) || JsonNode.Parse(File.ReadAllText(Path))?["HdAmrFactsheet"] is not JsonObject m) return d;
            foreach (var (robot, v) in m)
                if (v?["configVersion"] is JsonValue jv && jv.TryGetValue<int>(out var n)) d[robot] = n;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return d;
    }

    /// <summary>진행 중 run 때문에 미뤄 둔 보고를 run 이 끝난 뒤 반영한다(10초마다 확인).</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }
            try { await TryApplyAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("AMR factsheet 반영 재시도 실패: {Err}", ex.Message); }
        }
    }
}
