using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>전개도 캔버스 도형 — 좌표는 이미 캔버스 px로 투영됨.</summary>
public sealed record AreaShape(double Left, double Top, double Width, double Height, string Label);
public sealed record StationMarker(double Left, double Top, bool IsOverride);
public sealed record AreaTaskSegment(double X1, double Y1, double X2, double Y2,
    double EndX, double EndY, double MidX, double MidY, string BadgeText);

/// <summary>
/// 영역(Area) LAYER + 수동 검사 작업 [PHASE2 개정, 구 SlicingView 대체].
/// 영역 등록 → 검사 작업 등록 → generate-from-areas → 벽면 전개도에 영역/정차/작업 렌더.
/// </summary>
public sealed partial class AreaPlanningViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public const double CanvasSize = 600;
    private const double Margin = 28;

    public ObservableCollection<ScenarioSummaryDto> Scenarios { get; } = new();
    public ObservableCollection<WallDto> WallDefs { get; } = new();   // 등록된 벽면(법선 소유) [Wall 법선 승격]
    public ObservableCollection<AreaDto> Areas { get; } = new();
    public ObservableCollection<AreaTaskDto> AreaTasks { get; } = new();
    public ObservableCollection<string> Walls { get; } = new();
    public ObservableCollection<AreaShape> AreaShapes { get; } = new();
    public ObservableCollection<StationMarker> StationMarkers { get; } = new();
    public ObservableCollection<AreaTaskSegment> TaskSegments { get; } = new();

    private readonly Dictionary<Guid, List<AreaTaskDto>> _tasksByArea = new();

    [ObservableProperty] private ScenarioSummaryDto? _selectedScenario;
    [ObservableProperty] private AreaDto? _selectedArea;
    [ObservableProperty] private string? _selectedWall;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _newScenarioName = "검사 시나리오";

    // 벽면 등록 입력 [정차각 자동화] — 벽면 레지스트리(코드·설명). 정차각은 seam 기하에서 자동 산출(입력 없음)
    [ObservableProperty] private int _wallLevel = 2;
    [ObservableProperty] private string _wallCodeInput = "SM";
    [ObservableProperty] private string _wallDescription = "";

    // 영역 등록 입력 (법선 없음 — 소속 벽면에서 상속)
    [ObservableProperty] private int _areaLevel = 2;
    [ObservableProperty] private string _areaWall = "SM";
    [ObservableProperty] private string _areaName = "A01";
    [ObservableProperty] private double _minX;
    [ObservableProperty] private double _minY = -0.5;
    [ObservableProperty] private double _maxX = 1.0;
    [ObservableProperty] private double _maxY = 0.5;

    // 검사 작업 등록 입력 (선택 영역)
    [ObservableProperty] private double _startX;
    [ObservableProperty] private double _startY;
    [ObservableProperty] private double _startZ;
    [ObservableProperty] private double _endX = 0.8;
    [ObservableProperty] private double _endY;
    [ObservableProperty] private double _endZ;

    public AreaPlanningViewModel(IAcsApiClient api, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
    }

    private string TankId => SelectedScenario?.TankId ?? "CT1";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Scenarios.Clear();
            foreach (var s in await _api.GetScenariosAsync()) Scenarios.Add(s);
            SelectedScenario ??= Scenarios.FirstOrDefault();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"조회 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CreateScenarioAsync()
    {
        try
        {
            var id = await _api.CreateScenarioAsync(NewScenarioName, TankId);
            await LoadAsync();
            SelectedScenario = Scenarios.FirstOrDefault(s => s.ScenarioId == id) ?? SelectedScenario;
            StatusMessage = $"시나리오 생성: {NewScenarioName}";
        }
        catch (Exception ex) { StatusMessage = $"시나리오 생성 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshAreasAsync();

    private async Task RefreshAreasAsync()
    {
        try
        {
            WallDefs.Clear();
            foreach (var w in await _api.GetWallsAsync(TankId)) WallDefs.Add(w);
            Areas.Clear();
            foreach (var a in await _api.GetAreasAsync(TankId)) Areas.Add(a);
            Walls.Clear();
            foreach (var w in Areas.Select(a => a.WallCode).Distinct().OrderBy(w => w)) Walls.Add(w);
            SelectedWall ??= Walls.FirstOrDefault();
            await LoadWallTasksAndProjectAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 조회 실패: {ex.Message}"; }
    }

    // 벽면 등록 [정차각 자동화] — 벽면 레지스트리(코드·설명). 정차각은 영역·작업 seam 기하에서 자동 산출.
    [RelayCommand]
    private async Task RegisterWallAsync()
    {
        if (string.IsNullOrWhiteSpace(WallCodeInput))
        {
            StatusMessage = "벽면 코드를 입력하세요.";
            return;
        }
        try
        {
            await _api.CreateWallAsync(TankId, WallLevel, WallCodeInput,
                string.IsNullOrWhiteSpace(WallDescription) ? null : WallDescription, _operatorId);
            StatusMessage = $"벽면 등록: L{WallLevel}/{WallCodeInput}";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"벽면 등록 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteWallAsync(WallDto? wall)
    {
        if (wall is null) return;
        try { await _api.DeleteWallAsync(wall.TankId, wall.Level, wall.WallCode); StatusMessage = "벽면 삭제됨."; await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"벽면 삭제 실패: {ex.Message}"; }  // 참조 영역 존재 409
    }

    [RelayCommand]
    private async Task RegisterAreaAsync()
    {
        // 도면 좌표 입력 규약 사전검증 [TANK_WALL_LAYOUT §6] — 라운드트립 전 즉시 안내. 서버(400/409)가 최종 판정.
        if (MinX >= MaxX || MinY >= MaxY)
        {
            StatusMessage = "영역 경계가 올바르지 않습니다: min < max (X·Y) 이어야 합니다.";
            return;
        }
        if (string.IsNullOrWhiteSpace(AreaWall))   // 법선은 벽면에서 상속 → 등록된 벽면 선택 필요
        {
            StatusMessage = "벽면을 선택하세요(법선은 벽면에서 상속). 먼저 벽면을 등록하세요.";
            return;
        }
        // 동일 벽면(층·벽면) 내 영역 이름 중복 즉시 안내 — 서버 409가 최종 판정.
        if (Areas.Any(a => a.Level == AreaLevel && a.WallCode == AreaWall && a.Name == AreaName))
        {
            StatusMessage = $"이미 등록된 영역입니다: L{AreaLevel}/{AreaWall}/{AreaName}";
            return;
        }
        try
        {
            await _api.CreateAreaAsync(TankId, AreaLevel, AreaWall, AreaName,
                MinX, MinY, MaxX, MaxY, null, null, null, _operatorId);
            StatusMessage = $"영역 등록: {AreaWall}/{AreaName} [{MinX},{MinY}]–[{MaxX},{MaxY}]";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }  // 경계 400·미등록벽면 400·중복 409 등
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
        if (SelectedArea is null) return;
        try
        {
            int seq = await _api.CreateAreaTaskAsync(SelectedArea.AreaId,
                new[] { StartX, StartY, StartZ }, new[] { EndX, EndY, EndZ },
                "LINE", "DXF-1", "PROF-1", _operatorId);
            StatusMessage = $"작업 등록: seq {seq} ({StartX},{StartY})–({EndX},{EndY})";
            await LoadSelectedAreaTasksAsync();
            await LoadWallTasksAndProjectAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 등록 실패: {ex.Message}"; }  // 경계 밖 400
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(AreaTaskDto? task)
    {
        if (task is null) return;
        try
        {
            await _api.DeleteAreaTaskAsync(task.TaskId);
            AreaTasks.Remove(task);
            StatusMessage = "작업 삭제됨.";
            await LoadWallTasksAndProjectAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 삭제 실패: {ex.Message}"; }
    }

    private bool CanGenerate() => SelectedScenario is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (SelectedScenario is null) return;
        try
        {
            var (stations, tasks) = await _api.GenerateFromAreasAsync(SelectedScenario.ScenarioId);
            StatusMessage = $"미션 생성 완료: 스테이션 {stations} · 작업 {tasks}";
        }
        catch (Exception ex) { StatusMessage = $"미션 생성 실패: {ex.Message}"; }  // 유효 T_W_D 없음 400
    }

    private async Task LoadSelectedAreaTasksAsync()
    {
        AreaTasks.Clear();
        if (SelectedArea is null) return;
        foreach (var t in await _api.GetAreaTasksAsync(SelectedArea.AreaId)) AreaTasks.Add(t);
    }

    private async Task LoadWallTasksAndProjectAsync()
    {
        _tasksByArea.Clear();
        if (SelectedWall is not null)
            foreach (var a in Areas.Where(a => a.WallCode == SelectedWall))
                _tasksByArea[a.AreaId] = (await _api.GetAreaTasksAsync(a.AreaId)).ToList();
        Project();
    }

    partial void OnSelectedScenarioChanged(ScenarioSummaryDto? value)
    {
        GenerateCommand.NotifyCanExecuteChanged();
        _ = RefreshAreasAsync();
    }

    partial void OnSelectedWallChanged(string? value) => _ = LoadWallTasksAndProjectAsync();

    partial void OnSelectedAreaChanged(AreaDto? value)
    {
        RegisterTaskCommand.NotifyCanExecuteChanged();
        _ = LoadSelectedAreaTasksAsync();
    }

    /// <summary>선택 벽면의 영역 경계 + 작업 끝점 bbox → 캔버스 auto-fit(Y 뒤집기) 투영.</summary>
    private void Project()
    {
        AreaShapes.Clear(); StationMarkers.Clear(); TaskSegments.Clear();
        if (SelectedWall is null) return;
        var wallAreas = Areas.Where(a => a.WallCode == SelectedWall).ToList();
        if (wallAreas.Count == 0) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Fit(double x, double y)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        foreach (var a in wallAreas)
        {
            Fit(a.MinX, a.MinY); Fit(a.MaxX, a.MaxY); Fit(a.StationX, a.StationY);
            if (_tasksByArea.TryGetValue(a.AreaId, out var ts))
                foreach (var t in ts) { Fit(t.SeamStart[0], t.SeamStart[1]); Fit(t.SeamEnd[0], t.SeamEnd[1]); }
        }
        double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
        double scale = Math.Min((CanvasSize - 2 * Margin) / spanX, (CanvasSize - 2 * Margin) / spanY);
        (double cx, double cy) Proj(double x, double y) =>
            (Margin + (x - minX) * scale, Margin + (maxY - y) * scale);

        foreach (var a in wallAreas)
        {
            var (lx, ty) = Proj(a.MinX, a.MaxY);   // 좌상단(도면 maxY = 캔버스 top)
            var (rx, by) = Proj(a.MaxX, a.MinY);
            AreaShapes.Add(new AreaShape(lx, ty, rx - lx, by - ty, a.Name));

            var (sx, sy) = Proj(a.StationX, a.StationY);
            StationMarkers.Add(new StationMarker(sx - 6, sy - 6, a.IsOverride));

            if (_tasksByArea.TryGetValue(a.AreaId, out var ts))
                foreach (var t in ts)
                {
                    var (x1, y1) = Proj(t.SeamStart[0], t.SeamStart[1]);
                    var (x2, y2) = Proj(t.SeamEnd[0], t.SeamEnd[1]);
                    TaskSegments.Add(new AreaTaskSegment(x1, y1, x2, y2, x2, y2,
                        (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
                }
        }
    }
}
