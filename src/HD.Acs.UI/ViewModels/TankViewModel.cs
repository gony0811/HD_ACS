using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 좌측 화물창 뷰(3D + 전개도) 데이터. 벽면 코드/좌표계는 TankLayout(전개도·3D 공유 기준)에서 온다.
/// 3D 셸은 지오메트리 API(GetWallsAsync)의 실제 10면을 소비하며, 선택 층 도달 밴드를 강조한다.
/// 벽면별 완료율 집계 API는 아직 없어 로봇 현재 위치만 실데이터로 오버레이한다.
/// </summary>
public sealed partial class TankViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<WallCode> Walls { get; } = new(TankLayout.Walls);

    /// <summary>3D 셸 = 지오메트리 API의 실제 10면(도면 3D 프레임).</summary>
    public ObservableCollection<WallDto> ShellWalls { get; } = new();
    /// <summary>선택 층에서 도달 가능한 면 + reachableVBand (층 z-밴드 강조용).</summary>
    public ObservableCollection<WallDto> LevelWalls { get; } = new();

    /// <summary>3D 뷰 모드: "전체" + 층 목록(L1~L4). 전체=개관, L{n}=그 층 슬라이스 격리.</summary>
    public ObservableCollection<string> ViewModes { get; } =
        new(new[] { AllMode }.Concat(TankLayout.Floors.Select(f => f.Level)));

    private const string AllMode = "전체";

    /// <summary>셸/강조를 다시 빌드해야 함(데이터 로드·뷰 모드 변경). 뷰가 구독.</summary>
    public event EventHandler? ViewChanged;

    [ObservableProperty] private string _tankId = TankLayout.DefaultTankId;
    [ObservableProperty] private string _selectedViewMode = AllMode;
    [ObservableProperty] private TankFloor? _selectedFloor;

    /// <summary>선택 뷰 모드의 층 번호(1-based). "전체"면 null.</summary>
    public int? SelectedLevel =>
        SelectedViewMode is { } m && m != AllMode &&
        int.TryParse(m.TrimStart('L', 'l'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv)
            ? lv : null;

    /// <summary>층 슬라이스 격리 모드인지(전체가 아니면 true) — 뷰가 채움 면을 생략하고 슬라이스를 강조.</summary>
    public bool IsolateLevel => SelectedLevel is not null;
    [ObservableProperty] private double? _robotX;
    [ObservableProperty] private double? _robotY;
    [ObservableProperty] private string? _robotMapId;

    public bool HasRobotPosition => RobotX is not null && RobotY is not null;

    /// <summary>선택 뷰 층에 로봇이 있는지 — 다른 층이면 마커를 흐리게. "전체"면 항상 표시(비교 안 함).</summary>
    public bool RobotOnSelectedFloor =>
        SelectedLevel is null ||
        string.Equals(RobotMapId, $"{TankId}-L{SelectedLevel}", StringComparison.OrdinalIgnoreCase);

    public TankViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.RobotStateReceived += OnRobotState;
    }

    /// <summary>선창 파라미터(팔각 치수·유도값). 마구리(F/A) 팔각 윤곽 렌더에 사용.</summary>
    public TankGeometryDto? Geometry { get; private set; }

    /// <summary>선창 면 전체를 불러와 3D 셸을 구성하고, 현재 뷰 모드 강조를 로드한다.</summary>
    public async Task LoadAsync()
    {
        try
        {
            Geometry = await _api.GetTankGeometryAsync(TankId);   // 마구리 팔각 윤곽용 치수
            var walls = await _api.GetWallsAsync(TankId);
            ShellWalls.Clear();
            foreach (var w in walls) ShellWalls.Add(w);
            await LoadLevelWallsAsync();   // ViewChanged 발생(셸+강조 재빌드)
        }
        catch
        {
            // 서버 미기동/면 미생성 — 빈 셸 유지(무해).
            Geometry = null;
            ShellWalls.Clear();
            LevelWalls.Clear();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>선택 뷰 모드가 층(L{n})이면 그 층 도달 면+reachableVBand 로드, "전체"면 clear. 후 ViewChanged.</summary>
    private async Task LoadLevelWallsAsync()
    {
        LevelWalls.Clear();
        if (ShellWalls.Count > 0 && SelectedLevel is int level)
        {
            try
            {
                foreach (var w in await _api.GetWallsAsync(TankId, level)) LevelWalls.Add(w);
            }
            catch { /* 강조 실패는 무해 — 와이어만 표시 */ }
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        RobotX = s.ReportedX;
        RobotY = s.ReportedY;
        RobotMapId = s.ReportedMapId;
        OnPropertyChanged(nameof(HasRobotPosition));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
    }

    partial void OnSelectedViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedLevel));
        OnPropertyChanged(nameof(IsolateLevel));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
        _ = LoadLevelWallsAsync();   // 뷰 모드 변경 시 슬라이스 갱신
    }
}
