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
    private readonly string _operatorId;

    public RobotStatusViewModel RobotStatus { get; }
    public MissionViewModel Mission { get; }
    public AlarmsViewModel Alarms { get; }
    public ManualZoneChangeViewModel ManualZoneChange { get; }
    public CalibrationViewModel Calibration { get; }
    public AreaPlanningViewModel AreaPlanning { get; }
    public TankViewModel Tank { get; }

    [ObservableProperty] private string _connectionText = "서버 연결 대기…";

    public ShellViewModel(
        IMonitoringClient monitoring,
        IAcsApiClient api,
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
        _operatorId = options.Value.OperatorId;
        RobotStatus = robotStatus;
        Mission = mission;
        Alarms = alarms;
        ManualZoneChange = manualZoneChange;
        Calibration = calibration;
        AreaPlanning = areaPlanning;
        Tank = tank;

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
            AreaPlanning.LoadAsync());
    }

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
