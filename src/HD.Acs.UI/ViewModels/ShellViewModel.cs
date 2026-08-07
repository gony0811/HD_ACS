using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ShellViewModel(
        IMonitoringClient monitoring,
        IAcsApiClient api,
        IProjectService project,
        IProjectDialogService projectDialog,
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

        _monitoring.StatusChanged += (_, status) => ConnectionText = ToText(status);
        RobotStatus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotStatusViewModel.SelectedRobot))
                EmergencyStopCommand.NotifyCanExecuteChanged();
        };
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
            Tank.LoadAsync());   // 3D 셸(지오메트리 10면) 로드
    }

    // ── 파일 메뉴 (프로젝트 파일 .hdacs) ──────────────────────────────
    // 파일 = 현재 선창의 지오메트리·영역·작업 스냅샷. DB가 런타임 truth이며 파일은 내보내기/가져오기.

    /// <summary>새 프로젝트 — 팝업으로 선창/면 등록 → 저장 위치 선택 → 프로젝트 파일 생성.</summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!_projectDialog.ShowNewProject()) return;   // 취소/실패 시 파일 미생성
        await Tank.LoadAsync();                          // 새 지오메트리로 3D 셸 갱신
        var path = _projectDialog.PickSavePath(AreaPlanning.TankId);
        if (path is null) { UpdateTitle(); return; }    // DB엔 등록됨, 파일만 나중에 저장 가능
        await SaveToAsync(path);
    }

    /// <summary>열기 — 프로젝트 파일을 읽어 DB에 재적재하고 화면을 갱신.</summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = _projectDialog.PickOpenPath();
        if (path is null) return;
        try
        {
            var doc = await _project.OpenAsync(path);
            AreaPlanning.TankId = doc.TankId;
            Tank.TankId = doc.TankId;
            await AreaPlanning.LoadAsync();
            await Tank.LoadAsync();   // 열린 지오메트리로 3D 셸 갱신
            UpdateTitle();
            ConnectionText = $"프로젝트 열기: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"프로젝트 열기 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>저장 — 현재 파일에 덮어쓰기(없으면 다른 이름으로 저장).</summary>
    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        var path = _project.CurrentPath ?? _projectDialog.PickSavePath(AreaPlanning.TankId);
        if (path is null) return;
        await SaveToAsync(path);
    }

    /// <summary>다른 이름으로 저장.</summary>
    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var path = _projectDialog.PickSavePath(AreaPlanning.TankId);
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
            MessageBox.Show($"프로젝트 저장 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var confirm = MessageBox.Show(
            $"로봇 '{robot.RobotId}' 비상정지를 실행합니다.\n" +
            "※ 기능적 정지(VDA 5050)이며 안전규격 정지는 로봇측 하드웨어입니다. [ADR-007]\n계속하시겠습니까?",
            "비상정지 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _api.EmergencyStopAsync(robot.RobotId, _operatorId);
            ConnectionText = $"비상정지 명령 전송: {robot.RobotId}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"비상정지 전송 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
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
