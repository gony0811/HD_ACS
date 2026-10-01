using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>검사 RUN 및 TASK별 최종 성공/실패 이력.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private bool _loading;

    public ObservableCollection<HistoryRunRow> Runs { get; } = new();
    public ObservableCollection<HistoryTaskRow> Results { get; } = new();

    [ObservableProperty] private HistoryRunRow? _selectedRun;
    [ObservableProperty] private string _statusMessage = "이력을 불러오세요.";

    public HistoryViewModel(IAcsApiClient api) => _api = api;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            StatusMessage = "검사 이력 조회 중…";
            var runs = await _api.GetRunsAsync(50);
            var rows = new List<HistoryRunRow>();
            foreach (var run in runs)
            {
                var results = await _api.GetRunResultsAsync(run.RunId);
                var items = results?.Items ?? Array.Empty<RunTaskResultDto>();
                int success = items.Count(i => i.Status == "SUCCESS");
                int failed = items.Count(i => i.Status is "FAILED" or "SKIPPED");
                string outcome = failed > 0 ? "실패" : items.Count > 0 && success == items.Count ? "성공" : "진행/결과 없음";
                string? reason = items.LastOrDefault(i => i.Status is "FAILED" or "SKIPPED")?.Description;
                rows.Add(new HistoryRunRow(run, outcome, success, failed, reason));
            }

            var keep = SelectedRun?.RunId;
            Runs.Clear();
            foreach (var row in rows) Runs.Add(row);
            SelectedRun = keep is Guid id ? Runs.FirstOrDefault(r => r.RunId == id) : Runs.FirstOrDefault();
            StatusMessage = $"최근 RUN {Runs.Count}건";
        }
        catch (Exception ex) { StatusMessage = $"이력 조회 실패: {ex.Message}"; }
        finally { _loading = false; }
    }

    partial void OnSelectedRunChanged(HistoryRunRow? value) => _ = LoadResultsAsync(value);

    private async Task LoadResultsAsync(HistoryRunRow? run)
    {
        Results.Clear();
        if (run is null) return;
        try
        {
            var result = await _api.GetRunResultsAsync(run.RunId);
            if (SelectedRun?.RunId != run.RunId) return;
            foreach (var item in result?.Items ?? Array.Empty<RunTaskResultDto>())
                Results.Add(new HistoryTaskRow(item));
        }
        catch (Exception ex) { StatusMessage = $"상세 이력 조회 실패: {ex.Message}"; }
    }
}

public sealed class HistoryRunRow
{
    public HistoryRunRow(RunSummaryDto run, string outcome, int success, int failed, string? failureReason)
    {
        RunId = run.RunId; ScenarioName = run.ScenarioName ?? run.ScenarioId.ToString(); TankId = run.TankId ?? "-";
        RobotId = run.RobotId; State = run.State; StartedAt = run.StartedAt; EndedAt = run.EndedAt;
        Outcome = outcome; Success = success; Failed = failed; FailureReason = failureReason ?? "";
    }
    public Guid RunId { get; }
    public string ScenarioName { get; }
    public string TankId { get; }
    public string RobotId { get; }
    public string State { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? EndedAt { get; }
    public string Outcome { get; }
    public int Success { get; }
    public int Failed { get; }
    public string FailureReason { get; }
}

public sealed class HistoryTaskRow
{
    public HistoryTaskRow(RunTaskResultDto item)
    {
        TaskId = item.TaskId; AreaName = item.AreaName ?? "-"; WallCode = item.WallCode ?? "-";
        Level = item.Level; Status = item.Status; Attempts = item.Attempts; OccurredAt = item.OccurredAt;
        Description = item.Description ?? (item.Status == "SUCCESS" ? "정상 완료" : "AMR 실패 사유 미제공");
    }
    public Guid TaskId { get; }
    public string AreaName { get; }
    public string WallCode { get; }
    public int? Level { get; }
    public string Status { get; }
    public int Attempts { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Description { get; }
}
