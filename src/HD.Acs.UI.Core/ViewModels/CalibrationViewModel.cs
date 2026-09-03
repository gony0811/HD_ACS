using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 도면→맵 캘리브레이션(T_W_D) 기준점 캡처 화면 [PHASE2 WP-5a]. WP-1 캘리브레이션 API만 사용.
/// 캡처는 서버가 '입력한 도면 좌표'와 '해당 층을 보고 중인 로봇의 map 좌표'를 대응쌍으로 저장한다.
/// 따라서 캡처 전 로봇(또는 시뮬레이터)이 선택 층(mapId)을 보고 중이어야 한다(아니면 409).
/// </summary>
public sealed partial class CalibrationViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<CalibrationPointDto> Points { get; } = new();
    public string[] Units { get; } = { "m", "mm" };

    [ObservableProperty] private TankFloor? _selectedFloor;
    [ObservableProperty] private double _drawingX;
    [ObservableProperty] private double _drawingY;
    [ObservableProperty] private string _unit = "m";

    [ObservableProperty] private MapCalibrationDto? _calibration;      // 저장된 유효 T_W_D
    [ObservableProperty] private CalibrationSolveResultDto? _solveResult;

    // 선택 층을 보고 중인 로봇의 최신 위치 (캡처 준비 상태 표시)
    [ObservableProperty] private string? _robotReportedMapId;
    [ObservableProperty] private double? _robotReportedX;
    [ObservableProperty] private double? _robotReportedY;

    [ObservableProperty] private string? _statusMessage;

    public CalibrationViewModel(IAcsApiClient api, IMonitoringClient monitoring, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
        monitoring.RobotStateReceived += OnRobotState;
        Points.CollectionChanged += (_, _) => SolveCommand.NotifyCanExecuteChanged();
    }

    private string? SelectedMapId => SelectedFloor?.MapId;

    /// <summary>선택 층을 보고 중인 로봇이 있어 캡처 가능한 상태.</summary>
    public bool RobotReady =>
        SelectedMapId is not null &&
        string.Equals(RobotReportedMapId, SelectedMapId, StringComparison.OrdinalIgnoreCase) &&
        RobotReportedX is not null && RobotReportedY is not null;

    public string ReadinessText => RobotReady
        ? $"로봇 보고 위치: ({RobotReportedX:F3}, {RobotReportedY:F3}) @ {RobotReportedMapId}"
        : "선택 층을 보고 중인 로봇이 없습니다 — 캡처 시 409. 로봇/시뮬레이터가 해당 층을 보고해야 합니다.";

    public bool HasWarning => SolveResult?.Warning is not null;
    public string WarningText => SolveResult?.Warning ?? string.Empty;

    public string CalibrationSummary
    {
        get
        {
            if (SolveResult is { } s)
                return $"tx={s.Tx:F4} m, ty={s.Ty:F4} m, yaw={s.YawRad:F5} rad, RMS={s.RmsM:F4} m (최대 {s.MaxResidualM:F4}), 점 {s.PointCount}";
            if (Calibration is { } c)
                return $"tx={c.Tx:F4} m, ty={c.Ty:F4} m, yaw={c.YawRad:F5} rad, RMS={c.RmsM:F4} m, 점 {c.PointCount} (등록 {c.RegisteredAt:yyyy-MM-dd HH:mm})";
            return "저장된 T_W_D 없음 — 기준점 2~3점 캡처 후 계산하세요.";
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        SelectedFloor ??= Floors.FirstOrDefault();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedMapId is null) return;
        try
        {
            Points.Clear();
            foreach (var p in await _api.GetCalibrationPointsAsync(SelectedMapId))
                Points.Add(p);
            Calibration = await _api.GetCalibrationAsync(SelectedMapId);
            SolveResult = null;
            RaiseDerived();
        }
        catch (Exception ex)
        {
            StatusMessage = $"캘리브레이션 조회 실패: {ex.Message}";
        }
    }

    private bool CanCapture() => SelectedMapId is not null;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CapturePointAsync()
    {
        if (SelectedMapId is null) return;
        try
        {
            var pt = await _api.CaptureCalibrationPointAsync(SelectedMapId, DrawingX, DrawingY, Unit, _operatorId);
            Points.Add(pt);
            StatusMessage = $"캡처: 도면({pt.DrawingXM:F3},{pt.DrawingYM:F3}) ↔ 맵({pt.MapX:F3},{pt.MapY:F3})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"캡처 실패: {ex.Message}";   // 409(로봇 미보고)·404 서버 메시지 노출
        }
    }

    [RelayCommand]
    private async Task DeletePointAsync(CalibrationPointDto? point)
    {
        if (SelectedMapId is null || point is null) return;
        try
        {
            await _api.DeleteCalibrationPointAsync(SelectedMapId, point.Id);
            Points.Remove(point);
            StatusMessage = "대응쌍 삭제됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"삭제 실패: {ex.Message}";
        }
    }

    private bool CanSolve() => Points.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private async Task SolveAsync()
    {
        if (SelectedMapId is null) return;
        try
        {
            SolveResult = await _api.SolveCalibrationAsync(SelectedMapId);
            Calibration = await _api.GetCalibrationAsync(SelectedMapId);
            StatusMessage = SolveResult.Warning is null ? "T_W_D 계산·저장 완료." : "계산 완료 (RMS 경고).";
            RaiseDerived();
        }
        catch (Exception ex)
        {
            StatusMessage = $"계산 실패: {ex.Message}";   // 400(<2점)·404 서버 메시지 노출
        }
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        // 선택 층을 보고 중인 로봇의 위치만 반영
        if (SelectedMapId is null || !string.Equals(s.ReportedMapId, SelectedMapId, StringComparison.OrdinalIgnoreCase))
            return;
        RobotReportedMapId = s.ReportedMapId;
        RobotReportedX = s.ReportedX;
        RobotReportedY = s.ReportedY;
        RaiseDerived();
    }

    partial void OnSelectedFloorChanged(TankFloor? value)
    {
        RobotReportedMapId = null; RobotReportedX = null; RobotReportedY = null;
        CapturePointCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync();
    }

    partial void OnCalibrationChanged(MapCalibrationDto? value) => RaiseDerived();
    partial void OnSolveResultChanged(CalibrationSolveResultDto? value) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(RobotReady));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(CalibrationSummary));
    }
}
