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

    // ── 계획 정차점까지 거리 (정차점 사전 평가) ──
    /// <summary>비교 대상 정차점 또는 비교 불가 사유(예: "F-01 (현재 작업) · 면 F", "이 층에 계획 정차점 없음").</summary>
    [ObservableProperty] private string _stationTarget = "-";
    /// <summary>거리 + 판정(예: "3.53 m · 허용 오차 밖").</summary>
    [ObservableProperty] private string _stationDistance = "";
    /// <summary>벽 따라/벽까지/방향 차 분해.</summary>
    [ObservableProperty] private string _stationDetail = "";
    /// <summary>색 지정: done(허용 오차 이내)·warn(밖)·wait(비교 불가).</summary>
    [ObservableProperty] private string _stationKind = "wait";

    private readonly MissionViewModel? _mission;
    private PlannedStationsDto? _plan;
    private Guid? _planScenarioId;
    private DateTimeOffset _planLoadedAt;
    private bool _planLoading;
    /// <summary>마지막으로 계산한 로봇 도면 위치 — 계획 변경 시 재평가용.</summary>
    private (string RobotId, string MapId, double X, double Y, double? Yaw)? _lastDrawing;
    /// <summary>계획 정차점 재조회 주기 — 영역 수정이 다른 단말에서 일어나도 따라가도록.</summary>
    private static readonly TimeSpan PlanTtl = TimeSpan.FromSeconds(15);

    public RobotStatusViewModel(IAcsApiClient api, IMonitoringClient monitoring, MissionViewModel? mission = null)
    {
        _api = api;
        monitoring.RobotStateReceived += OnRobotState;
        monitoring.RobotConnectionReceived += OnRobotConnection;
        _mission = mission;
        if (mission is not null)
        {
            mission.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MissionViewModel.CurrentRun) or nameof(MissionViewModel.SelectedScenario))
                    _ = RefreshStationAsync();
            };
            mission.WorkItemsChanged += (_, _) => EvaluateStation();   // 현재 작업 영역이 바뀌면 비교 대상 변경
        }
    }

    /// <summary>계획(영역·작업)이 바뀌었음을 알림 — 다음 평가에서 정차점을 다시 조회한다.</summary>
    public void InvalidatePlannedStations()
    {
        _planLoadedAt = DateTimeOffset.MinValue;
        _ = RefreshStationAsync();
    }

    /// <summary>비교 기준 시나리오 = 진행 중 run의 시나리오, 없으면 운영 화면에서 선택한 시나리오.</summary>
    private Guid? PlanScenarioId => _mission?.CurrentRun?.ScenarioId ?? _mission?.SelectedScenario?.ScenarioId;

    private async Task RefreshStationAsync()
    {
        await EnsurePlanAsync();
        EvaluateStation();
    }

    private async Task EnsurePlanAsync()
    {
        var id = PlanScenarioId;
        if (id is null) { _plan = null; _planScenarioId = null; return; }
        if (_planLoading) return;
        if (id == _planScenarioId && DateTimeOffset.UtcNow - _planLoadedAt < PlanTtl) return;
        _planLoading = true;
        try
        {
            var plan = await _api.GetPlannedStationsAsync(id.Value);
            if (PlanScenarioId != id) return;   // 조회 중 시나리오가 바뀜 — 다음 평가에서 다시
            _plan = plan;
            _planScenarioId = id;
            _planLoadedAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            // 조회 실패 — 이전 계획 유지, 다음 state 수신 때 재시도
        }
        finally { _planLoading = false; }
    }

    private void EvaluateStation()
    {
        if (_mission is null) return;
        if (PlanScenarioId is null) { SetStation("시나리오 미선택", "", "", "wait"); return; }
        if (_plan is null) { SetStation("계획 정차점 조회 중", "", "", "wait"); return; }
        if (_lastDrawing is not { } d || d.RobotId != SelectedRobot?.RobotId)
        {
            SetStation("로봇 도면 위치 없음 (캘리브레이션 필요)", "", "", "wait");
            return;
        }

        var currentArea = _mission.WorkItems.FirstOrDefault(w => w.Status == "DISPATCHED")?.AreaId;
        var r = StationDeviation.Evaluate(d.X, d.Y, d.Yaw, d.MapId, _plan, currentArea);
        if (r is null) { SetStation($"이 층({d.MapId})에 계획 정차점 없음", "", "", "wait"); return; }

        SetStation(
            $"{r.Station.AreaName} ({(r.IsCurrentTask ? "현재 작업" : "가장 가까운 계획")}) · 면 {r.Station.WallCode}"
                + (r.Station.Manual ? " · 수동 지정" : ""),
            $"{r.DistanceM:F2} m · {(r.WithinTolerance ? "허용 오차 이내" : "허용 오차 밖")}",
            StationDeviation.Describe(r),
            r.WithinTolerance ? "done" : "warn");
    }

    private void SetStation(string target, string distance, string detail, string kind)
    {
        StationTarget = target; StationDistance = distance; StationDetail = detail; StationKind = kind;
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
        _lastDrawing = null;
        EvaluateStation();
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
                _lastDrawing = null;
                EvaluateStation();
                return;
            }

            // drawing = R(-yaw) * (map - translation)
            double px = mapX.Value - cal.Tx, py = mapY.Value - cal.Ty;
            double cos = Math.Cos(cal.YawRad), sin = Math.Sin(cal.YawRad);
            double drawingX = cos * px + sin * py;
            double drawingY = -sin * px + cos * py;
            Position = FormatPosition(drawingX, drawingY);
            double? drawingYaw = mapTheta is double th ? TankViewModel.MapThetaToDrawing(th, cal.YawRad) : null;
            Heading = drawingYaw is double dyaw ? $"{dyaw * 180.0 / Math.PI:F0}°" : "-";

            _lastDrawing = (robotId, mapId, drawingX, drawingY, drawingYaw);
            await EnsurePlanAsync();
            if (version == _positionUpdateVersion && SelectedRobot?.RobotId == robotId) EvaluateStation();
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
