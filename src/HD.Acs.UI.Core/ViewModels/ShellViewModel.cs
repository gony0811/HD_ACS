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
    public TankViewModel Tank { get; }

    [ObservableProperty] private string _connectionText = "서버 연결 대기…";
    [ObservableProperty] private string _windowTitle = BaseTitle;

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
        TankViewModel tank)
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
        Tank = tank;

        // 2D "영역·작업 관리"에서 등록/삭제 시 3D 도면 오버레이 자동 동기화
        AreaPlanning.PlanningChanged += (_, _) => _ = Tank.LoadOverlaysAsync();

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
            Tank.LoadAsync());   // 3D 셸(지오메트리 10면) 로드
        Mission.TankId = AreaPlanning.TankId;   // 시나리오 생성 대상 선창 동기화
    }

    // ── 파일 메뉴 (프로젝트 파일 .hdacs) ──────────────────────────────
    // 파일 = 현재 선창의 지오메트리·영역·작업 스냅샷. DB가 런타임 truth이며 파일은 내보내기/가져오기.

    /// <summary>새 프로젝트 — 팝업으로 선창/면 등록 → 저장 위치 선택 → 프로젝트 파일 생성.</summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!await _projectDialog.ShowNewProjectAsync()) return;   // 취소/실패 시 파일 미생성
        Mission.TankId = AreaPlanning.TankId;            // 시나리오 생성 대상 선창 동기화
        await Tank.LoadAsync();                          // 새 지오메트리로 3D 셸 갱신
        var path = await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) { UpdateTitle(); return; }    // DB엔 등록됨, 파일만 나중에 저장 가능
        await SaveToAsync(path);
    }

    /// <summary>열기 — 프로젝트 파일을 읽어 DB에 재적재하고 화면을 갱신.</summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = await _projectDialog.PickOpenPathAsync();
        if (path is null) return;
        try
        {
            var doc = await _project.OpenAsync(path);
            AreaPlanning.TankId = doc.TankId;
            Tank.TankId = doc.TankId;
            Mission.TankId = doc.TankId;   // 시나리오 생성 대상 선창 동기화
            await AreaPlanning.LoadAsync();
            await Tank.LoadAsync();   // 열린 지오메트리로 3D 셸 갱신
            UpdateTitle();
            ConnectionText = $"프로젝트 열기: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await _dialog.ShowErrorAsync(DescribeProjectFailure("열기", ex), "오류");
        }
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
        // DB 재적재 단계 — 관제 서버(:5199)/PostgreSQL 연결 실패
        HttpRequestException => $"프로젝트 {verb} 실패: 파일은 정상이지만 관제 서버에 연결하지 못했습니다.\n" +
            "HD.Acs.App(포트 5199)과 PostgreSQL이 실행 중인지 확인한 뒤 다시 시도하세요.\n\n" +
            $"({ex.Message})",
        _ => $"프로젝트 {verb} 실패:\n{ex.Message}",
    };

    /// <summary>저장 — 현재 파일에 덮어쓰기(없으면 다른 이름으로 저장).</summary>
    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        var path = _project.CurrentPath ?? await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) return;
        await SaveToAsync(path);
    }

    /// <summary>다른 이름으로 저장.</summary>
    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var path = await _projectDialog.PickSavePathAsync(AreaPlanning.TankId);
        if (path is null) return;
        await SaveToAsync(path);
    }

    private async Task SaveToAsync(string path)
    {
        try
        {
            await _project.SaveAsync(AreaPlanning.TankId, path);
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

        var confirm = await _dialog.ConfirmAsync(
            $"로봇 '{robot.RobotId}' 비상정지를 실행합니다.\n" +
            "※ 기능적 정지(VDA 5050)이며 안전규격 정지는 로봇측 하드웨어입니다. [ADR-007]\n계속하시겠습니까?",
            "비상정지 확인");
        if (!confirm) return;

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
