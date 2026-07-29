using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>로봇 목록 + 선택 로봇의 실시간 상태(배터리/위치/연결/주행). RobotState·RobotConnection 푸시로 갱신.</summary>
public sealed partial class RobotStatusViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<RobotDto> Robots { get; } = new();

    [ObservableProperty] private RobotDto? _selectedRobot;
    [ObservableProperty] private double? _batteryPct;
    [ObservableProperty] private string _connectionState = "-";
    [ObservableProperty] private string _position = "-";
    [ObservableProperty] private string _mapId = "-";
    [ObservableProperty] private bool _driving;
    [ObservableProperty] private int _errors;
    [ObservableProperty] private string? _statusMessage;

    public RobotStatusViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.RobotStateReceived += OnRobotState;
        monitoring.RobotConnectionReceived += OnRobotConnection;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Robots.Clear();
            foreach (var r in await _api.GetRobotsAsync())
                Robots.Add(r);
            SelectedRobot ??= Robots.FirstOrDefault();
            StatusMessage = Robots.Count == 0 ? "등록된 로봇이 없습니다." : null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"로봇 목록 조회 실패: {ex.Message}";
        }
    }

    partial void OnSelectedRobotChanged(RobotDto? value) => _ = LoadContextAsync();

    private async Task LoadContextAsync()
    {
        if (SelectedRobot is null) return;
        try
        {
            var ctx = await _api.GetRobotContextAsync(SelectedRobot.RobotId);
            if (ctx is null) { ResetLive(); return; }
            BatteryPct = ctx.BatteryPct;
            ConnectionState = ctx.ConnectionState ?? "-";
            MapId = ctx.ReportedMapId ?? "-";
            Position = FormatPosition(ctx.ReportedX, ctx.ReportedY);
        }
        catch (Exception ex)
        {
            StatusMessage = $"로봇 상태 조회 실패: {ex.Message}";
        }
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        if (s.RobotId != SelectedRobot?.RobotId) return;
        BatteryPct = s.BatteryPct;
        MapId = s.ReportedMapId ?? "-";
        Position = FormatPosition(s.ReportedX, s.ReportedY);
        Driving = s.Driving;
        Errors = s.Errors;
    }

    private void OnRobotConnection(object? sender, RobotConnectionDto c)
    {
        if (c.RobotId != SelectedRobot?.RobotId) return;
        ConnectionState = c.ConnectionState;
    }

    private void ResetLive()
    {
        BatteryPct = null; ConnectionState = "-"; Position = "-"; MapId = "-"; Driving = false; Errors = 0;
    }

    private static string FormatPosition(double? x, double? y) =>
        x is null || y is null ? "-" : $"({x:F2}, {y:F2})";
}
