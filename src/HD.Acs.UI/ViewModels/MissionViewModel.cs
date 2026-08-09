using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 미션 디스패치·제어: 시나리오 선택 → 실행(POST /api/runs) → 층 미션 진행(Seq) 표시 → 다음 층 릴리스.
/// MissionProgress 푸시로 미션 상태를 갱신한다. run 상태 WAITING_FLOOR_TRANSFER는 층 전환(수동, Q9) 대기.
/// </summary>
public sealed partial class MissionViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<ScenarioSummaryDto> Scenarios { get; } = new();
    public ObservableCollection<string> RobotIds { get; } = new();
    public ObservableCollection<MissionDto> Missions { get; } = new();

    /// <summary>운영 화면 좌측 "층 진행 레일" 바인딩 소스 — Missions에서 층별 상태로 파생.</summary>
    public ObservableCollection<FloorProgressItem> FloorProgress { get; } = new();

    [ObservableProperty] private ScenarioSummaryDto? _selectedScenario;
    [ObservableProperty] private string? _selectedRobotId;
    [ObservableProperty] private ScenarioRunDto? _currentRun;
    [ObservableProperty] private string? _statusMessage;
    /// <summary>시나리오 생성 입력(계획 ▸ 시나리오 관리).</summary>
    [ObservableProperty] private string _newScenarioName = "";
    /// <summary>현재 프로젝트 선창 — 시나리오 생성 대상. Shell이 열기/새 프로젝트 시 동기화.</summary>
    [ObservableProperty] private string _tankId = "CT1";
    /// <summary>층 진행 요약(예: "2 / 4 층 완료"). 검사점 단위 %는 백엔드 미노출 — 층 단위 coarse 집계.</summary>
    [ObservableProperty] private string _progressSummary = "실행 중 미션 없음";

    public MissionViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.MissionProgressReceived += OnMissionProgress;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Scenarios.Clear();
            foreach (var s in await _api.GetScenariosAsync())
                Scenarios.Add(s);
            SelectedScenario ??= Scenarios.FirstOrDefault();

            RobotIds.Clear();
            foreach (var r in await _api.GetRobotsAsync())
                RobotIds.Add(r.RobotId);
            SelectedRobotId ??= RobotIds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"시나리오/로봇 조회 실패: {ex.Message}";
        }
    }

    // ── 시나리오 관리 (계획 ▸ 시나리오) — 운영 콤보와 같은 Scenarios 컬렉션을 공유 ──
    private bool CanCreateScenario() => !string.IsNullOrWhiteSpace(NewScenarioName);

    [RelayCommand(CanExecute = nameof(CanCreateScenario))]
    private async Task CreateScenarioAsync()
    {
        try
        {
            var id = await _api.CreateScenarioAsync(NewScenarioName.Trim(), TankId);
            await LoadAsync();
            SelectedScenario = Scenarios.FirstOrDefault(s => s.ScenarioId == id) ?? SelectedScenario;
            StatusMessage = $"시나리오 생성됨: {NewScenarioName.Trim()}";
            NewScenarioName = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"시나리오 생성 실패: {ex.Message}";
        }
    }

    private bool CanDeleteScenario() => SelectedScenario is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteScenario))]
    private async Task DeleteScenarioAsync()
    {
        var sc = SelectedScenario;
        if (sc is null) return;
        try
        {
            await _api.DeleteScenarioAsync(sc.ScenarioId);
            SelectedScenario = null;      // 목록 재적재 시 첫 항목이 선택되도록
            await LoadAsync();
            StatusMessage = $"시나리오 삭제됨: {sc.Name}";
        }
        catch (Exception ex)
        {
            // 참조 run 있으면 409 메시지가 여기로 노출됨
            StatusMessage = $"시나리오 삭제 실패: {ex.Message}";
        }
    }

    private bool CanStartRun() => SelectedScenario is not null && !string.IsNullOrWhiteSpace(SelectedRobotId);

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync()
    {
        try
        {
            var runId = await _api.StartRunAsync(SelectedScenario!.ScenarioId, SelectedRobotId!);
            await RefreshRunAsync(runId);
            StatusMessage = $"미션 실행 시작: run {runId}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"미션 시작 실패: {ex.Message}";
        }
    }

    private bool CanReleaseNext() => CurrentRun is not null;

    [RelayCommand(CanExecute = nameof(CanReleaseNext))]
    private async Task ReleaseNextAsync()
    {
        if (CurrentRun is null) return;
        try
        {
            var released = await _api.ReleaseNextMissionAsync(CurrentRun.RunId);
            StatusMessage = released
                ? "다음 층 미션 릴리스됨."
                : "릴리스 보류 — 로봇 보고 층(mapId)이 아직 일치하지 않습니다. (수동 층 변경 확인)";
            await RefreshRunAsync(CurrentRun.RunId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"릴리스 실패: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanReleaseNext))]
    private async Task RefreshCurrentRunAsync()
    {
        if (CurrentRun is not null) await RefreshRunAsync(CurrentRun.RunId);
    }

    private async Task RefreshRunAsync(Guid runId)
    {
        var run = await _api.GetRunAsync(runId);
        CurrentRun = run;
        Missions.Clear();
        if (run is not null)
            foreach (var m in run.Missions.OrderBy(m => m.Seq))
                Missions.Add(m);
        RebuildFloorProgress();
        ReleaseNextCommand.NotifyCanExecuteChanged();
        RefreshCurrentRunCommand.NotifyCanExecuteChanged();
    }

    private void OnMissionProgress(object? sender, MissionProgressDto p)
    {
        for (int i = 0; i < Missions.Count; i++)
        {
            if (Missions[i].MissionId != p.MissionId) continue;
            Missions[i] = Missions[i] with { State = p.State };
            RebuildFloorProgress();
            return;
        }
    }

    /// <summary>
    /// Missions(층=미션 1:1)에서 층 진행 레일을 파생한다. 검사점 단위 진행률은 백엔드 미노출이라
    /// 미션 State로 완료/진행/대기/실패만 분류하는 coarse 표시다.
    /// </summary>
    private void RebuildFloorProgress()
    {
        FloorProgress.Clear();
        int done = 0;
        foreach (var m in Missions)
        {
            var kind = Classify(m.State);
            if (kind == "done") done++;
            var level = m.MapId.Contains('-') ? m.MapId[(m.MapId.LastIndexOf('-') + 1)..] : m.MapId;
            double frac = kind switch { "done" => 1.0, "fail" => 1.0, "run" => 0.5, _ => 0.0 };
            FloorProgress.Add(new FloorProgressItem(level, m.MapId, m.State, kind, frac, m.Seq));
        }
        ProgressSummary = Missions.Count == 0 ? "실행 중 미션 없음" : $"{done} / {Missions.Count} 층 완료";
    }

    /// <summary>미션 State 문자열을 done/run/wait/fail로 분류(정확한 enum 명칭에 독립적).</summary>
    private static string Classify(string? state)
    {
        var s = (state ?? "").ToUpperInvariant();
        if (s.Contains("FAIL") || s.Contains("ABORT") || s.Contains("ERROR")) return "fail";
        if (s.Contains("COMPLET") || s.Contains("FINISH") || s.Contains("DONE")) return "done";
        if (s.Contains("RUN") || s.Contains("ACTIVE") || s.Contains("EXEC") || s.Contains("PROGRESS")
            || s.Contains("RELEAS") || s.Contains("WAIT")) return "run";
        return "wait";
    }

    partial void OnSelectedScenarioChanged(ScenarioSummaryDto? value)
    {
        StartRunCommand.NotifyCanExecuteChanged();
        DeleteScenarioCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedRobotIdChanged(string? value) => StartRunCommand.NotifyCanExecuteChanged();
    partial void OnNewScenarioNameChanged(string value) => CreateScenarioCommand.NotifyCanExecuteChanged();
    partial void OnCurrentRunChanged(ScenarioRunDto? value)
    {
        ReleaseNextCommand.NotifyCanExecuteChanged();
        RefreshCurrentRunCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>운영 화면 층 진행 레일 항목. Kind = done|run|wait|fail, Fraction = 미니 진행바 채움(0~1).</summary>
public sealed record FloorProgressItem(string Level, string MapId, string State, string Kind, double Fraction, int Seq);
