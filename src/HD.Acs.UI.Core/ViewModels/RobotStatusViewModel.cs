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
    private readonly Dictionary<string, (MapCalibrationDto? Calibration, DateTimeOffset LoadedAt)> _calibrations = new();
    private int _positionUpdateVersion;

    public ObservableCollection<RobotDto> Robots { get; } = new();

    [ObservableProperty] private RobotDto? _selectedRobot;
    [ObservableProperty] private double? _batteryPct;
    [ObservableProperty] private string _connectionState = "-";
    [ObservableProperty] private string _position = "-";
    /// <summary>도면 프레임 heading(도, x축 기준 CCW) 표시 문자열. 위치와 동일 T_W_D 규칙(theta − yaw).</summary>
    [ObservableProperty] private string _heading = "-";
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
        if (SelectedRobot is not { } robot) return;
        try
        {
            var ctx = await _api.GetRobotContextAsync(robot.RobotId);
            if (SelectedRobot?.RobotId != robot.RobotId) return;
            if (ctx is null) { ResetLive(); return; }
            BatteryPct = ctx.BatteryPct;
            ConnectionState = ctx.ConnectionState ?? "-";
            MapId = ctx.ReportedMapId ?? "-";
            await UpdateDrawingPositionAsync(
                robot.RobotId, ctx.ReportedMapId, ctx.ReportedX, ctx.ReportedY, ctx.ReportedTheta);
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
        _ = UpdateDrawingPositionAsync(s.RobotId, s.ReportedMapId, s.ReportedX, s.ReportedY, s.ReportedTheta);
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
        Interlocked.Increment(ref _positionUpdateVersion);
        BatteryPct = null; ConnectionState = "-"; Position = "-"; Heading = "-"; MapId = "-"; Driving = false; Errors = 0;
    }

    /// <summary>state의 SLAM 맵 좌표를 현재 map의 T_W_D 역변환으로 도면 좌표화한다.</summary>
    private async Task UpdateDrawingPositionAsync(
        string robotId, string? mapId, double? mapX, double? mapY, double? mapTheta = null)
    {
        int version = Interlocked.Increment(ref _positionUpdateVersion);
        if (mapId is null || mapX is null || mapY is null)
        {
            if (version == _positionUpdateVersion) { Position = "-"; Heading = "-"; }
            return;
        }

        try
        {
            MapCalibrationDto? cal;
            if (_calibrations.TryGetValue(mapId, out var cached)
                && DateTimeOffset.UtcNow - cached.LoadedAt < TimeSpan.FromSeconds(10))
            {
                cal = cached.Calibration;
            }
            else
            {
                cal = await _api.GetCalibrationAsync(mapId);
                _calibrations[mapId] = (cal, DateTimeOffset.UtcNow);
            }

            if (version != _positionUpdateVersion || SelectedRobot?.RobotId != robotId) return;
            if (cal is null)
            {
                Position = "캘리브레이션 없음";
                Heading = "-";
                return;
            }

            // drawing = R(-yaw) * (map - translation)
            double px = mapX.Value - cal.Tx, py = mapY.Value - cal.Ty;
            double cos = Math.Cos(cal.YawRad), sin = Math.Sin(cal.YawRad);
            double drawingX = cos * px + sin * py;
            double drawingY = -sin * px + cos * py;
            Position = FormatPosition(drawingX, drawingY);
            Heading = mapTheta is double th
                ? $"{TankViewModel.MapThetaToDrawing(th, cal.YawRad) * 180.0 / Math.PI:F0}°"
                : "-";
        }
        catch
        {
            if (version == _positionUpdateVersion && SelectedRobot?.RobotId == robotId)
            {
                Position = "좌표 변환 실패";
                Heading = "-";
            }
        }
    }

    private static string FormatPosition(double? x, double? y) =>
        x is null || y is null ? "-" : $"({x:F2}, {y:F2})";
}
