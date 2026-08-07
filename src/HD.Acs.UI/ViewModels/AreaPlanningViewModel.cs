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
/// <summary>층 필터 선택 항목 [SPEC v3.1 §9]. Level=1-based, Label="L1".</summary>
public sealed record LevelOption(int Level, string Label);

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
    public ObservableCollection<LevelOption> Levels { get; } = new();          // 층 필터 목록(level_z 기반)
    public ObservableCollection<AreaDto> Areas { get; } = new();
    public ObservableCollection<AreaTaskDto> AreaTasks { get; } = new();
    public ObservableCollection<AreaBox> AreaBoxes { get; } = new();
    public ObservableCollection<AreaBox> DraftAreas { get; } = new();          // 입력 중 영역 미리보기(점선)
    public ObservableCollection<AreaBox> ReachBands { get; } = new();          // 선택 층 도달 v구간 하이라이트
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
    [ObservableProperty] private string _reachZMinText = "";      // 선택 — 코봇 도달 밴드 하한
    [ObservableProperty] private string _reachZMaxText = "";      // 선택 — 코봇 도달 밴드 상한
    [ObservableProperty] private double _originOx;
    [ObservableProperty] private double _originOy;
    [ObservableProperty] private string _derivedText = "-";

    // ── 층 필터 (v3.1 §9 — 선택 층에서 도달 가능한 면만 노출) ──
    [ObservableProperty] private LevelOption? _selectedLevel;

    // ── 영역 등록 입력 (면 로컬 u,v). level은 서버가 유도 → 입력 없음 ──
    [ObservableProperty] private WallDto? _selectedWall;
    [ObservableProperty] private AreaDto? _selectedArea;
    [ObservableProperty] private string _areaName = "A01";
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
            await LoadWallsAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"조회 실패: {ex.Message}"; }
    }

    /// <summary>선택 층 필터로 면 목록 재적재 [v3.1 §8/§9]. 층 미선택 시 전체 면.</summary>
    private async Task LoadWallsAsync()
    {
        var list = await _api.GetWallsAsync(TankId, SelectedLevel?.Level);
        Walls.Clear();
        foreach (var w in list) Walls.Add(w);
        // 면 재생성/필터 후 인스턴스가 바뀌므로 코드로 재바인딩(콤보 stale 방지)
        SelectedWall = Walls.FirstOrDefault(x => x.WallCode == SelectedWall?.WallCode) ?? Walls.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RegisterGeometryAsync() => await TryRegisterGeometryAsync();

    /// <summary>선창 파라미터 등록(면 자동생성) 후 재로드. 성공 여부 반환 — 새 프로젝트 팝업이 결과를 사용.</summary>
    public async Task<bool> TryRegisterGeometryAsync()
    {
        double[] levelZ;
        try { levelZ = ParseLevelZ(LevelZText); }
        catch { StatusMessage = "level_z 형식 오류 — 쉼표로 구분된 숫자 목록 (예: 0, 3.2, 6.4)."; return false; }
        double? reachMin = ParseOptional(ReachZMinText), reachMax = ParseOptional(ReachZMaxText);
        try
        {
            int n = await _api.RegisterTankGeometryAsync(TankId, LengthL, WFloor, ThetaLowDeg, HLow,
                HWall, ThetaUpDeg, HUp, levelZ, OriginOx, OriginOy, _operatorId, reachMin, reachMax);
            StatusMessage = $"선창 파라미터 등록: {TankId} → {n}면 자동생성";
            await LoadAsync();
            return true;
        }
        catch (Exception ex) { StatusMessage = $"등록 실패: {ex.Message}"; return false; }
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
            var (_, level) = await _api.CreateAreaAsync(TankId, w.WallCode, AreaName, UMin, VMin, UMax, VMax,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                _operatorId);
            StatusMessage = $"영역 등록: {w.WallCode}/{AreaName} u[{UMin},{UMax}] v[{VMin},{VMax}] → 유도 층 L{level}";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }   // 면범위·층유도 400·중복 409
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
        // 조회 먼저 → Clear+Add 원자적(await 사이 없음): 동시 호출이 겹쳐도 중복 방지
        var list = SelectedWall is { } w
            ? await _api.GetAreasAsync(TankId, w.WallCode)
            : (IReadOnlyList<AreaDto>)Array.Empty<AreaDto>();
        Areas.Clear();
        foreach (var a in list) Areas.Add(a);
        await LoadTasksAndProjectAsync();
    }

    private async Task LoadTasksAndProjectAsync()
    {
        var list = SelectedArea is { } a
            ? await _api.GetAreaTasksAsync(a.AreaId)
            : (IReadOnlyList<AreaTaskDto>)Array.Empty<AreaTaskDto>();
        AreaTasks.Clear();
        foreach (var t in list) AreaTasks.Add(t);
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
        AreaBoxes.Clear(); DraftAreas.Clear(); ReachBands.Clear(); TaskSegments.Clear(); StationMarkers.Clear(); DraftSegments.Clear();
        if (SelectedWall is not { } w || w.ULen <= 0 || w.VLen <= 0) return;

        double scale = CanvasScale(w);
        (double x, double y) Proj(double u, double v) => (Margin + u * scale, Margin + (w.VLen - v) * scale);
        AreaBox Box(double uMin, double vMin, double uMax, double vMax, string label)
        {
            var (lx, ty) = Proj(uMin, vMax);   // 좌상단(v_max=위)
            var (rx, by) = Proj(uMax, vMin);
            return new AreaBox(lx, ty, Math.Abs(rx - lx), Math.Abs(by - ty), label);
        }

        // 선택 층 도달 v구간 하이라이트(면 전 폭 × [vLo,vHi]) [v3.1 §9]
        if (w.ReachableVBand is { Length: 2 } vb && SelectedLevel is { } lvl)
            ReachBands.Add(Box(0, vb[0], w.ULen, vb[1], $"{lvl.Label} 도달"));

        foreach (var a in Areas)
        {
            AreaBoxes.Add(Box(a.UMin, a.VMin, a.UMax, a.VMax, a.Name));
            var (sx, sy) = Proj((a.UMin + a.UMax) / 2, (a.VMin + a.VMax) / 2);   // 정차점 = 영역 중심
            StationMarkers.Add(new StationMarker(sx - 7, sy - 7, a.Name));
        }
        foreach (var t in AreaTasks)
        {
            var (x1, y1) = Proj(t.StartU, t.StartV);
            var (x2, y2) = Proj(t.EndU, t.EndV);
            TaskSegments.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
        }
        // 입력 중 영역 미리보기(현재 u/v min·max) — 유효 사각형일 때
        if (UMin < UMax && VMin < VMax)
            DraftAreas.Add(Box(UMin, VMin, UMax, VMax, AreaName));
        // 입력 중 용접선 미리보기(현재 시작/끝)
        if (SelectedArea is not null)
        {
            var (px1, py1) = Proj(StartU, StartV);
            var (px2, py2) = Proj(EndU, EndV);
            DraftSegments.Add(new TaskSeg(px1, py1, px2, py2, px2 - 4, py2 - 4, (px1 + px2) / 2, (py1 + py2) / 2, "new"));
        }
    }

    partial void OnUMinChanged(double value) => Project();
    partial void OnVMinChanged(double value) => Project();
    partial void OnUMaxChanged(double value) => Project();
    partial void OnVMaxChanged(double value) => Project();
    partial void OnAreaNameChanged(string value) => Project();
    partial void OnStartUChanged(double value) => Project();
    partial void OnStartVChanged(double value) => Project();
    partial void OnEndUChanged(double value) => Project();
    partial void OnEndVChanged(double value) => Project();

    private void ApplyGeometry(TankGeometryDto g)
    {
        LengthL = g.LengthL; WFloor = g.WFloor; ThetaLowDeg = g.ThetaLowDeg; HLow = g.HLow;
        HWall = g.HWall; ThetaUpDeg = g.ThetaUpDeg; HUp = g.HUp;
        OriginOx = g.OriginOx; OriginOy = g.OriginOy;
        ReachZMinText = g.ReachZMin?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        ReachZMaxText = g.ReachZMax?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        if (g.LevelZ is { Length: > 0 })
        {
            LevelZText = string.Join(", ", g.LevelZ.Select(z => z.ToString("0.###", CultureInfo.InvariantCulture)));
            // 층 필터 목록 = level_z 길이만큼 L1..LN (기존 선택 층 유지)
            int keep = SelectedLevel?.Level ?? 1;
            Levels.Clear();
            for (int i = 1; i <= g.LevelZ.Length; i++) Levels.Add(new LevelOption(i, $"L{i}"));
            SelectedLevel = Levels.FirstOrDefault(x => x.Level == keep) ?? Levels.FirstOrDefault();
        }
        var d = g.Derived;
        DerivedText = $"B(전폭)={d.B:0.###}  W_ceil(천장폭)={d.WCeil:0.###}  H(전체높이)={d.H:0.###}";
    }

    partial void OnSelectedLevelChanged(LevelOption? value) => _ = ReloadWallsForLevelAsync();

    /// <summary>층 필터 변경 시 면 목록 재적재 후 전개도 갱신 [v3.1 §9].</summary>
    private async Task ReloadWallsForLevelAsync()
    {
        try { await LoadWallsAsync(); await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"면 조회 실패: {ex.Message}"; }
    }

    private static double[] ParseLevelZ(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>빈 문자열 → null, 아니면 파싱(실패 시 null). reach_z 선택 입력용.</summary>
    private static double? ParseOptional(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
