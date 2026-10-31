using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Abstractions;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 셸 최상위 VM — 자식 패널 VM을 보유·조율하고, 서버 연결 상태와 비상정지를 담당.
/// 비상정지는 기능적 정지(안전규격 아님, ADR-007)이며 대상은 로봇 상태 패널에서 선택된 로봇.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IMonitoringClient _monitoring;
    private readonly IAcsApiClient _api;
    private readonly IProjectService _project;
    private readonly IProjectDialogService _projectDialog;
    private readonly IDialogService _dialog;
    private readonly string _operatorId;
    private const string BaseTitle = "HD_ACS — LNG 화물창 용접검사로봇 관제";

    public RobotStatusViewModel RobotStatus { get; }
    public MissionViewModel Mission { get; }
    public AlarmsViewModel Alarms { get; }
    public ManualZoneChangeViewModel ManualZoneChange { get; }
    public CalibrationViewModel Calibration { get; }
    public AreaPlanningViewModel AreaPlanning { get; }
    public BatterySwapNodesViewModel BatterySwapNodes { get; }
    public TankViewModel Tank { get; }
    public HistoryViewModel History { get; }
    public ReachSimViewModel ReachSim { get; }

    [ObservableProperty] private string _connectionText = "서버 연결 대기…";
    [ObservableProperty] private string _windowTitle = BaseTitle;

    /// <summary>
    /// 프로젝트 열기/저장/새 프로젝트 진행 중. 이 동안 파일 메뉴 4개 명령을 모두 막는다 —
    /// 열기는 DB를 지우고 수천 건을 다시 등록하므로, 그 사이 저장하면 반쯤 적재된 상태가 파일(심지어 열어 둔 원본)에 덮어써진다.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewProjectCommand), nameof(OpenProjectCommand),
        nameof(SaveProjectCommand), nameof(SaveProjectAsCommand))]
    private bool _isProjectBusy;

    /// <summary>진행 표시 문구(예: "열기 중 · 영역·작업 등록 1,234/8,789 (14%)"). 바쁘지 않으면 빈 문자열.</summary>
    [ObservableProperty] private string _projectProgressText = "";
    /// <summary>진행률 0~100.</summary>
    [ObservableProperty] private double _projectProgressPercent;
    /// <summary>건수가 없는 단계(선창 등록·파일 기록 등) — 진행바를 불확정으로 표시.</summary>
    [ObservableProperty] private bool _projectProgressIndeterminate = true;

    /// <summary>현재 작업 모드(운영/계획/이력). 상단 모드 탭이 양방향 바인딩한다. 기본=운영.</summary>
    [ObservableProperty] private AppMode _currentMode = AppMode.Operation;

    public ShellViewModel(
        IMonitoringClient monitoring,
        IAcsApiClient api,
        IProjectService project,
        IProjectDialogService projectDialog,
        IDialogService dialog,
        IOptions<AcsOptions> options,
        RobotStatusViewModel robotStatus,
        MissionViewModel mission,
        AlarmsViewModel alarms,
        ManualZoneChangeViewModel manualZoneChange,
        CalibrationViewModel calibration,
        AreaPlanningViewModel areaPlanning,
        BatterySwapNodesViewModel batterySwapNodes,
        TankViewModel tank,
        HistoryViewModel history,
        ReachSimViewModel reachSim)
    {
        _monitoring = monitoring;
        _api = api;
        _project = project;
        _projectDialog = projectDialog;
        _dialog = dialog;
        _operatorId = options.Value.OperatorId;
        RobotStatus = robotStatus;
        Mission = mission;
        Alarms = alarms;
        ManualZoneChange = manualZoneChange;
        Calibration = calibration;
        AreaPlanning = areaPlanning;
        BatterySwapNodes = batterySwapNodes;
        Tank = tank;
        History = history;
        ReachSim = reachSim;

        // 재캘리브레이션(계산) 직후 표시 캐시를 즉시 버린다 — TTL(10초)을 기다리면
        // 운영자가 "고쳤는데 화면이 그대로"로 오해한다.
        Calibration.TransformChanged += (_, _) =>
        {
            Tank.InvalidateCalibrationCache();      // 3D·전개도 마커를 새 T_W_D로 즉시 재계산
            RobotStatus.InvalidateCalibrations();   // 위치 카드도 함께
        };

        // 2D "영역·작업 관리"에서 등록/삭제 시 3D 도면 오버레이 자동 동기화
        AreaPlanning.PlanningChanged += (_, _) => _ = Tank.LoadOverlaysAsync();
        // 영역 수정 → 로봇 상태 카드의 "계획 정차점까지 거리" 즉시 재계산
        AreaPlanning.PlanningChanged += (_, _) => RobotStatus.InvalidatePlannedStations();

        // 실행 큐(work_item)·용접라인(액션) 상태 변화 → TankView 영역/용접선 상태색 갱신 (운영 진행 지도)
        Mission.WorkItemsChanged += (_, _) =>
            Tank.ApplyWorkItemStatuses(
                Mission.WorkItems.ToDictionary(w => w.AreaId, w => w.Status),
                Mission.WorkItems.SelectMany(w => w.Tasks).ToDictionary(t => t.TaskId, t => t.Status));

        _monitoring.StatusChanged += (_, status) => ConnectionText = ToText(status);
        RobotStatus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotStatusViewModel.SelectedRobot))
                EmergencyStopCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>모드 탭 전환. 탭 버튼이 CommandParameter로 AppMode를 전달한다(토글 해제 방지).</summary>
    [RelayCommand]
    private void SetMode(AppMode mode) => CurrentMode = mode;

    partial void OnCurrentModeChanged(AppMode value)
    {
        if (value == AppMode.History) _ = History.LoadAsync();
    }

    /// <summary>앱 시작 시 호출 — 실시간 연결 개시 + 각 패널 초기 로드. 서버 미기동이어도 UI는 유지된다.</summary>
    public async Task InitializeAsync()
    {
        ConnectionText = ToText(_monitoring.Status);
        await _monitoring.StartAsync();
        await Task.WhenAll(
            RobotStatus.LoadAsync(),
            Mission.LoadAsync(),
            ManualZoneChange.LoadAsync(),
            Calibration.LoadAsync(),
            AreaPlanning.LoadAsync(),
            BatterySwapNodes.LoadAsync(),
            Tank.LoadAsync());   // 3D 셸(지오메트리 10면) 로드
        Mission.TankId = AreaPlanning.TankId;   // 시나리오 생성 대상 선창 동기화
    }

    // ── 파일 메뉴 (프로젝트 파일 .hdacs) ──────────────────────────────
    // 파일 = 현재 선창의 지오메트리·영역·작업 스냅샷. DB가 런타임 truth이며 파일은 내보내기/가져오기.

    /// <summary>새 프로젝트 — 팝업으로 선창/면 등록 → 저장 위치 선택 → 프로젝트 파일 생성.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectCommand))]
    private async Task NewProjectAsync()
    {
        if (IsProjectBusy) return;   // CanExecute를 거치지 않는 직접 호출 방어
        if (!await _projectDialog.ShowNewProjectAsync()) return;   // 취소/실패 시 파일 미생성
        Mission.TankId = AreaPlanning.TankId;            // 시나리오 생성 대상 선창 동기화
        await Tank.LoadAsync();                          // 새 지오메트리로 3D 셸 갱신
        var path = await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) { UpdateTitle(); return; }    // DB엔 등록됨, 파일만 나중에 저장 가능
        await SaveToAsync(path);
    }

    /// <summary>열기 — 프로젝트 파일을 읽어 DB에 재적재하고 화면을 갱신.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectCommand))]
    private async Task OpenProjectAsync()
    {
        if (IsProjectBusy) return;
        var path = await _projectDialog.PickOpenPathAsync();
        if (path is null) return;
        try
        {
            await RunProjectBusyAsync("열기", async progress =>
            {
                var doc = await _project.OpenAsync(path, progress: progress);
                AreaPlanning.TankId = doc.TankId;
                Tank.TankId = doc.TankId;
                Mission.TankId = doc.TankId;   // 시나리오 생성 대상 선창 동기화
                // 프로젝트 재적재 결과를 모두 다시 조회한다. 특히 Calibration을 갱신하지 않으면
                // 앱 시작 때 로컬 DB에서 읽은 포인트가 파일에서 복원된 것처럼 화면에 남는다.
                progress.Report(new ProjectProgress("화면 갱신", 0, 0));
                await Task.WhenAll(
                    AreaPlanning.LoadAsync(),
                    Tank.LoadAsync(),          // 열린 지오메트리로 3D 셸 갱신
                    Calibration.LoadAsync(),
                    Mission.LoadAsync());
                UpdateTitle();
                var calibrationCount = doc.Calibrations?.Length ?? 0;
                var pointCount = doc.Calibrations?.Sum(c => c.Points.Length) ?? 0;
                ConnectionText = $"프로젝트 열기: {System.IO.Path.GetFileName(path)} · 캘리브레이션 {calibrationCount}개/{pointCount}점 복원";
            });
        }
        catch (Exception ex)
        {
            await _dialog.ShowErrorAsync(DescribeProjectFailure("열기", ex), "오류");
        }
    }

    private bool CanRunProjectCommand() => !IsProjectBusy;

    /// <summary>
    /// 프로젝트 작업을 "바쁨" 상태로 감싸 실행 — 파일 메뉴를 막고 진행 문구·진행률을 갱신한다.
    /// 예외는 그대로 올리되(호출자가 오류 대화상자 표시) 바쁨 상태는 먼저 해제한다.
    /// </summary>
    private async Task RunProjectBusyAsync(string verb, Func<IProgress<ProjectProgress>, Task> work)
    {
        // ProjectService가 UI 스레드 컨텍스트에서 이어지므로 동기 보고로 충분하다
        // (Progress<T>는 비동기 Post라 완료 후에 늦은 보고가 도착해 문구가 되살아날 수 있다).
        var progress = new SyncProgress(p => ShowProjectProgress(verb, p));
        IsProjectBusy = true;
        progress.Report(new ProjectProgress("준비", 0, 0));
        try
        {
            await work(progress);
        }
        finally
        {
            IsProjectBusy = false;
            ProjectProgressText = "";
            ProjectProgressPercent = 0;
            ProjectProgressIndeterminate = true;
        }
    }

    private void ShowProjectProgress(string verb, ProjectProgress p)
    {
        ProjectProgressIndeterminate = p.Percent is null;
        ProjectProgressPercent = p.Percent ?? 0;
        ProjectProgressText = p.Percent is double pct ? $"{verb} 중 · {p} ({pct:F0}%)" : $"{verb} 중 · {p}";
    }

    private sealed class SyncProgress(Action<ProjectProgress> report) : IProgress<ProjectProgress>
    {
        public void Report(ProjectProgress value) => report(value);
    }

    /// <summary>
    /// 프로젝트 열기/저장 실패 원인을 유형별로 구분해 안내 메시지를 만든다.
    /// .hdacs 열기·저장은 파일 I/O에 더해 관제 서버(REST/DB) 재적재를 수반하므로,
    /// "파일이 깨졌나?"와 "서버가 안 떴나?"를 사용자가 구분할 수 있어야 한다.
    /// </summary>
    private string DescribeProjectFailure(string verb, Exception ex) => ex switch
    {
        // 파일 파싱 단계(매직/버전/GZip/JSON) — ProjectService가 InvalidDataException을 던짐
        InvalidDataException => $"프로젝트 {verb} 실패: 이 프로그램의 프로젝트 파일이 아니거나 손상되었습니다.\n\n{ex.Message}",
        // 일괄 가져오기 검증 거부 — 서버가 DB를 건드리기 전에 막았다(전부 또는 전무)
        ProjectRejectedException rej => $"프로젝트 {verb} 실패: 관제 서버가 파일 내용을 거부했습니다.\n" +
            "DB는 변경되지 않았습니다 — 기존 영역·작업이 그대로 남아 있습니다.\n\n" +
            (rej.Reasons.Count > 0
                ? string.Join("\n", rej.Reasons.Take(10).Select(r => "· " + r)) + (rej.Reasons.Count > 10 ? $"\n… 외 {rej.Reasons.Count - 10}건" : "")
                : rej.Message),
        // DB 재적재 단계 — 서버가 응답했지만 내용을 거부(4xx/5xx). 연결 문제가 아니다.
        HttpRequestException { StatusCode: { } code } => $"프로젝트 {verb} 실패: 관제 서버가 파일 내용을 거부했습니다 (HTTP {(int)code}).\n" +
            "서버 연결은 정상입니다. 아래 사유를 고친 파일로 다시 여세요.\n\n" +
            $"사유: {ex.Message}" +
            (verb == "열기" ? "\n\n※ 열기는 선창을 먼저 다시 등록하므로, 이 선창의 기존 영역·작업이 지워졌을 수 있습니다. 정상 프로젝트 파일을 다시 열어 복원하세요." : ""),
        // DB 재적재 단계 — 관제 서버(:5199)/PostgreSQL 연결 실패
        HttpRequestException => $"프로젝트 {verb} 실패: 파일은 정상이지만 관제 서버에 연결하지 못했습니다.\n" +
            "HD.Acs.App(포트 5199)과 PostgreSQL이 실행 중인지 확인한 뒤 다시 시도하세요.\n\n" +
            $"({ex.Message})",
        _ => $"프로젝트 {verb} 실패:\n{ex.Message}",
    };

    /// <summary>저장 — 현재 파일에 덮어쓰기(없으면 다른 이름으로 저장).</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectCommand))]
    private async Task SaveProjectAsync()
    {
        if (IsProjectBusy) return;
        var path = _project.CurrentPath ?? await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) return;
        await SaveToAsync(path);
    }

    /// <summary>다른 이름으로 저장.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectCommand))]
    private async Task SaveProjectAsAsync()
    {
        if (IsProjectBusy) return;
        var path = await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) return;
        await SaveToAsync(path);
    }

    private async Task SaveToAsync(string path)
    {
        try
        {
            await RunProjectBusyAsync("저장", progress => _project.SaveAsync(AreaPlanning.TankId, path, progress: progress));
            UpdateTitle();
            ConnectionText = $"프로젝트 저장: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await _dialog.ShowErrorAsync(DescribeProjectFailure("저장", ex), "오류");
        }
    }

    private void UpdateTitle() =>
        WindowTitle = _project.CurrentPath is { } p ? $"{BaseTitle} — {System.IO.Path.GetFileName(p)}" : BaseTitle;

    private bool CanEmergencyStop() => RobotStatus.SelectedRobot is not null;

    [RelayCommand(CanExecute = nameof(CanEmergencyStop))]
    private async Task EmergencyStopAsync()
    {
        var robot = RobotStatus.SelectedRobot;
        if (robot is null) return;

        // 확인 팝업 없음 — 비상정지는 누르는 즉시 전송되어야 한다(확인 단계가 정지를 지연시킨다).
        // 기능적 정지(VDA 5050 instantActions)이며 안전규격 정지는 로봇측 하드웨어 [ADR-007].
        try
        {
            await _api.EmergencyStopAsync(robot.RobotId, _operatorId);
            ConnectionText = $"비상정지 명령 전송: {robot.RobotId}";
        }
        catch (Exception ex)
        {
            await _dialog.ShowErrorAsync($"비상정지 전송 실패: {ex.Message}", "오류");
        }
    }

    public void NotifyEmergencyStopCanExecute() => EmergencyStopCommand.NotifyCanExecuteChanged();

    private static string ToText(HubStatus s) => s switch
    {
        HubStatus.Connected => "서버 연결됨",
        HubStatus.Connecting => "서버 연결 중…",
        HubStatus.Reconnecting => "재연결 중…",
        HubStatus.Failed => "서버 연결 실패 — HD.Acs.App 실행 확인",
        _ => "서버 연결 끊김",
    };
}
