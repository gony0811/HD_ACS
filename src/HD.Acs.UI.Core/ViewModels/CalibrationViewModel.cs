using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>도면 픽, 배치 검사, 안정화 자동 측정, solve/잔차 분석을 묶은 캘리브레이션 마법사.</summary>
public sealed partial class CalibrationViewModel : ObservableObject
{
    public const double CanvasSize = 520;
    private const double CanvasPadding = 28;
    private const int StableSampleCount = 15;
    private const double StableRadiusM = 0.02;
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;
    private readonly List<(double X, double Y)> _samples = new();
    private bool _measurementCompleting;
    private double _minX = -15.5, _maxX = 15.5, _minY = -8.65, _maxY = 8.65;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<CalibrationPointDto> Points { get; } = new();
    public ObservableCollection<CalibrationCandidate> Candidates { get; } = new();
    public string[] Units { get; } = { "m", "mm" };

    [ObservableProperty] private TankFloor? _selectedFloor;
    [ObservableProperty] private double _drawingX;
    [ObservableProperty] private double _drawingY;
    [ObservableProperty] private string _unit = "m";
    [ObservableProperty] private double? _mapX;
    [ObservableProperty] private double? _mapY;
    [ObservableProperty] private MapCalibrationDto? _calibration;
    [ObservableProperty] private CalibrationSolveResultDto? _solveResult;
    [ObservableProperty] private string? _robotReportedMapId;
    [ObservableProperty] private double? _robotReportedX;
    [ObservableProperty] private double? _robotReportedY;
    [ObservableProperty] private bool _robotDriving;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private CalibrationCandidate? _selectedCandidate;
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private int _stableSamples;
    [ObservableProperty] private string _geometryLabel = "+X 선수 / +Y 좌현";

    public CalibrationViewModel(IAcsApiClient api, IMonitoringClient monitoring, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
        monitoring.RobotStateReceived += OnRobotState;
        Points.CollectionChanged += (_, _) => SolveCommand.NotifyCanExecuteChanged();
        Candidates.CollectionChanged += (_, _) => RaisePlanningDerived();
    }

    private string? SelectedMapId => SelectedFloor?.MapId;
    public bool RobotReady => SelectedMapId is not null
        && string.Equals(RobotReportedMapId, SelectedMapId, StringComparison.OrdinalIgnoreCase)
        && RobotReportedX is not null && RobotReportedY is not null;
    public string ReadinessText => RobotReady
        ? $"로봇 보고 위치: ({RobotReportedX:F3}, {RobotReportedY:F3}) @ {RobotReportedMapId}" + (RobotDriving ? " · 이동 중" : " · 정지")
        : "선택 층을 보고 중인 로봇이 없습니다. 로봇/시뮬레이터의 보고 층을 확인하세요.";
    public bool HasWarning => SolveResult?.Warning is not null;
    public string WarningText => SolveResult?.Warning ?? string.Empty;
    public bool CanAddCandidate => Candidates.Count < 4 && !IsMeasuring;
    public bool HasCandidates => Candidates.Count > 0;
    public string MeasurementProgress => IsMeasuring
        ? $"위치 안정화 측정 {StableSamples}/{StableSampleCount} · 이동하면 다시 시작합니다."
        : "AMR을 선택 마커에 맞춘 뒤 ‘정렬 완료’를 누르세요.";
    public string CandidateSummary => Candidates.Count == 0
        ? "도면을 클릭해 기준점 3~4개를 지정하세요."
        : $"기준점 {Candidates.Count}/4 · 측정 {Candidates.Count(c => c.IsMeasured)}개";
    public string LayoutQualityText => EvaluateLayout();
    public bool LayoutReady => Candidates.Count >= 3 && IsLayoutWellSpread();
    public string CalibrationSummary => SolveResult is { } s
        ? $"tx={s.Tx:F4} m, ty={s.Ty:F4} m, yaw={s.YawRad:F5} rad, RMS={s.RmsM:F4} m (최대 {s.MaxResidualM:F4}), 점 {s.PointCount}"
        : Calibration is { } c
            ? $"tx={c.Tx:F4} m, ty={c.Ty:F4} m, yaw={c.YawRad:F5} rad, RMS={c.RmsM:F4} m, 점 {c.PointCount} (등록 {c.RegisteredAt:yyyy-MM-dd HH:mm})"
            : "저장된 T_W_D 없음 — 기준점 3~4점을 측정하세요.";

    [RelayCommand]
    public async Task LoadAsync()
    {
        SelectedFloor ??= Floors.FirstOrDefault();
        await LoadGeometryAsync();
        await RefreshAsync();
    }

    private async Task LoadGeometryAsync()
    {
        try
        {
            var g = await _api.GetTankGeometryAsync(TankLayout.DefaultTankId);
            if (g is null) return;
            _minX = g.OriginOx - g.LengthL / 2; _maxX = g.OriginOx + g.LengthL / 2;
            _minY = g.OriginOy - g.WFloor / 2; _maxY = g.OriginOy + g.WFloor / 2;
            GeometryLabel = $"+X 선수 / +Y 좌현 · {g.LengthL:F2} × {g.WFloor:F2} m";
        }
        catch (Exception ex) { StatusMessage = $"도면 범위 조회 실패(기본 범위 사용): {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedMapId is null) return;
        try
        {
            Points.Clear();
            foreach (var p in await _api.GetCalibrationPointsAsync(SelectedMapId)) Points.Add(p);
            Calibration = await _api.GetCalibrationAsync(SelectedMapId);
            SolveResult = null;
            RaiseDerived();
        }
        catch (Exception ex) { StatusMessage = $"캘리브레이션 조회 실패: {ex.Message}"; }
    }

    /// <summary>520px 도면 클릭을 ACS drawing 좌표로 역투영한다.</summary>
    public void CanvasClick(double canvasX, double canvasY)
    {
        if (!CanAddCandidate || canvasX < CanvasPadding || canvasX > CanvasSize - CanvasPadding
            || canvasY < CanvasPadding || canvasY > CanvasSize - CanvasPadding) return;
        var drawing = CalibrationPlanning.CanvasToDrawing(canvasX, canvasY, CanvasSize, CanvasPadding, _minX, _maxX, _minY, _maxY);
        var c = new CalibrationCandidate($"P{Candidates.Count + 1}",
            drawing.X, drawing.Y, canvasX - 9, canvasY - 9);
        Candidates.Add(c);
        SelectCandidate(c);
        StatusMessage = $"{c.Name} 지정: 도면 ({c.DrawingX:F3}, {c.DrawingY:F3})";
    }

    [RelayCommand]
    private void SelectCandidate(CalibrationCandidate? candidate)
    {
        if (candidate is null || IsMeasuring) return;
        foreach (var c in Candidates) c.IsSelected = false;
        SelectedCandidate = candidate; candidate.IsSelected = true;
        DrawingX = candidate.DrawingX; DrawingY = candidate.DrawingY; Unit = "m";
        StatusMessage = candidate.IsMeasured
            ? $"{candidate.Name} 측정 완료. 재측정하려면 대응점을 먼저 삭제하세요."
            : $"{candidate.Name} 현장 마커로 AMR을 이동하고 중심을 맞추세요.";
    }

    [RelayCommand]
    private void RemoveCandidate(CalibrationCandidate? candidate)
    {
        if (candidate is null || candidate.IsMeasured || IsMeasuring) return;
        Candidates.Remove(candidate);
        if (SelectedCandidate == candidate) SelectedCandidate = null;
        for (int i = 0; i < Candidates.Count; i++) Candidates[i].Name = $"P{i + 1}";
        RaisePlanningDerived();
    }

    [RelayCommand]
    private void ClearCandidates()
    {
        if (IsMeasuring || Candidates.Any(c => c.IsMeasured))
        { StatusMessage = "측정된 기준점은 대응쌍 목록에서 먼저 삭제하세요."; return; }
        Candidates.Clear(); SelectedCandidate = null;
        StatusMessage = "기준점 계획을 초기화했습니다.";
    }

    private bool CanStartMeasurement() => RobotReady && !RobotDriving && !IsMeasuring && SelectedCandidate is { IsMeasured: false };
    [RelayCommand(CanExecute = nameof(CanStartMeasurement))]
    private void StartMeasurement()
    {
        _samples.Clear(); StableSamples = 0; IsMeasuring = true;
        StatusMessage = $"{SelectedCandidate!.Name} 안정화 측정 중… AMR을 움직이지 마세요.";
        RaiseDerived();
    }

    [RelayCommand]
    private void CancelMeasurement()
    {
        IsMeasuring = false; _samples.Clear(); StableSamples = 0;
        StatusMessage = "자동 측정을 취소했습니다."; RaiseDerived();
    }

    private bool CanCapture() => RobotReady;
    [RelayCommand(CanExecute = nameof(CanCapture))]
    private void CaptureRobotPosition()
    {
        MapX = RobotReportedX; MapY = RobotReportedY;
        StatusMessage = "AMR 위치를 캡처했습니다. 맵 X/Y 확인 후 기준점 저장을 누르세요.";
    }

    private bool CanSavePoint() => SelectedMapId is not null
        && MapX is double x && double.IsFinite(x) && MapY is double y && double.IsFinite(y)
        && double.IsFinite(DrawingX) && double.IsFinite(DrawingY);
    [RelayCommand(CanExecute = nameof(CanSavePoint))]
    private async Task CapturePointAsync() => await SavePointAsync(SelectedCandidate);

    private async Task SavePointAsync(CalibrationCandidate? candidate)
    {
        if (SelectedMapId is null || MapX is null || MapY is null) return;
        try
        {
            var mapId = SelectedMapId;
            var pt = await _api.CaptureCalibrationPointAsync(mapId, DrawingX, DrawingY, Unit, _operatorId, mapX: MapX, mapY: MapY);
            if (SelectedMapId != mapId) return;
            Points.Add(pt);
            if (candidate is not null)
            {
                candidate.SavedPointId = pt.Id; candidate.MapX = pt.MapX; candidate.MapY = pt.MapY;
                candidate.IsMeasured = true; candidate.ResidualM = null;
            }
            StatusMessage = $"저장 완료: 도면({pt.DrawingXM:F3},{pt.DrawingYM:F3}) ↔ 맵({pt.MapX:F3},{pt.MapY:F3})";
            SelectNextCandidate();
            if (Candidates.Count(c => c.IsMeasured) >= 3) await SolveAsync();
            RaisePlanningDerived();
        }
        catch (Exception ex) { StatusMessage = $"저장 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeletePointAsync(CalibrationPointDto? point)
    {
        if (SelectedMapId is null || point is null) return;
        try
        {
            await _api.DeleteCalibrationPointAsync(SelectedMapId, point.Id); Points.Remove(point);
            var c = Candidates.FirstOrDefault(x => x.SavedPointId == point.Id);
            if (c is not null) { c.SavedPointId = null; c.MapX = null; c.MapY = null; c.IsMeasured = false; c.ResidualM = null; }
            SolveResult = null; StatusMessage = "대응쌍 삭제됨."; RaisePlanningDerived();
        }
        catch (Exception ex) { StatusMessage = $"삭제 실패: {ex.Message}"; }
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
            UpdateResiduals(SolveResult);
            var worst = Candidates.Where(c => c.ResidualM.HasValue).OrderByDescending(c => c.ResidualM).FirstOrDefault();
            StatusMessage = SolveResult.Warning is null
                ? $"계산 완료. {(worst is null ? "" : $"최대 잔차 {worst.Name} {worst.ResidualM:F3}m.")}"
                : $"계산 경고. {worst?.Name} 재측정을 우선 권장합니다.";
            RaiseDerived();
        }
        catch (Exception ex) { StatusMessage = $"계산 실패: {ex.Message}"; }
    }

    private void UpdateResiduals(CalibrationSolveResultDto s)
    {
        double cos = Math.Cos(s.YawRad), sin = Math.Sin(s.YawRad);
        foreach (var c in Candidates.Where(c => c.IsMeasured && c.MapX.HasValue && c.MapY.HasValue))
        {
            double mx = cos * c.DrawingX - sin * c.DrawingY + s.Tx;
            double my = sin * c.DrawingX + cos * c.DrawingY + s.Ty;
            c.ResidualM = Distance(mx, my, c.MapX!.Value, c.MapY!.Value);
        }
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        if (SelectedMapId is null || !string.Equals(s.ReportedMapId, SelectedMapId, StringComparison.OrdinalIgnoreCase)) return;
        RobotReportedMapId = s.ReportedMapId; RobotReportedX = s.ReportedX; RobotReportedY = s.ReportedY; RobotDriving = s.Driving;
        if (IsMeasuring) CollectSample(s);
        RaiseDerived();
    }

    private void CollectSample(RobotStateDto s)
    {
        if (s.Driving || s.ReportedX is not double x || s.ReportedY is not double y)
        { _samples.Clear(); StableSamples = 0; return; }
        _samples.Add((x, y));
        if (_samples.Count > StableSampleCount) _samples.RemoveAt(0);
        StableSamples = _samples.Count;
        if (_samples.Count < StableSampleCount || _measurementCompleting) return;
        double avgX = _samples.Average(p => p.X), avgY = _samples.Average(p => p.Y);
        double maxRadius = _samples.Max(p => Distance(p.X, p.Y, avgX, avgY));
        if (maxRadius > StableRadiusM)
        { _samples.RemoveAt(0); StableSamples = _samples.Count; StatusMessage = $"위치 흔들림 {maxRadius:F3}m — 안정화 대기 중…"; return; }
        _measurementCompleting = true; IsMeasuring = false; MapX = avgX; MapY = avgY;
        _ = CompleteAutomaticMeasurementAsync();
    }

    private async Task CompleteAutomaticMeasurementAsync()
    {
        try { await SavePointAsync(SelectedCandidate); }
        finally { _measurementCompleting = false; _samples.Clear(); StableSamples = 0; RaiseDerived(); }
    }

    private void SelectNextCandidate()
    {
        var pending = Candidates.Where(c => !c.IsMeasured).ToList();
        if (pending.Count == 0) return;
        var measured = Candidates.Where(c => c.IsMeasured).ToList();
        var next = measured.Count == 0 ? pending[0] : pending
            .OrderByDescending(p => measured.Min(m => Distance(p.DrawingX, p.DrawingY, m.DrawingX, m.DrawingY))).First();
        SelectCandidate(next); StatusMessage = $"다음 추천 기준점: {next.Name}. 현장 마커로 이동하세요.";
    }

    private string EvaluateLayout()
    {
        if (Candidates.Count < 3) return "배치 검사: 기준점이 3개 이상 필요합니다.";
        double sx = Candidates.Max(c => c.DrawingX) - Candidates.Min(c => c.DrawingX);
        double sy = Candidates.Max(c => c.DrawingY) - Candidates.Min(c => c.DrawingY);
        return IsLayoutWellSpread()
            ? $"배치 양호: X {sx:F1}m, Y {sy:F1}m에 걸쳐 분산되었습니다."
            : $"배치 경고: X {sx:F1}m, Y {sy:F1}m 분산. 서로 먼 반대편 점을 추가/이동하세요.";
    }

    private bool IsLayoutWellSpread()
    {
        if (Candidates.Count < 3) return false;
        return CalibrationPlanning.IsWellSpread(Candidates.Select(c => (c.DrawingX, c.DrawingY)),
            _minX, _maxX, _minY, _maxY);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2, dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    partial void OnSelectedFloorChanged(TankFloor? value)
    {
        RobotReportedMapId = null; RobotReportedX = null; RobotReportedY = null; MapX = null; MapY = null;
        IsMeasuring = false; _samples.Clear(); Candidates.Clear(); SelectedCandidate = null;
        CaptureRobotPositionCommand.NotifyCanExecuteChanged(); CapturePointCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync();
    }
    partial void OnSelectedCandidateChanged(CalibrationCandidate? value) => StartMeasurementCommand.NotifyCanExecuteChanged();
    partial void OnIsMeasuringChanged(bool value) { OnPropertyChanged(nameof(MeasurementProgress)); RaisePlanningDerived(); }
    partial void OnStableSamplesChanged(int value) => OnPropertyChanged(nameof(MeasurementProgress));
    partial void OnRobotDrivingChanged(bool value) { OnPropertyChanged(nameof(ReadinessText)); StartMeasurementCommand.NotifyCanExecuteChanged(); }
    partial void OnCalibrationChanged(MapCalibrationDto? value) => RaiseDerived();
    partial void OnMapXChanged(double? value) => CapturePointCommand.NotifyCanExecuteChanged();
    partial void OnMapYChanged(double? value) => CapturePointCommand.NotifyCanExecuteChanged();
    partial void OnDrawingXChanged(double value) => CapturePointCommand.NotifyCanExecuteChanged();
    partial void OnDrawingYChanged(double value) => CapturePointCommand.NotifyCanExecuteChanged();
    partial void OnSolveResultChanged(CalibrationSolveResultDto? value) => RaiseDerived();

    private void RaisePlanningDerived()
    {
        OnPropertyChanged(nameof(CanAddCandidate)); OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(CandidateSummary)); OnPropertyChanged(nameof(LayoutQualityText));
        OnPropertyChanged(nameof(LayoutReady)); StartMeasurementCommand.NotifyCanExecuteChanged();
    }
    private void RaiseDerived()
    {
        CaptureRobotPositionCommand.NotifyCanExecuteChanged(); CapturePointCommand.NotifyCanExecuteChanged(); StartMeasurementCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RobotReady)); OnPropertyChanged(nameof(ReadinessText)); OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningText)); OnPropertyChanged(nameof(CalibrationSummary)); OnPropertyChanged(nameof(MeasurementProgress));
    }
}

public static class CalibrationPlanning
{
    public static (double X, double Y) CanvasToDrawing(double x, double y, double size, double padding,
        double minX, double maxX, double minY, double maxY)
    {
        double nx = (x - padding) / (size - 2 * padding);
        double ny = (y - padding) / (size - 2 * padding);
        return (minX + nx * (maxX - minX), maxY - ny * (maxY - minY));
    }

    public static bool IsWellSpread(IEnumerable<(double X, double Y)> points,
        double minX, double maxX, double minY, double maxY)
    {
        var list = points.ToList();
        if (list.Count < 3) return false;
        double spanX = list.Max(p => p.X) - list.Min(p => p.X);
        double spanY = list.Max(p => p.Y) - list.Min(p => p.Y);
        return spanX >= (maxX - minX) * .30 && spanY >= (maxY - minY) * .30;
    }
}

public sealed partial class CalibrationCandidate : ObservableObject
{
    public CalibrationCandidate(string name, double drawingX, double drawingY, double left, double top)
    { _name = name; DrawingX = drawingX; DrawingY = drawingY; Left = left; Top = top; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMeasured;
    [ObservableProperty] private double? _residualM;
    public double DrawingX { get; }
    public double DrawingY { get; }
    public double Left { get; }
    public double Top { get; }
    public Guid? SavedPointId { get; set; }
    public double? MapX { get; set; }
    public double? MapY { get; set; }
    public string CoordinateText => $"({DrawingX:F3}, {DrawingY:F3})";
    public string ResultText => IsMeasured ? "측정 완료" + (ResidualM is double r ? $" · 잔차 {r:F3}m" : "") : "대기";
    partial void OnIsMeasuredChanged(bool value) => OnPropertyChanged(nameof(ResultText));
    partial void OnResidualMChanged(double? value) => OnPropertyChanged(nameof(ResultText));
}
