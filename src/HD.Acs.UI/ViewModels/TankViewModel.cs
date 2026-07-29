using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 좌측 화물창 뷰(3D + 전개도) 데이터. 벽면 코드/좌표계는 TankLayout(전개도·3D 공유 기준)에서 온다.
/// 벽면별 완료율 집계 API는 아직 없어 로봇 현재 위치만 실데이터로 오버레이한다.
/// </summary>
public sealed partial class TankViewModel : ObservableObject
{
    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<WallCode> Walls { get; } = new(TankLayout.Walls);

    [ObservableProperty] private TankFloor? _selectedFloor;
    [ObservableProperty] private double? _robotX;
    [ObservableProperty] private double? _robotY;
    [ObservableProperty] private string? _robotMapId;

    public bool HasRobotPosition => RobotX is not null && RobotY is not null;

    /// <summary>선택 층에 로봇이 있는지 — 다른 층이면 마커를 흐리게 표시하는 데 사용.</summary>
    public bool RobotOnSelectedFloor =>
        SelectedFloor is not null &&
        string.Equals(RobotMapId, SelectedFloor.MapId, StringComparison.OrdinalIgnoreCase);

    public TankViewModel(IMonitoringClient monitoring)
    {
        SelectedFloor = Floors.FirstOrDefault();
        monitoring.RobotStateReceived += OnRobotState;
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        RobotX = s.ReportedX;
        RobotY = s.ReportedY;
        RobotMapId = s.ReportedMapId;
        OnPropertyChanged(nameof(HasRobotPosition));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
    }

    partial void OnSelectedFloorChanged(TankFloor? value) => OnPropertyChanged(nameof(RobotOnSelectedFloor));
}
