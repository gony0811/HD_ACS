using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>(u,v) 캔버스 도형 — 좌표는 이미 캔버스 px로 투영됨.</summary>
public sealed record AreaBox(double Left, double Top, double Width, double Height, string Label);
public sealed record TaskSeg(double X1, double Y1, double X2, double Y2, double EndX, double EndY, double MidX, double MidY, string Badge);
public sealed record StationMarker(double Left, double Top, string Label);

/// <summary>
/// 선창 3D 정의(§2/§3) + 영역·검사 작업 (u,v) 등록(§4) [SPEC v3].
/// 파라미터 등록 → 면 자동생성 → 면 선택 → (u,v) 영역/작업 등록.
/// </summary>
public sealed partial class AreaPlanningViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public const double CanvasSize = 600;
    private const double Margin = 28;

    public AreaPlanningViewModel(IAcsApiClient api, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
    }

    public ObservableCollection<WallDto> Walls { get; } = new();
    public ObservableCollection<AreaDto> Areas { get; } = new();
    public ObservableCollection<AreaTaskDto> AreaTasks { get; } = new();
    public ObservableCollection<AreaBox> AreaBoxes { get; } = new();
    public ObservableCollection<TaskSeg> TaskSegments { get; } = new();
    public ObservableCollection<TaskSeg> DraftSegments { get; } = new();       // 입력 중 용접선 미리보기
    public ObservableCollection<StationMarker> StationMarkers { get; } = new(); // 정차점 = 영역 중심

    [ObservableProperty] private string _tankId = "CT1";
    [ObservableProperty] private string? _statusMessage;

    // ── 선창 파라미터 (각도 deg) ──
    [ObservableProperty] private double _lengthL = 30.0;
    [ObservableProperty] private double _wFloor = 10.0;
    [ObservableProperty] private double _thetaLowDeg = 45.0;
    [ObservableProperty] private double _hLow = 3.0;
    [ObservableProperty] private double _hWall = 8.0;
    [ObservableProperty] private double _thetaUpDeg = 45.0;
    [ObservableProperty] private double _hUp = 2.0;
    [ObservableProperty] private string _levelZText = "0, 3.2, 6.4, 9.6";
    [ObservableProperty] private double _originOx;
    [ObservableProperty] private double _originOy;
    [ObservableProperty] private string _derivedText = "-";

    // ── 영역 등록 입력 (면 로컬 u,v) ──
    [ObservableProperty] private WallDto? _selectedWall;
    [ObservableProperty] private AreaDto? _selectedArea;
    [ObservableProperty] private string _areaName = "A01";
    [ObservableProperty] private int _areaLevel = 1;
    [ObservableProperty] private double _uMin;
    [ObservableProperty] private double _vMin;
    [ObservableProperty] private double _uMax = 2.0;
    [ObservableProperty] private double _vMax = 2.0;
    [ObservableProperty] private bool _stationOverride;
    [ObservableProperty] private double _stationX;
    [ObservableProperty] private double _stationY;
    [ObservableProperty] private double _stationTheta;

    // ── 작업 등록 입력 (면 로컬 u,v) ──
    [ObservableProperty] private double _startU;
    [ObservableProperty] private double _startV;
    [ObservableProperty] private double _endU = 1.0;
    [ObservableProperty] private double _endV;

    public string SelectedWallInfo => SelectedWall is { } w
        ? $"면 {w.WallCode} — u∈[0,{w.ULen:0.###}], v∈[0,{w.VLen:0.###}]{(w.FacingYaw is null ? " (바닥/천장: 정차 수동지정 필요)" : "")}"
        : "면 선택";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var g = await _api.GetTankGeometryAsync(TankId);
            if (g is not null) ApplyGeometry(g);
            Walls.Clear();
            foreach (var w in await _api.GetWallsAsync(TankId)) Walls.Add(w);
            SelectedWall ??= Walls.FirstOrDefault();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"조회 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RegisterGeometryAsync()
    {
        double[] levelZ;
        try { levelZ = ParseLevelZ(LevelZText); }
        catch { StatusMessage = "level_z 형식 오류 — 쉼표로 구분된 숫자 목록 (예: 0, 3.2, 6.4)."; return; }
        try
        {
            int n = await _api.RegisterTankGeometryAsync(TankId, LengthL, WFloor, ThetaLowDeg, HLow,
                HWall, ThetaUpDeg, HUp, levelZ, OriginOx, OriginOy, _operatorId);
            StatusMessage = $"선창 파라미터 등록: {TankId} → {n}면 자동생성";
            await LoadAsync();
        }
        catch (Exception ex) { StatusMessage = $"등록 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private bool CanRegisterArea() => SelectedWall is not null;

    [RelayCommand(CanExecute = nameof(CanRegisterArea))]
    private async Task RegisterAreaAsync()
    {
        if (SelectedWall is not { } w) return;
        if (UMin >= UMax || VMin >= VMax) { StatusMessage = "영역 경계: u_min<u_max, v_min<v_max 이어야 합니다."; return; }
        try
        {
            await _api.CreateAreaAsync(TankId, w.WallCode, AreaLevel, AreaName, UMin, VMin, UMax, VMax,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                _operatorId);
            StatusMessage = $"영역 등록: {w.WallCode}/{AreaName} u[{UMin},{UMax}] v[{VMin},{VMax}]";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }   // 면범위 400·중복 409
    }

    [RelayCommand]
    private async Task DeleteAreaAsync(AreaDto? area)
    {
        if (area is null) return;
        try { await _api.DeleteAreaAsync(area.AreaId); StatusMessage = "영역 삭제됨."; await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"영역 삭제 실패: {ex.Message}"; }
    }

    private bool CanRegisterTask() => SelectedArea is not null;

    [RelayCommand(CanExecute = nameof(CanRegisterTask))]
    private async Task RegisterTaskAsync()
    {
        if (SelectedArea is not { } a) return;
        try
        {
            int seq = await _api.CreateAreaTaskAsync(a.AreaId, StartU, StartV, EndU, EndV, "LINE", "DXF-1", "PROF-1", _operatorId);
            StatusMessage = $"작업 등록: seq {seq} ({StartU},{StartV})–({EndU},{EndV})";
            await LoadTasksAndProjectAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 등록 실패: {ex.Message}"; }   // 경계 밖 400
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(AreaTaskDto? task)
    {
        if (task is null) return;
        try { await _api.DeleteAreaTaskAsync(task.TaskId); StatusMessage = "작업 삭제됨."; await LoadTasksAndProjectAsync(); await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"작업 삭제 실패: {ex.Message}"; }
    }

    private async Task RefreshAreasAsync()
    {
        Areas.Clear();
        if (SelectedWall is { } w)
            foreach (var a in await _api.GetAreasAsync(TankId, w.WallCode)) Areas.Add(a);
        await LoadTasksAndProjectAsync();
    }

    private async Task LoadTasksAndProjectAsync()
    {
        AreaTasks.Clear();
        if (SelectedArea is { } a)
            foreach (var t in await _api.GetAreaTasksAsync(a.AreaId)) AreaTasks.Add(t);
        Project();
    }

    partial void OnSelectedWallChanged(WallDto? value)
    {
        RegisterAreaCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedWallInfo));
        _ = RefreshAreasAsync();
    }

    partial void OnSelectedAreaChanged(AreaDto? value)
    {
        RegisterTaskCommand.NotifyCanExecuteChanged();
        _ = LoadTasksAndProjectAsync();
    }

    private double CanvasScale(WallDto w) => Math.Min((CanvasSize - 2 * Margin) / w.ULen, (CanvasSize - 2 * Margin) / w.VLen);

    /// <summary>선택 면 (u,v)를 캔버스에 auto-fit(v 뒤집기) 투영 — 영역 박스 · 정차점(영역 중심) · 작업 선분 · 입력 미리보기.</summary>
    private void Project()
    {
        AreaBoxes.Clear(); TaskSegments.Clear(); StationMarkers.Clear(); DraftSegments.Clear();
        if (SelectedWall is not { } w || w.ULen <= 0 || w.VLen <= 0) return;

        double scale = CanvasScale(w);
        (double x, double y) Proj(double u, double v) => (Margin + u * scale, Margin + (w.VLen - v) * scale);

        foreach (var a in Areas)
        {
            var (lx, ty) = Proj(a.UMin, a.VMax);   // 좌상단(v_max=위)
            var (rx, by) = Proj(a.UMax, a.VMin);
            AreaBoxes.Add(new AreaBox(lx, ty, Math.Abs(rx - lx), Math.Abs(by - ty), a.Name));
            var (sx, sy) = Proj((a.UMin + a.UMax) / 2, (a.VMin + a.VMax) / 2);   // 정차점 = 영역 중심
            StationMarkers.Add(new StationMarker(sx - 7, sy - 7, a.Name));
        }
        foreach (var t in AreaTasks)
        {
            var (x1, y1) = Proj(t.StartU, t.StartV);
            var (x2, y2) = Proj(t.EndU, t.EndV);
            TaskSegments.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
        }
        if (SelectedArea is not null)   // 입력 중 용접선 미리보기(현재 시작/끝)
        {
            var (px1, py1) = Proj(StartU, StartV);
            var (px2, py2) = Proj(EndU, EndV);
            DraftSegments.Add(new TaskSeg(px1, py1, px2, py2, px2 - 4, py2 - 4, (px1 + px2) / 2, (py1 + py2) / 2, "new"));
        }
    }

    partial void OnStartUChanged(double value) => Project();
    partial void OnStartVChanged(double value) => Project();
    partial void OnEndUChanged(double value) => Project();
    partial void OnEndVChanged(double value) => Project();

    private void ApplyGeometry(TankGeometryDto g)
    {
        LengthL = g.LengthL; WFloor = g.WFloor; ThetaLowDeg = g.ThetaLowDeg; HLow = g.HLow;
        HWall = g.HWall; ThetaUpDeg = g.ThetaUpDeg; HUp = g.HUp;
        OriginOx = g.OriginOx; OriginOy = g.OriginOy;
        if (g.LevelZ is { Length: > 0 })
            LevelZText = string.Join(", ", g.LevelZ.Select(z => z.ToString("0.###", CultureInfo.InvariantCulture)));
        var d = g.Derived;
        DerivedText = $"B(전폭)={d.B:0.###}  W_ceil(천장폭)={d.WCeil:0.###}  H(전체높이)={d.H:0.###}";
    }

    private static double[] ParseLevelZ(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
}
