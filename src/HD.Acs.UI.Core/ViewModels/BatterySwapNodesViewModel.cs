using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 계획 ▸ 배터리 교체 장소 탭 뷰모델 [HD_AMR 배터리관리 §6].
///
/// 한 mapId당 1곳 정책(서버가 409 가드). 운영자는 좌측에서 층을 고르고 이름·좌표를 입력해 등록,
/// 우측 그리드에서 선택해 삭제한다. 캘리브레이션 이후의 **도면 좌표(m)**가 아니라 **맵 좌표(m)**를 받는다 —
/// 교체 Order 발행 시 로봇에 그대로 전달하는 좌표이므로 캘리브레이션 보정이 불필요한 쪽.
///
/// 좌표 체계 혼동 방지: 다른 UI(계획 등록·수동 goto)는 도면 좌표를 쓰지만 ref.node 자체는 맵 프레임이다
/// (ref.edge·VDA 5050 nodePosition과 같은 공간). 운영자가 캘리브레이션 상태에서 로봇을 교체 장소에
/// 수동 이동시킨 뒤 그 위치의 ReportedX/Y를 받아 등록하는 운영 흐름이 자연스럽다 — 이번 MVP는 수치 입력만.
/// </summary>
public sealed partial class BatterySwapNodesViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<BatterySwapNodeDto> Nodes { get; } = new();

    [ObservableProperty] private TankFloor? _selectedFloor;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double? _theta;
    [ObservableProperty] private BatterySwapNodeDto? _selectedNode;
    [ObservableProperty] private string? _statusMessage;

    public BatterySwapNodesViewModel(IAcsApiClient api)
    {
        _api = api;
        SelectedFloor = Floors.FirstOrDefault();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Nodes.Clear();
            foreach (var n in await _api.GetBatterySwapNodesAsync())
                Nodes.Add(n);
            StatusMessage = Nodes.Count == 0
                ? "등록된 교체 장소가 없습니다. 층을 고르고 좌표를 입력해 등록하세요."
                : $"교체 장소 {Nodes.Count}곳 (층별 1곳).";
        }
        catch (Exception ex) { StatusMessage = $"조회 실패: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        if (SelectedFloor is not { } floor)
        { StatusMessage = "층을 선택하세요."; return; }
        if (string.IsNullOrWhiteSpace(Name))
        { StatusMessage = "이름을 입력하세요."; return; }

        try
        {
            await _api.CreateBatterySwapNodeAsync(floor.MapId, Name.Trim(), X, Y, Theta, "operator");
            Name = ""; Theta = null;
            await LoadAsync();
            StatusMessage = $"{floor.MapId} 교체 장소 등록 완료.";
        }
        catch (Exception ex) { StatusMessage = $"등록 실패: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (SelectedNode is not { } node) { StatusMessage = "삭제할 교체 장소를 선택하세요."; return; }
        try
        {
            await _api.DeleteBatterySwapNodeAsync(node.NodeId, "operator");
            await LoadAsync();
            StatusMessage = $"{node.MapId} 교체 장소 삭제 완료.";
        }
        catch (Exception ex) { StatusMessage = $"삭제 실패: {ex.Message}"; }
    }
}
