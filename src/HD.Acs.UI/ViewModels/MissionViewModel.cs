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

    [ObservableProperty] private ScenarioSummaryDto? _selectedScenario;
    [ObservableProperty] private string? _selectedRobotId;
    [ObservableProperty] private ScenarioRunDto? _currentRun;
    [ObservableProperty] private string? _statusMessage;

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
        ReleaseNextCommand.NotifyCanExecuteChanged();
        RefreshCurrentRunCommand.NotifyCanExecuteChanged();
    }

    private void OnMissionProgress(object? sender, MissionProgressDto p)
    {
        for (int i = 0; i < Missions.Count; i++)
        {
            if (Missions[i].MissionId != p.MissionId) continue;
            Missions[i] = Missions[i] with { State = p.State };
            return;
        }
    }

    partial void OnSelectedScenarioChanged(ScenarioSummaryDto? value) => StartRunCommand.NotifyCanExecuteChanged();
    partial void OnSelectedRobotIdChanged(string? value) => StartRunCommand.NotifyCanExecuteChanged();
    partial void OnCurrentRunChanged(ScenarioRunDto? value)
    {
        ReleaseNextCommand.NotifyCanExecuteChanged();
        RefreshCurrentRunCommand.NotifyCanExecuteChanged();
    }
}
