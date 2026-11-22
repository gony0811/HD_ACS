using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>정차 묶음 최적화 표 한 줄.</summary>
public sealed record SetCoverRow(int No, string Station, string Lift, int Welds, string Tier, string Margin);

/// <summary>
/// 정차 묶음 최적화(탐욕 set cover) — 선택 층·면의 용접선을 모두 덮는 정차 수를 줄이는 제안을 서버에 계산시키고(작업, 진행률 조회),
/// 결과를 표와 3D 지도(제안 정차가 맡는 범위 = 영역 음영)로 보인다. 조회 전용 — 영역·정차를 바꾸지 않는다.
/// </summary>
public sealed partial class ReachSimViewModel
{
    private CancellationTokenSource? _setCoverCts;
    private Guid? _setCoverJob;
    private bool _mapIsSetCover;

    public ObservableCollection<SetCoverRow> SetCoverRows { get; } = new();
    [ObservableProperty] private bool _isSetCovering;
    [ObservableProperty] private string _setCoverSummary = "";
    /// <summary>정차 후보 격자 간격 [m] — 작을수록 정차가 조금 줄지만 계산이 길다(천장 L4: 0.3 m 약 4.5분, 0.5 m 약 1.2분).</summary>
    [ObservableProperty] private double _setCoverGridStepM = 0.3;
    [ObservableProperty] private SetCoverPlanDto? _setCoverPlan;
    [ObservableProperty] private bool _isApplyingSetCover;
    /// <summary>적용할 결과가 있는가 — 계산이 끝났고 아직 적용하지 않았을 때.</summary>
    public bool CanApplySetCover => SetCoverPlan is not null && !IsSetCovering && !IsApplyingSetCover && !_setCoverApplied;
    private bool _setCoverApplied;

    partial void OnSetCoverPlanChanged(SetCoverPlanDto? value) => OnPropertyChanged(nameof(CanApplySetCover));
    partial void OnIsSetCoveringChanged(bool value) { OnPropertyChanged(nameof(CanApplySetCover)); OnPropertyChanged(nameof(CanGrid)); }
    partial void OnIsApplyingSetCoverChanged(bool value) => OnPropertyChanged(nameof(CanApplySetCover));

    /// <summary>
    /// 제안대로 그 면·층의 영역을 다시 구성한다 — 정차 하나 = 영역 하나(정차·리프트 덮어쓰기), 못 닿는·임시 제외 작업은 X 영역.
    /// taskId·검사 기록은 유지, 영역 ID 는 작업이 가장 많던 기존 영역에서 물려받는다. 확인 창을 거친다.
    /// </summary>
    [RelayCommand]
    public async Task ApplySetCoverAsync()
    {
        if (!CanApplySetCover || SetCoverPlan is not { } plan || _setCoverJob is not { } job) return;
        var x = plan.Uncovered.Count;
        var msg = $"{LevelLabel.Of(plan.Level)} {plan.WallCode} 의 영역을 제안대로 다시 구성합니다.\n\n"
                  + $"· 영역(정차) {plan.CurrentStations}곳 → {plan.Stations.Count}곳{(x > 0 ? $" + 못 닿는 용접선 X 영역" : "")}\n"
                  + "· 작업(taskId)·검사 기록은 그대로, 영역 이름은 L{층}-S001… 로 바뀝니다.\n"
                  + "· X 영역은 시나리오 검사 대상에서 빼세요.\n"
                  + "· 감사로그에 적용 전 배치가 남지만 자동 되돌리기는 없습니다. 적용 뒤 파일 ▸ 저장으로 .hdacs 에 반영하세요.\n\n계속할까요?";
        if (_confirm is not null && !await _confirm.ConfirmAsync(msg, "정차 묶음 최적화 적용")) return;
        IsApplyingSetCover = true;
        try
        {
            var r = await _api.ApplySetCoverAsync(job);
            if (r is null) { SetCoverSummary = "관제 서버가 적용을 지원하지 않습니다(구빌드) — 서버를 재빌드·재시작하세요."; return; }
            _setCoverApplied = true;
            OnPropertyChanged(nameof(CanApplySetCover));
            var stations = r.Areas.Count(a => !a.Unreachable);
            var xs = r.Areas.Count(a => a.Unreachable);
            SetCoverSummary = $"적용함 — {LevelLabel.Of(r.Level)} {r.WallCode} 영역 {stations}곳" + (xs > 0 ? $" + X 영역 {xs}곳" : "")
                              + $" (영역 ID 물려받음 {r.Areas.Count(a => a.Inherited)} · 지운 기존 영역 {r.DeletedAreas} · 옮긴 작업 {r.TasksMoved}"
                              + (r.ScenarioLinksAdded > 0 ? $" · 시나리오 연결 추가 {r.ScenarioLinksAdded}" : "") + "). 파일 ▸ 저장으로 .hdacs 에 반영하세요.";
            try { ApplyAreaWalls(await _api.GetAreasAsync(TankId)); } catch { /* 면 목록은 그대로 */ }
            await SurveyAsync();
        }
        catch (Exception ex) { SetCoverSummary = $"적용 실패: {ex.Message}"; }
        finally { IsApplyingSetCover = false; }
    }

    /// <summary>폴링 간격 — 테스트가 줄인다.</summary>
    public TimeSpan SetCoverPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>선택 층·면의 정차 묶음 최적화를 시작하고 끝날 때까지 진행률을 보인다.</summary>
    [RelayCommand]
    public async Task RunSetCoverAsync()
    {
        if (IsSetCovering) return;
        IsSetCovering = true;
        _setCoverCts = new CancellationTokenSource();
        var ct = _setCoverCts.Token;
        var (level, wall) = (SelectedLevel, SelectedWall);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SetCoverSummary = $"{LevelLabel.Of(level)} {wall} 정차 묶음 최적화 시작…";
            _setCoverJob = await _api.StartSetCoverAsync(TankId, level, wall, SetCoverGridStepM, null, ct);
            if (_setCoverJob is not { } job)
            {
                SetCoverSummary = "관제 서버가 정차 묶음 최적화를 지원하지 않습니다(구빌드) — 서버를 재빌드·재시작하세요.";
                return;
            }
            while (true)
            {
                await Task.Delay(SetCoverPollInterval, ct);
                var j = await _api.GetSetCoverAsync(job, ct);
                if (j is null) { SetCoverSummary = "작업이 사라졌습니다(서버 재시작)."; return; }
                if (j.State == "RUNNING")
                {
                    SetCoverSummary = j.Total > 0
                        ? $"{LevelLabel.Of(level)} {wall} 계산 중… 정차 후보 {j.Done:N0}/{j.Total:N0} ({sw.Elapsed.TotalSeconds:0}초)"
                        : $"{LevelLabel.Of(level)} {wall} 정차 후보 만드는 중… ({sw.Elapsed.TotalSeconds:0}초)";
                    continue;
                }
                if (j.State != "DONE" || j.Result is null)
                {
                    SetCoverSummary = j.State == "CANCELLED" ? "중지했습니다." : $"정차 묶음 최적화 실패: {j.Error}";
                    return;
                }
                ApplySetCover(j.Result);
                return;
            }
        }
        catch (OperationCanceledException) { SetCoverSummary = "중지했습니다."; }
        catch (Exception ex) { SetCoverSummary = $"정차 묶음 최적화 실패: {ex.Message}"; }
        finally
        {
            IsSetCovering = false;
            _setCoverCts?.Dispose();
            _setCoverCts = null;
        }
    }

    /// <summary>계산 중지 — 서버 작업도 멈춘다.</summary>
    [RelayCommand]
    public async Task CancelSetCoverAsync()
    {
        if (_setCoverJob is { } job)
            try { await _api.CancelSetCoverAsync(job); } catch { /* 이미 끝났을 수 있다 */ }
        _setCoverCts?.Cancel();
    }

    /// <summary>결과를 표·요약에 채우고 3D 지도로 보인다.</summary>
    public void ApplySetCover(SetCoverPlanDto r)
    {
        _setCoverApplied = false;
        SetCoverPlan = r;
        var ci = CultureInfo.InvariantCulture;
        SetCoverRows.Clear();
        foreach (var s in r.Stations)
            SetCoverRows.Add(new SetCoverRow(s.No,
                $"({s.X.ToString("0.00", ci)}, {s.Y.ToString("0.00", ci)}) {(s.Theta * 180 / Math.PI).ToString("0", ci)}°",
                $"{s.StrokeMm.ToString("0", ci)} mm", s.TaskIds.Count, s.Tier == "Safe" ? "여유 확보" : "간섭 없음(여유 부족)",
                s.MinMarginDeg is { } m ? $"{m.ToString("0.0", ci)}°" : "-"));
        SetCoverSummary = Describe(r);

        // 3D — 제안 정차마다 맡는 용접선의 범위를 영역 음영으로, 못 덮는 용접선은 빨강으로
        _mapAreas = ToMapAreas(r);
        _mapIsSetCover = true;
        var idx = Levels.IndexOf(r.Level);
        AreaMapScopeIndex = idx >= 0 ? idx + 1 : 0;   // 그 층만 보이게
        RebuildAreaMap();
        if (AreaMapMode) { SceneChanged?.Invoke(this, EventArgs.Empty); ViewReset?.Invoke(this, EventArgs.Empty); }
        else AreaMapMode = true;
    }

    /// <summary>요약 한 줄 — 제안 정차 수와 덮은 용접선, 지금 배치와 비교.</summary>
    public static string Describe(SetCoverPlanDto r)
    {
        var covered = r.SafeCovered + r.ClearCovered;
        return $"{LevelLabel.Of(r.Level)} {r.WallCode}: 제안 정차 {r.Stations.Count}곳 — 용접선 {covered}/{r.Welds} 간섭 없이 덮음"
               + $"(여유 확보 {r.SafeCovered}{(r.ClearCovered > 0 ? $" · 여유 부족 {r.ClearCovered}" : "")})"
               + (r.Uncovered.Count > 0 ? $" · 어느 정차로도 못 닿음 {r.Uncovered.Count}" : "")
               + $"  |  지금 배치: 정차 {r.CurrentStations}곳 · 간섭 없이 닿음 {r.CurrentClear}"
               + $"  |  후보 {r.Cells:N0} · 판정 {r.Evaluations:N0} · {r.ElapsedMs / 1000.0:0.0}초 — 조회 전용(영역은 바뀌지 않음)";
    }

    /// <summary>결과 → 지도 영역(제안 정차 하나 = 영역 하나, 범위 = 맡은 용접선 외곽) + 못 덮은 용접선(범위 없음, 빨강).</summary>
    public static List<ReachAreaVerdictDto> ToMapAreas(SetCoverPlanDto r)
    {
        var list = new List<ReachAreaVerdictDto>();
        foreach (var s in r.Stations)
        {
            var pts = s.Seams.SelectMany(w => new[] { (w.StartU, w.StartV), (w.EndU, w.EndV) }).ToList();
            const double pad = 0.05;
            double u0 = pts.Min(p => p.Item1) - pad, u1 = pts.Max(p => p.Item1) + pad;
            double v0 = pts.Min(p => p.Item2) - pad, v1 = pts.Max(p => p.Item2) + pad;
            list.Add(new ReachAreaVerdictDto(Guid.NewGuid(), $"#{s.No}", r.WallCode, r.Level,
                new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } },
                s.Seams.Count, s.Seams.Count, 0, true, s.StrokeMm, null, s.Seams));
        }
        if (r.UncoveredSeams is { Count: > 0 } bad)
            list.Add(new ReachAreaVerdictDto(Guid.Empty, "못 닿음", r.WallCode, r.Level, Array.Empty<double[]>(),
                bad.Count, 0, 0, false, 0, null, bad));
        return list;
    }
}
