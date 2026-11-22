using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 리치·간섭 화면의 리프트·차체 설정 — 리프트 운영 방식(미사용·자동·고정)·고정 높이·코봇 BASE 높이·차체 윗면 높이·받침 기둥·
/// 진입 관절 이동 한계. [설정 적용]이면 서버가 덮어쓰기 파일에 저장하고 바로 다시 읽어, 다음 조사부터 새 값으로 계산한다
/// (배차 지령의 리프트 높이도 같은 설정). 바꾸지 않은 항목은 appsettings.json 값 그대로 둔다.
/// </summary>
public sealed partial class ReachSimViewModel
{
    /// <summary>서버 값(Off·Auto·Fixed) — <see cref="LiftModeLabels"/> 와 같은 순서.</summary>
    public static readonly string[] LiftModeValues = { "Off", "Auto", "Fixed" };
    public IReadOnlyList<string> LiftModeLabels { get; } = new[] { "미사용 (Off)", "자동 (Auto — 0~1000 mm 탐색)", "고정 (Fixed)" };

    private const string KeyMode = "Acs:Lift:Mode";
    private const string KeyFixed = "Acs:Lift:FixedStrokeMm";
    private const string KeyMountZ = "Acs:Reach:MountZMm";
    private const string KeyAmrHeight = "Acs:Reach:AmrHeightMm";
    private const string KeyColumn = "Acs:Reach:SupportColumn";
    private const string KeyEntry = "Acs:Reach:MaxEntryJointTravelDeg";

    /// <summary>마지막으로 서버에서 받은 값 — 바뀐 항목만 덮어쓰기 위해 비교한다.</summary>
    private ReachSettingsDto? _settings;

    [ObservableProperty] private int _liftModeIndex = 1;
    [ObservableProperty] private double _settingFixedStrokeMm;
    [ObservableProperty] private double _settingMountZMm;
    [ObservableProperty] private double _settingAmrHeightMm;
    [ObservableProperty] private bool _settingSupportColumn;
    [ObservableProperty] private double _settingEntryTravelDeg;
    [ObservableProperty] private string _settingsStatus = "";
    /// <summary>서버가 설정 화면을 지원하는가(구서버면 입력칸을 끈다).</summary>
    [ObservableProperty] private bool _settingsAvailable;
    [ObservableProperty] private bool _isSavingSettings;

    public bool IsFixedLift => LiftModeIndex == 2;
    /// <summary>받침 기둥은 리프트를 쓰지 않을 때만 의미가 있다.</summary>
    public bool IsLiftOff => LiftModeIndex == 0;
    public string LiftModeValue => LiftModeValues[Math.Clamp(LiftModeIndex, 0, LiftModeValues.Length - 1)];

    partial void OnLiftModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFixedLift));
        OnPropertyChanged(nameof(IsLiftOff));
        OnPropertyChanged(nameof(LiftModeValue));
    }

    /// <summary>서버의 지금 설정을 읽어 입력칸에 채운다.</summary>
    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        try
        {
            var s = await _api.GetReachSettingsAsync();
            if (s is null)
            {
                SettingsAvailable = false;
                SettingsStatus = "서버가 이 설정 화면을 지원하지 않습니다(구빌드) — 관제 서버를 재빌드·재시작하세요.";
                return;
            }
            ApplySettings(s);
            SettingsStatus = DescribeSettings(s);
        }
        catch (Exception ex)
        {
            SettingsAvailable = false;
            SettingsStatus = $"설정 조회 실패: {ex.Message}";
        }
    }

    private void ApplySettings(ReachSettingsDto s)
    {
        _settings = s;
        SettingsAvailable = true;
        LiftModeIndex = Math.Max(0, Array.FindIndex(LiftModeValues, v => string.Equals(v, s.LiftMode, StringComparison.OrdinalIgnoreCase)));
        SettingFixedStrokeMm = s.FixedStrokeMm ?? 0;
        SettingMountZMm = s.MountZMm;
        SettingAmrHeightMm = s.AmrHeightMm;
        SettingSupportColumn = s.SupportColumn;
        SettingEntryTravelDeg = s.MaxEntryJointTravelDeg;
    }

    /// <summary>
    /// 저장 요청 — 지금 서버 값과 다르거나 이미 화면에서 덮어쓴 항목만 싣는다(나머지는 appsettings.json 값 유지).
    /// 고정 모드면 고정 높이를 항상 싣는다(서버가 높이 없는 고정을 거부).
    /// </summary>
    public ReachSettingsUpdateDto BuildSettingsUpdate()
    {
        var s = _settings;
        var over = new HashSet<string>(s?.Overridden ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool Keep(string key, bool changed) => s is null || changed || over.Contains(key);
        static bool Diff(double a, double b) => Math.Abs(a - b) > 1e-6;
        var mode = LiftModeValue;
        return new ReachSettingsUpdateDto(
            Keep(KeyMode, !string.Equals(mode, s?.LiftMode, StringComparison.OrdinalIgnoreCase)) ? mode : null,
            IsFixedLift || (s is not null && over.Contains(KeyFixed)) ? SettingFixedStrokeMm : null,
            Keep(KeyMountZ, s is not null && Diff(SettingMountZMm, s.MountZMm)) ? SettingMountZMm : null,
            Keep(KeyAmrHeight, s is not null && Diff(SettingAmrHeightMm, s.AmrHeightMm)) ? SettingAmrHeightMm : null,
            Keep(KeyColumn, s is not null && SettingSupportColumn != s.SupportColumn) ? SettingSupportColumn : null,
            Keep(KeyEntry, s is not null && Diff(SettingEntryTravelDeg, s.MaxEntryJointTravelDeg)) ? SettingEntryTravelDeg : null);
    }

    /// <summary>입력한 값으로 서버 설정을 바꾸고(바로 적용) 지금 층·면을 다시 조사한다.</summary>
    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        if (IsSavingSettings) return;
        IsSavingSettings = true;
        try
        {
            var r = await _api.SaveReachSettingsAsync(BuildSettingsUpdate());
            if (r is null) { SettingsStatus = "서버가 설정 저장을 지원하지 않습니다(구빌드)."; return; }
            ApplySettings(r);
            SettingsStatus = "적용됨 — " + DescribeSettings(r);
            ClearOptima();
            await SurveyAsync();
        }
        catch (Exception ex) { SettingsStatus = $"설정 적용 실패: {ex.Message}"; }
        finally { IsSavingSettings = false; }
    }

    /// <summary>화면에서 덮어쓴 값을 모두 지우고 AMR 보고·appsettings.json 값으로 돌아간 뒤 다시 조사한다.</summary>
    [RelayCommand]
    public async Task ResetSettingsAsync()
    {
        if (IsSavingSettings) return;
        IsSavingSettings = true;
        try
        {
            var r = await _api.ResetReachSettingsAsync();
            if (r is null) { SettingsStatus = "서버가 설정 초기화를 지원하지 않습니다(구빌드)."; return; }
            ApplySettings(r);
            SettingsStatus = "화면 설정 지움(AMR 보고·appsettings.json 값) — " + DescribeSettings(r);
            ClearOptima();
            await SurveyAsync();
        }
        catch (Exception ex) { SettingsStatus = $"설정 초기화 실패: {ex.Message}"; }
        finally { IsSavingSettings = false; }
    }

    /// <summary>설정 요약 한 줄 — 리프트·BASE·차체·기둥 + 덮어쓴 항목 수·경고.</summary>
    public static string DescribeSettings(ReachSettingsDto s)
    {
        var ci = CultureInfo.InvariantCulture;
        var lift = s.LiftMode.ToUpperInvariant() switch
        {
            "OFF" => "리프트 미사용",
            "FIXED" => $"리프트 고정 {(s.FixedStrokeMm ?? 0).ToString("0", ci)} mm",
            _ => "리프트 자동(0~1000 mm)",
        };
        var n = s.Overridden?.Count ?? 0;
        var amr = s.AmrReported?.Count ?? 0;
        return $"{lift} · BASE {s.MountZMm.ToString("0", ci)} mm · 차체 {s.AmrHeightMm.ToString("0", ci)} mm"
               + (s.SupportColumn ? " · 받침 기둥" : "")
               + $" · 진입 한계 {s.MaxEntryJointTravelDeg.ToString("0", ci)}°"
               + (n > 0 ? $" · 화면에서 바꾼 항목 {n}개" : "")
               + (amr > 0 ? $" · AMR 보고 값 {amr}개" : "")
               + (n == 0 && amr == 0 ? " · appsettings.json 값" : "")
               + (s.Warning is { Length: > 0 } w ? $" · 경고: {w}" : "");
    }
}
