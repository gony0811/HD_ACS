using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 작업자 수동 층(존) 변경 [Q9] — Operator 권한 필요(감사 로그 기록).
/// 수동 지정 층 vs 로봇 보고 층을 나란히 표시하고 불일치 시 경고한다.
/// 릴리스 게이트: mission.map_id == robot_context.reported_map_id 일 때만 다음 층 미션 릴리스 허용.
/// </summary>
public sealed partial class ManualZoneChangeViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public ObservableCollection<string> RobotIds { get; } = new();
    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);

    [ObservableProperty] private string? _selectedRobotId;
    [ObservableProperty] private TankFloor? _selectedFloor;
    [ObservableProperty] private RobotContextDto? _context;

    [ObservableProperty] private string? _statusMessage;

    public ManualZoneChangeViewModel(IAcsApiClient api, IMonitoringClient monitoring, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
        monitoring.RobotStateReceived += OnRobotState;
        monitoring.RobotConnectionReceived += OnRobotConnection;
    }

    public string ManualMapId => Context?.ManualMapId ?? "-";
    public string ReportedMapId => Context?.ReportedMapId ?? "-";
    public string ManualUpdatedBy => Context?.ManualUpdatedBy ?? "-";

    /// <summary>수동 지정과 로봇 보고 층 불일치 — 릴리스 게이트가 잠긴 상태.</summary>
    public bool IsMismatch =>
        !string.IsNullOrEmpty(Context?.ManualMapId) &&
        !string.IsNullOrEmpty(Context?.ReportedMapId) &&
        !string.Equals(Context!.ManualMapId, Context!.ReportedMapId, StringComparison.OrdinalIgnoreCase);

    public string MismatchWarning => IsMismatch
        ? "⚠ 수동 지정 층과 로봇 보고 층이 다릅니다. 로봇이 해당 층을 보고할 때까지 다음 층 미션이 릴리스되지 않습니다."
        : string.Empty;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            RobotIds.Clear();
            foreach (var r in await _api.GetRobotsAsync())
                RobotIds.Add(r.RobotId);
            SelectedRobotId ??= RobotIds.FirstOrDefault();
            SelectedFloor ??= Floors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"로봇 목록 조회 실패: {ex.Message}";
        }
    }

    partial void OnSelectedRobotIdChanged(string? value) => _ = RefreshContextAsync();

    [RelayCommand]
    private async Task RefreshContextAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRobotId)) return;
        try
        {
            Context = await _api.GetRobotContextAsync(SelectedRobotId);
            RaiseContextDerived();
        }
        catch (Exception ex)
        {
            StatusMessage = $"컨텍스트 조회 실패: {ex.Message}";
        }
    }

    private bool CanApply() => !string.IsNullOrWhiteSpace(SelectedRobotId) && SelectedFloor is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyZoneChangeAsync()
    {
        if (SelectedRobotId is null || SelectedFloor is null) return;
        try
        {
            await _api.ManualZoneChangeAsync(SelectedRobotId, SelectedFloor.MapId, _operatorId);
            StatusMessage = $"수동 층 변경 요청: {SelectedFloor.Level} ({SelectedFloor.MapId}) — 작업자 {_operatorId}";
            await RefreshContextAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"수동 층 변경 실패: {ex.Message}";
        }
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        if (s.RobotId != SelectedRobotId || Context is null) return;
        Context = Context with { ReportedMapId = s.ReportedMapId, ReportedX = s.ReportedX, ReportedY = s.ReportedY };
        RaiseContextDerived();
    }

    private void OnRobotConnection(object? sender, RobotConnectionDto c)
    {
        if (c.RobotId != SelectedRobotId || Context is null) return;
        Context = Context with { ConnectionState = c.ConnectionState };
    }

    partial void OnContextChanged(RobotContextDto? value) => RaiseContextDerived();
    partial void OnSelectedFloorChanged(TankFloor? value) => ApplyZoneChangeCommand.NotifyCanExecuteChanged();

    private void RaiseContextDerived()
    {
        OnPropertyChanged(nameof(ManualMapId));
        OnPropertyChanged(nameof(ReportedMapId));
        OnPropertyChanged(nameof(ManualUpdatedBy));
        OnPropertyChanged(nameof(IsMismatch));
        OnPropertyChanged(nameof(MismatchWarning));
    }
}
