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

    /// <summary>실행 큐(work_item) 항목 — "작업 현황" 탭·TankView 상태색의 단일 소스 [INSPECTION_SCENARIO §3.1].
    /// 행 확장(RowDetails)으로 소속 용접라인(TaskRow) 드릴다운.</summary>
    public ObservableCollection<WorkItemRow> WorkItems { get; } = new();

    /// <summary>WorkItems 변경(초기 로드·푸시 단건) 통지 — Shell이 TankView 상태색 갱신에 사용.</summary>
    public event EventHandler? WorkItemsChanged;

    [ObservableProperty] private ScenarioSummaryDto? _selectedScenario;
    [ObservableProperty] private string? _selectedRobotId;
    [ObservableProperty] private ScenarioRunDto? _currentRun;
    [ObservableProperty] private string? _statusMessage;
    /// <summary>시나리오 생성 입력(계획 ▸ 시나리오 관리).</summary>
    [ObservableProperty] private string _newScenarioName = "";
    /// <summary>현재 프로젝트 선창 — 시나리오 생성 대상. Shell이 열기/새 프로젝트 시 동기화.</summary>
    [ObservableProperty] private string _tankId = "CT1";
    /// <summary>층 진행 요약(예: "2 / 4 층 완료"). 층 단위 coarse 집계.</summary>
    [ObservableProperty] private string _progressSummary = "실행 중 미션 없음";

    /// <summary>TASK 단위 진행률 스냅샷(완료/전체·%). RunProgress 푸시·초기 pull로 갱신.</summary>
    [ObservableProperty] private RunProgressDto? _taskProgress;
    /// <summary>TASK 진행 요약(예: "검사 12 / 40 (30%) · 실패 1").</summary>
    [ObservableProperty] private string _taskProgressSummary = "검사 TASK 대기 중";
    /// <summary>TASK 진행바 값(0~1).</summary>
    [ObservableProperty] private double _taskProgressFraction;
    /// <summary>작업 현황 요약(예: "완료 5 / 전체 12 · 스킵 1").</summary>
    [ObservableProperty] private string _workSummary = "실행 중 작업 없음";

    public MissionViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.MissionProgressReceived += OnMissionProgress;
        monitoring.RunProgressReceived += OnRunProgress;
        monitoring.WorkItemProgressReceived += OnWorkItemProgress;
        monitoring.TaskActionProgressReceived += OnTaskActionProgress;
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

    // ── 부분 검사 계획 (계획 ▸ 시나리오 — 검사 대상 영역 편집: 좌 풀[+] / 우 대상[−]) ──
    /// <summary>선창 영역 풀(아직 대상이 아닌 영역).</summary>
    public ObservableCollection<ScenarioAreaPick> AvailableAreas { get; } = new();
    /// <summary>검사 대상 목록. 0개 = 선창 전체 검사(하위호환).</summary>
    public ObservableCollection<ScenarioAreaPick> SelectedAreas { get; } = new();
    [ObservableProperty] private string _scenarioAreaSummary = "";

    private int _areasLoadSeq;   // 재진입 가드 — 시나리오 연속 선택/저장 재로드 시 교차 방지(중복 추가 버그)

    private async Task LoadScenarioAreasAsync()
    {
        var seq = ++_areasLoadSeq;
        var sc = SelectedScenario;
        if (sc is null)
        {
            AvailableAreas.Clear(); SelectedAreas.Clear(); ScenarioAreaSummary = "";
            return;
        }
        try
        {
            var all = await _api.GetAreasAsync(sc.TankId);
            var linked = (await _api.GetScenarioAreasAsync(sc.ScenarioId)).Select(x => x.AreaId).ToHashSet();
            if (seq != _areasLoadSeq) return;   // 그 사이 다른 로드가 시작됨 — 이 결과는 폐기

            // 임시 리스트에 구성 후 한 번에 교체 — await 교차로 인한 중복 원천 차단
            var pool = new List<ScenarioAreaPick>();
            var chosen = new List<ScenarioAreaPick>();
            foreach (var a in all.OrderBy(x => x.Level).ThenBy(x => x.WallCode).ThenBy(x => x.Name))
                (linked.Contains(a.AreaId) ? chosen : pool)
                    .Add(new ScenarioAreaPick(a.AreaId, a.WallCode, a.Level, a.Name));

            AvailableAreas.Clear(); foreach (var p in pool) AvailableAreas.Add(p);
            SelectedAreas.Clear(); foreach (var p in chosen) SelectedAreas.Add(p);
            UpdateScenarioAreaSummary();
        }
        catch (Exception ex)
        {
            if (seq == _areasLoadSeq) StatusMessage = $"대상 영역 조회 실패: {ex.Message}";
        }
    }

    private void UpdateScenarioAreaSummary() =>
        ScenarioAreaSummary = SelectedAreas.Count == 0
            ? $"대상 0개 — 선창 전체 검사 (풀 {AvailableAreas.Count}개)"
            : $"대상 {SelectedAreas.Count}개 — 부분 검사";

    /// <summary>풀 → 대상 (정렬 위치 유지 삽입).</summary>
    [RelayCommand]
    private void AddScenarioArea(ScenarioAreaPick? pick)
    {
        if (pick is null || !AvailableAreas.Remove(pick)) return;
        InsertSorted(SelectedAreas, pick);
        UpdateScenarioAreaSummary();
    }

    /// <summary>대상 → 풀.</summary>
    [RelayCommand]
    private void RemoveScenarioArea(ScenarioAreaPick? pick)
    {
        if (pick is null || !SelectedAreas.Remove(pick)) return;
        InsertSorted(AvailableAreas, pick);
        UpdateScenarioAreaSummary();
    }

    private static void InsertSorted(ObservableCollection<ScenarioAreaPick> list, ScenarioAreaPick pick)
    {
        int i = 0;
        while (i < list.Count &&
               (list[i].Level, list[i].WallCode, list[i].Name).CompareTo((pick.Level, pick.WallCode, pick.Name)) < 0)
            i++;
        list.Insert(i, pick);
    }

    /// <summary>대상 목록을 시나리오에 저장 (0개 = 전체 검사로 복귀).</summary>
    [RelayCommand]
    private async Task SaveScenarioAreasAsync()
    {
        var sc = SelectedScenario;
        if (sc is null) { StatusMessage = "시나리오를 먼저 선택하세요."; return; }
        try
        {
            var ids = SelectedAreas.Select(p => p.AreaId).ToList();
            await _api.SetScenarioAreasAsync(sc.ScenarioId, ids);
            StatusMessage = ids.Count == 0
                ? $"'{sc.Name}' 검사 대상 저장 — 선창 전체 검사"
                : $"'{sc.Name}' 검사 대상 저장 — {ids.Count}개 영역 (부분 검사)";
            await LoadAsync();   // AreaCount 갱신
            SelectedScenario = Scenarios.FirstOrDefault(s => s.ScenarioId == sc.ScenarioId) ?? SelectedScenario;
        }
        catch (Exception ex)
        {
            StatusMessage = $"검사 대상 저장 실패: {ex.Message}";
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

    private bool CanResumeRun() => !string.IsNullOrWhiteSpace(SelectedRobotId);

    /// <summary>이어하기 — 로봇의 가장 최근 재개 가능 run을 찾아 resume (DONE/SKIPPED 보존, 잔여만 재배차).</summary>
    [RelayCommand(CanExecute = nameof(CanResumeRun))]
    private async Task ResumeRunAsync()
    {
        try
        {
            var r = await _api.GetResumableRunAsync(SelectedRobotId!);
            if (r is null) { StatusMessage = "재개할 run이 없습니다 (미종결 작업 보유 run 없음)."; return; }
            await _api.ResumeRunAsync(r.RunId);
            await RefreshRunAsync(r.RunId);
            StatusMessage = $"run 재개: 완료 {r.Done}·스킵 {r.Skipped} 보존, 잔여 {r.Pending}건 재배차.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"재개 실패: {ex.Message}";
        }
    }

    private bool CanAbortRun() => CurrentRun is not null && CurrentRun.State is "RUNNING" or "WAITING_FLOOR_TRANSFER";

    /// <summary>중단 — 후속 배차 중지(진행 중 정차는 완주). 즉시 정지는 비상정지 사용.</summary>
    [RelayCommand(CanExecute = nameof(CanAbortRun))]
    private async Task AbortRunAsync()
    {
        if (CurrentRun is null) return;
        try
        {
            await _api.AbortRunAsync(CurrentRun.RunId);
            await RefreshRunAsync(CurrentRun.RunId);
            StatusMessage = "run 중단됨 — 완료 이력은 보존, '이어하기'로 재개할 수 있습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"중단 실패: {ex.Message}";
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

        // 실행 큐 초기 로드(pull) — 이후 실시간 갱신은 WorkItemProgress/TaskActionProgress 푸시가 담당.
        try
        {
            WorkItems.Clear();
            if (run is not null)
            {
                foreach (var w in await _api.GetWorkItemsAsync(runId))
                {
                    var row = new WorkItemRow(w);
                    // 소속 용접라인 = 계획(area_task) 기준 — 배차 전에는 PLANNED
                    try
                    {
                        foreach (var t in await _api.GetAreaTasksAsync(w.AreaId))
                            row.Tasks.Add(new TaskRow(t.TaskId, t.Seq, t.Name ?? $"라인 {t.Seq}"));
                    }
                    catch { /* 영역 삭제 등 — 라인 없이 표시 */ }
                    WorkItems.Add(row);
                }

                // 액션 상태 병합 — CreatedAt 오름차순 응답이라 나중(최신 시도) 것이 덮어씀
                foreach (var a in await _api.GetTaskActionsAsync(runId))
                {
                    var task = FindTask(a.WorkItemId, a.TaskId);
                    if (task is null) continue;
                    task.Status = a.Status;
                    task.Detail = ExtractResultDescription(a.Result);
                }
            }
        }
        catch { /* 작업 큐 조회 실패는 화면 흐름을 막지 않음 */ }
        RebuildWorkSummary();
        WorkItemsChanged?.Invoke(this, EventArgs.Empty);

        RebuildFloorProgress();

        // TASK 단위 진행률 초기 로드(pull) — 이후 실시간 갱신은 RunProgress 푸시가 담당.
        try { ApplyTaskProgress(run is null ? null : await _api.GetRunProgressAsync(runId)); }
        catch { /* 진행률 조회 실패는 화면 흐름을 막지 않음 */ }

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

    /// <summary>RunProgress 푸시 — 현재 보고 있는 Run의 것만 반영(다른 Run 노이즈 무시).</summary>
    private void OnRunProgress(object? sender, RunProgressDto p)
    {
        if (CurrentRun is null || p.RunId != CurrentRun.RunId) return;
        ApplyTaskProgress(p);
    }

    /// <summary>WorkItemProgress 푸시 — 실행 큐 항목 단건 상태 갱신(배차/완료/재큐잉/스킵).</summary>
    private void OnWorkItemProgress(object? sender, WorkItemProgressDto p)
    {
        if (CurrentRun is null || p.RunId != CurrentRun.RunId) return;
        var row = WorkItems.FirstOrDefault(w => w.WorkItemId == p.WorkItemId);
        if (row is null) return;
        row.Status = p.Status;
        row.Attempts = p.Attempts;
        // 재배차(DISPATCHED) 시 미완료 라인은 새 시도로 리셋 — 새 액션의 WAITING은 발행 시점엔 푸시되지 않음
        if (p.Status == "DISPATCHED")
            foreach (var t in row.Tasks.Where(t => t.Status != "FINISHED"))
                t.Status = "WAITING";
        RebuildWorkSummary();
        RebuildFloorProgress();   // 층 레일 실제 비율 갱신
        WorkItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>TaskActionProgress 푸시 — 용접라인(액션) 단건 상태 갱신(WAITING→RUNNING→FINISHED/FAILED).</summary>
    private void OnTaskActionProgress(object? sender, TaskActionProgressDto p)
    {
        if (CurrentRun is null || p.RunId != CurrentRun.RunId) return;
        var task = FindTask(p.WorkItemId, p.TaskId);
        if (task is null) return;
        task.Status = p.Status;
        task.Detail = p.ResultDescription;
        WorkItemsChanged?.Invoke(this, EventArgs.Empty);   // TankView 용접선 색 갱신
    }

    private TaskRow? FindTask(Guid? workItemId, Guid? taskId)
    {
        if (workItemId is null || taskId is null) return null;
        return WorkItems.FirstOrDefault(w => w.WorkItemId == workItemId)
            ?.Tasks.FirstOrDefault(t => t.TaskId == taskId);
    }

    /// <summary>order_action.Result json({"ActionStatus","ResultDescription"})에서 설명만 추출.</summary>
    private static string? ExtractResultDescription(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(resultJson)?["ResultDescription"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private void RebuildWorkSummary()
    {
        if (WorkItems.Count == 0) { WorkSummary = "실행 중 작업 없음"; return; }
        int done = WorkItems.Count(w => w.Status == "DONE");
        int skipped = WorkItems.Count(w => w.Status == "SKIPPED");
        var skip = skipped > 0 ? $" · 스킵 {skipped}" : "";
        WorkSummary = $"완료 {done} / 전체 {WorkItems.Count}{skip}";
    }

    /// <summary>TASK 진행률 스냅샷을 요약 문자열·진행바 값으로 반영.</summary>
    private void ApplyTaskProgress(RunProgressDto? p)
    {
        TaskProgress = p;
        if (p is null || p.TotalTasks == 0)
        {
            TaskProgressSummary = "검사 TASK 대기 중";
            TaskProgressFraction = 0;
            return;
        }
        var fail = p.FailedTasks > 0 ? $" · 실패 {p.FailedTasks}" : "";
        TaskProgressSummary = $"검사 {p.CompletedTasks} / {p.TotalTasks} ({p.Percent:0.#}%){fail}";
        TaskProgressFraction = p.Fraction;
    }

    /// <summary>
    /// Missions(층=미션 1:1)에서 층 진행 레일을 파생한다. Fraction은 그 층 실행 큐(WorkItems)의
    /// 종결 비율(DONE+SKIPPED / 전체)로 계산하고, 큐가 비어 있으면 미션 State 기반 coarse 값 폴백.
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

            var floorItems = WorkItems.Where(w => w.MapId == m.MapId).ToList();
            double frac = floorItems.Count > 0
                ? floorItems.Count(w => w.Status is "DONE" or "SKIPPED") / (double)floorItems.Count
                : kind switch { "done" => 1.0, "fail" => 1.0, "run" => 0.5, _ => 0.0 };
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
        _ = LoadScenarioAreasAsync();   // 계획 ▸ 시나리오 검사 대상 영역 편집 목록 갱신
    }
    partial void OnSelectedRobotIdChanged(string? value)
    {
        StartRunCommand.NotifyCanExecuteChanged();
        ResumeRunCommand.NotifyCanExecuteChanged();
    }
    partial void OnNewScenarioNameChanged(string value) => CreateScenarioCommand.NotifyCanExecuteChanged();
    partial void OnCurrentRunChanged(ScenarioRunDto? value)
    {
        ReleaseNextCommand.NotifyCanExecuteChanged();
        RefreshCurrentRunCommand.NotifyCanExecuteChanged();
        AbortRunCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>운영 화면 층 진행 레일 항목. Kind = done|run|wait|fail, Fraction = 미니 진행바 채움(0~1).</summary>
public sealed record FloorProgressItem(string Level, string MapId, string State, string Kind, double Fraction, int Seq);

/// <summary>작업 현황 그리드 행 — 실행 큐 항목(정차 1곳=영역 1개) + 소속 용접라인 드릴다운.</summary>
public sealed partial class WorkItemRow : ObservableObject
{
    public WorkItemRow(WorkItemDto w)
    {
        WorkItemId = w.WorkItemId; AreaId = w.AreaId; AreaName = w.AreaName;
        Level = w.Level; MapId = w.MapId; Seq = w.Seq;
        _status = w.Status; _attempts = w.Attempts;
    }

    public Guid WorkItemId { get; }
    public Guid AreaId { get; }
    public string AreaName { get; }
    public int Level { get; }
    public string MapId { get; }
    public int Seq { get; }
    [ObservableProperty] private string _status;
    [ObservableProperty] private int _attempts;

    public ObservableCollection<TaskRow> Tasks { get; } = new();
}

/// <summary>시나리오 검사 대상 영역 행 [부분 검사 계획] — 풀/대상 소속이 곧 선택 상태.</summary>
public sealed record ScenarioAreaPick(Guid AreaId, string WallCode, int Level, string Name);

/// <summary>용접라인(액션) 행. Status: PLANNED(미배차) | WAITING | RUNNING | FINISHED | FAILED.</summary>
public sealed partial class TaskRow : ObservableObject
{
    public TaskRow(Guid taskId, int seq, string name) { TaskId = taskId; Seq = seq; Name = name; }

    public Guid TaskId { get; }
    public int Seq { get; }
    public string Name { get; }
    [ObservableProperty] private string _status = "PLANNED";
    [ObservableProperty] private string? _detail;   // 예: OK;anchor=FULL;jobRef=…
}
