using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>전개도 캔버스에 그릴 스테이션(anchorGroup) 반투명 박스 — 좌표는 이미 캔버스 px로 투영됨.</summary>
public sealed record StationShape(double Left, double Top, double Width, double Height, string Label);

/// <summary>전개도 캔버스에 그릴 TASK 선분 — 좌표는 이미 캔버스 px. 방향=Start→End(End에 마커).</summary>
public sealed record TaskShape(
    double X1, double Y1, double X2, double Y2,
    double EndX, double EndY, double MidX, double MidY,
    string BadgeText, string AlignText);

/// <summary>
/// 슬라이싱/TASK 시각화 [PHASE2 WP-5b]. seam 등록 → generate-from-seams(백엔드 SeamSlicer) →
/// GET stations → 선택 벽면의 스테이션/TASK를 도면좌표 bbox auto-fit으로 캔버스에 투영해 렌더한다.
/// </summary>
public sealed partial class SlicingViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public const double CanvasSize = 600;
    private const double Margin = 28;

    public ObservableCollection<ScenarioSummaryDto> Scenarios { get; } = new();
    public ObservableCollection<SeamDto> Seams { get; } = new();
    public ObservableCollection<string> Walls { get; } = new();
    public ObservableCollection<StationShape> StationShapes { get; } = new();
    public ObservableCollection<TaskShape> TaskShapes { get; } = new();

    private readonly List<SlicedStationDto> _stations = new();

    [ObservableProperty] private ScenarioSummaryDto? _selectedScenario;
    [ObservableProperty] private string? _selectedWall;
    [ObservableProperty] private string? _statusMessage;

    // 새 시나리오
    [ObservableProperty] private string _newScenarioName = "슬라이싱 시나리오";

    // seam 등록 입력 (도면 좌표, m)
    [ObservableProperty] private int _seamLevel = 2;
    [ObservableProperty] private string _seamWall = "W03";
    [ObservableProperty] private double _startX;
    [ObservableProperty] private double _startY;
    [ObservableProperty] private double _endX = 3.2;
    [ObservableProperty] private double _endY;
    [ObservableProperty] private double _normalX;
    [ObservableProperty] private double _normalY = 1;
    [ObservableProperty] private string _sectionDxfId = "DXF-1";
    [ObservableProperty] private string _profileId = "PROF-1";

    public SlicingViewModel(IAcsApiClient api, IOptions<AcsOptions> options)
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
        }
        catch (Exception ex) { StatusMessage = $"시나리오 조회 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CreateScenarioAsync()
    {
        try
        {
            var id = await _api.CreateScenarioAsync(NewScenarioName, TankId);
            await LoadAsync();
            SelectedScenario = Scenarios.FirstOrDefault(s => s.ScenarioId == id) ?? SelectedScenario;
            StatusMessage = $"시나리오 생성: {NewScenarioName} ({id})";
        }
        catch (Exception ex) { StatusMessage = $"시나리오 생성 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RegisterSeamAsync()
    {
        try
        {
            var path = new[] { new[] { StartX, StartY, 0.0 }, new[] { EndX, EndY, 0.0 } };
            var normal = new[] { NormalX, NormalY, 0.0 };
            await _api.CreateSeamAsync(TankId, SeamLevel, SeamWall, "LINE", path, normal,
                SectionDxfId, ProfileId, _operatorId);
            StatusMessage = $"seam 등록: {TankId}/L{SeamLevel}/{SeamWall} ({StartX},{StartY})-({EndX},{EndY})";
            await RefreshSeamsAsync();
        }
        catch (Exception ex) { StatusMessage = $"seam 등록 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteSeamAsync(SeamDto? seam)
    {
        if (seam is null) return;
        try { await _api.DeleteSeamAsync(seam.SeamId); Seams.Remove(seam); StatusMessage = "seam 삭제됨."; }
        catch (Exception ex) { StatusMessage = $"seam 삭제 실패: {ex.Message}"; }
    }

    private bool CanGenerate() => SelectedScenario is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (SelectedScenario is null) return;
        try
        {
            var (stations, tasks) = await _api.GenerateFromSeamsAsync(SelectedScenario.ScenarioId);
            StatusMessage = $"슬라이싱 완료: 스테이션 {stations} · TASK {tasks}";
            await RefreshStationsAsync();
        }
        catch (Exception ex) { StatusMessage = $"슬라이싱 실패: {ex.Message}"; }  // 400 유효 T_W_D 없음 등
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshSeamsAsync();
        await RefreshStationsAsync();
    }

    private async Task RefreshSeamsAsync()
    {
        if (SelectedScenario is null) return;
        try
        {
            Seams.Clear();
            foreach (var s in await _api.GetSeamsAsync(TankId)) Seams.Add(s);
        }
        catch (Exception ex) { StatusMessage = $"seam 조회 실패: {ex.Message}"; }
    }

    private async Task RefreshStationsAsync()
    {
        if (SelectedScenario is null) return;
        try
        {
            _stations.Clear();
            _stations.AddRange(await _api.GetStationsAsync(SelectedScenario.ScenarioId));
            Walls.Clear();
            foreach (var w in _stations.Select(s => s.WallCode).Distinct().OrderBy(w => w)) Walls.Add(w);
            SelectedWall = Walls.FirstOrDefault();   // triggers Project()
            Project();
        }
        catch (Exception ex) { StatusMessage = $"스테이션 조회 실패: {ex.Message}"; }
    }

    partial void OnSelectedScenarioChanged(ScenarioSummaryDto? value)
    {
        GenerateCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync();
    }

    partial void OnSelectedWallChanged(string? value) => Project();

    /// <summary>선택 벽면의 스테이션/TASK를 도면좌표 bbox → 캔버스 auto-fit(Y 뒤집기)으로 투영.</summary>
    private void Project()
    {
        StationShapes.Clear();
        TaskShapes.Clear();
        var wall = SelectedWall;
        if (wall is null) return;
        var sts = _stations.Where(s => string.Equals(s.WallCode, wall, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sts.Count == 0) return;

        // 1) 도면 bbox (모든 task 끝점 + 스테이션 앵커)
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Fit(double x, double y)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        foreach (var s in sts)
        {
            Fit(s.StationDrawing.X, s.StationDrawing.Y);
            foreach (var t in s.Tasks)
            {
                Fit(t.SeamStartDrawing[0], t.SeamStartDrawing[1]);
                Fit(t.SeamEndDrawing[0], t.SeamEndDrawing[1]);
            }
        }
        double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
        double scale = Math.Min((CanvasSize - 2 * Margin) / spanX, (CanvasSize - 2 * Margin) / spanY);

        // 도면(x,y) → 캔버스 px (Y 뒤집기)
        (double cx, double cy) Proj(double x, double y) =>
            (Margin + (x - minX) * scale, Margin + (maxY - y) * scale);

        foreach (var s in sts)
        {
            // 스테이션 박스 = 멤버 task 끝점 bbox → 캔버스 + 8px 패딩
            double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;
            foreach (var t in s.Tasks)
            {
                foreach (var pt in new[] { t.SeamStartDrawing, t.SeamEndDrawing })
                {
                    var (cx, cy) = Proj(pt[0], pt[1]);
                    if (cx < bMinX) bMinX = cx; if (cx > bMaxX) bMaxX = cx;
                    if (cy < bMinY) bMinY = cy; if (cy > bMaxY) bMaxY = cy;
                }
            }
            const double pad = 10;
            StationShapes.Add(new StationShape(
                bMinX - pad, bMinY - pad, (bMaxX - bMinX) + 2 * pad, (bMaxY - bMinY) + 2 * pad, s.AnchorGroupId));

            foreach (var t in s.Tasks)
            {
                var (x1, y1) = Proj(t.SeamStartDrawing[0], t.SeamStartDrawing[1]);
                var (x2, y2) = Proj(t.SeamEndDrawing[0], t.SeamEndDrawing[1]);
                TaskShapes.Add(new TaskShape(
                    x1, y1, x2, y2, x2, y2, (x1 + x2) / 2, (y1 + y2) / 2,
                    t.SeqInGroup.ToString(),
                    t.SeqInGroup == 1 ? "정렬 포함" : "정렬 공유"));
            }
        }
    }
}
