using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Reach;

namespace HD.Acs.App.Services;

/// <summary>
/// 리치 시뮬레이션 화면에서 바꾸는 리프트·차체 설정(요청 본문). null = 덮어쓰기 없음(appsettings.json 값 사용).
/// </summary>
public sealed record ReachSettingsUpdate(
    string? LiftMode, double? FixedStrokeMm, double? MountZMm, double? AmrHeightMm, bool? SupportColumn,
    double? MaxEntryJointTravelDeg, string? UserId = null);

/// <summary>지금 적용 중인 값 + 화면에서 덮어쓴 키.</summary>
public sealed record ReachSettingsView(
    string LiftMode, double? FixedStrokeMm, double MountZMm, double AmrHeightMm, bool SupportColumn,
    double MaxEntryJointTravelDeg, IReadOnlyList<string> Overridden, string OverrideFile, string? Warning,
    IReadOnlyList<string>? AmrReported = null);

/// <summary>
/// 리치·간섭 화면에서 바꾸는 운영 설정 — <c>Acs:Lift:Mode</c>·<c>FixedStrokeMm</c>, <c>Acs:Reach:MountZMm</c>·<c>AmrHeightMm</c>·
/// <c>SupportColumn</c>·<c>MaxEntryJointTravelDeg</c>. appsettings.json 은 고치지 않고 덮어쓰기 파일
/// (<see cref="FileName"/>, 실행 폴더 — appsettings.json 다음에 읽어 그 값을 이긴다)에 써서 설정을 다시 읽는다.
/// 설정을 요청마다 읽는 리치 시뮬레이션·배차(<see cref="InspectionDispatcher"/> 스코프)가 다음 요청부터 바로 새 값을 쓴다 — 재시작 불필요,
/// 재시작 뒤에도 유지된다. 파일을 지우면(또는 <see cref="Reset"/>) appsettings.json 값으로 돌아간다.
/// </summary>
public sealed class ReachRuntimeSettings
{
    public const string FileName = "appsettings.reach-override.json";

    /// <summary>덮어쓰는 설정 키(콜론 경로) — 화면 항목과 1:1.</summary>
    public static readonly string[] Keys =
    {
        "Acs:Lift:Mode", "Acs:Lift:FixedStrokeMm", "Acs:Reach:MountZMm", "Acs:Reach:AmrHeightMm",
        "Acs:Reach:SupportColumn", "Acs:Reach:MaxEntryJointTravelDeg",
    };

    private readonly IConfiguration _config;
    private readonly object _gate = new();

    public string Path { get; }

    public ReachRuntimeSettings(IConfiguration config, string path)
    {
        _config = config;
        Path = path;
    }

    /// <summary>지금 적용 중인 값(설정에서 읽은 그대로 — 모델이 쓰는 범위 검사를 거친 값).</summary>
    public ReachSettingsView Read()
    {
        var lift = LiftSettings.From(_config);
        var d = ReachRobotModel.Default;
        var model = ReachSimService.ModelFrom(_config);
        return new ReachSettingsView(
            lift.Mode.ToString(), lift.FixedStrokeMm,
            model.MountZMm, model.AmrHeightMm, model.SupportColumn, model.MaxEntryJointTravelDeg,
            Overrides().Keys.ToList(), Path, lift.Warning, AmrReportedKeys());
    }

    /// <summary>AMR factsheet 로 보고돼 반영된 화면 항목 키(<see cref="AmrFactsheetService.FileName"/>, 기본 <c>Acs:Reach</c> 묶음만).</summary>
    public IReadOnlyList<string> AmrReportedKeys() =>
        ReadKeys(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? ".", AmrFactsheetService.FileName)).Keys.ToList();

    /// <summary>덮어쓰기 파일에 적힌 키 → 값.</summary>
    public IReadOnlyDictionary<string, string> Overrides() => ReadKeys(Path);

    private static IReadOnlyDictionary<string, string> ReadKeys(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            foreach (var key in Keys)
            {
                JsonNode? n = root;
                foreach (var part in key.Split(':')) n = n?[part];
                if (n is not null) map[key] = n.ToJsonString().Trim('"');
            }
        }
        catch (JsonException) { /* 깨진 파일 — 덮어쓰기 없음으로 본다(저장하면 다시 쓴다) */ }
        return map;
    }

    /// <summary>요청 검사 — 위반 사유 목록(비면 통과). 범위는 설정을 읽을 때와 같다.</summary>
    public static List<string> Validate(ReachSettingsUpdate u)
    {
        var errors = new List<string>();
        LiftMode? mode = null;
        if (u.LiftMode is { } m)
        {
            if (Enum.TryParse<LiftMode>(m.Trim(), true, out var pm) && Enum.IsDefined(pm)) mode = pm;
            else errors.Add($"리프트 방식 '{m}' 는 Off·Auto·Fixed 가 아님");
        }
        if (u.FixedStrokeMm is { } f && !(double.IsFinite(f) && f >= 0 && f <= LiftSettings.MaxStrokeMm))
            errors.Add($"고정 리프트 높이 {f} mm — 0~{LiftSettings.MaxStrokeMm:0} 만");
        if (mode == LiftMode.Fixed && u.FixedStrokeMm is null)
            errors.Add("리프트 고정이면 고정 높이를 넣어야 합니다");
        if (u.MountZMm is { } z && !(double.IsFinite(z) && z >= 300 && z <= 3000))
            errors.Add($"코봇 BASE 높이 {z} mm — 300~3000 만");
        if (u.AmrHeightMm is { } h && !(double.IsFinite(h) && h >= 300 && h <= 1500))
            errors.Add($"차체 윗면 높이 {h} mm — 300~1500 만");
        if (u.MaxEntryJointTravelDeg is { } t && !(double.IsFinite(t) && t >= 30 && t <= 360))
            errors.Add($"진입 관절 이동 한계 {t}° — 30~360 만");
        return errors;
    }

    /// <summary>
    /// 덮어쓰기 파일을 요청 값으로 통째로 바꾸고(null 항목은 빼서 appsettings.json 값으로) 설정을 다시 읽는다.
    /// 검사는 부른 쪽이 <see cref="Validate"/> 로 먼저 한다.
    /// </summary>
    public ReachSettingsView Save(ReachSettingsUpdate u)
    {
        var lift = new JsonObject();
        if (u.LiftMode is { } m && Enum.TryParse<LiftMode>(m.Trim(), true, out var pm)) lift["Mode"] = pm.ToString();
        if (u.FixedStrokeMm is { } f) lift["FixedStrokeMm"] = f;
        var reach = new JsonObject();
        if (u.MountZMm is { } z) reach["MountZMm"] = z;
        if (u.AmrHeightMm is { } h) reach["AmrHeightMm"] = h;
        if (u.SupportColumn is { } c) reach["SupportColumn"] = c;
        if (u.MaxEntryJointTravelDeg is { } t) reach["MaxEntryJointTravelDeg"] = t;
        var acs = new JsonObject();
        if (lift.Count > 0) acs["Lift"] = lift;
        if (reach.Count > 0) acs["Reach"] = reach;
        var root = new JsonObject
        {
            ["_comment"] = "리치·간섭 화면에서 저장한 값 — appsettings.json 보다 우선. 지우면 appsettings.json 값으로 돌아간다.",
            ["Acs"] = acs,
        };
        lock (_gate)
        {
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
            Reload();
        }
        return Read();
    }

    /// <summary>덮어쓰기를 모두 지운다(appsettings.json 값으로).</summary>
    public ReachSettingsView Reset() => Save(new ReachSettingsUpdate(null, null, null, null, null, null));

    /// <summary>파일 감시(reloadOnChange)를 기다리지 않고 바로 다시 읽는다 — 다음 요청부터 새 값.</summary>
    private void Reload()
    {
        if (_config is IConfigurationRoot r) r.Reload();
    }

    /// <summary>값 목록을 감사로그용 문자열로.</summary>
    public static string Describe(ReachSettingsUpdate u) => string.Join(", ", new[]
    {
        u.LiftMode is { } m ? $"Lift.Mode={m}" : null,
        u.FixedStrokeMm is { } f ? $"Lift.FixedStrokeMm={f.ToString(CultureInfo.InvariantCulture)}" : null,
        u.MountZMm is { } z ? $"MountZMm={z.ToString(CultureInfo.InvariantCulture)}" : null,
        u.AmrHeightMm is { } h ? $"AmrHeightMm={h.ToString(CultureInfo.InvariantCulture)}" : null,
        u.SupportColumn is { } c ? $"SupportColumn={c}" : null,
        u.MaxEntryJointTravelDeg is { } t ? $"MaxEntryJointTravelDeg={t.ToString(CultureInfo.InvariantCulture)}" : null,
    }.OfType<string>());
}
