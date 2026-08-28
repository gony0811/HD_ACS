using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

/// <summary>(u,v) 캔버스 도형 — 좌표는 이미 캔버스 px로 투영됨.</summary>
public sealed record AreaBox(double Left, double Top, double Width, double Height, string Label);
/// <summary>임의 4점 영역 폴리곤(캔버스 px) — Points=투영된 꼭짓점, 라벨 앵커. Status=work_item 상태(null=계획 표시).</summary>
public sealed record AreaPoly(PointCollection Points, double LabelX, double LabelY, string Label, string? Status = null);
public sealed record TaskSeg(double X1, double Y1, double X2, double Y2, double EndX, double EndY, double MidX, double MidY, string Badge,
    string? Status = null);   // 액션 상태(운영 중 용접선 색) — null=계획 표시(주황)
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
    public ObservableCollection<AreaDto> Areas { get; } = new();              // 그리드용 — 선택 층, 층-로컬 v
    private readonly List<AreaDto> _allAreas = new();                          // 전개도용 — 그 면 모든 층, 면-전체 v
    public ObservableCollection<AreaTaskDto> AreaTasks { get; } = new();
    public ObservableCollection<AreaPoly> AreaBoxes { get; } = new();          // 선택 층 영역(활성·녹색) — 4점 폴리곤
    public ObservableCollection<AreaPoly> InactiveAreaBoxes { get; } = new();  // 타 층 영역(회색·비활성)
    public ObservableCollection<AreaPoly> DraftAreas { get; } = new();          // 입력 중 영역 미리보기(점선)
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

    // 선택 면 경계 폴리곤(캔버스 px) — 면 전체(회색 음영). 마구리(F/A)는 팔각, 그 외 직사각형.
    [ObservableProperty] private PointCollection _faceOutline = new();
    // 선택 층 활성 밴드 폴리곤(캔버스 px) — 면 전체 위에 활성(밝게) 오버레이.
    [ObservableProperty] private PointCollection _activeBand = new();
    private double _derB, _derWCeil, _derH;   // 파생값(전폭·천장폭·전체높이) — 마구리 팔각 윤곽용

    // ── 층 필터 (v3.1 §9 — 선택 층에서 도달 가능한 면만 노출) ──
    [ObservableProperty] private LevelOption? _selectedLevel;

    // ── 영역 등록 입력 (면 로컬 u,v, 임의 4점 사각형). level은 서버가 유도 → 입력 없음 ──
    [ObservableProperty] private WallDto? _selectedWall;
    [ObservableProperty] private AreaDto? _selectedArea;
    [ObservableProperty] private string _areaName = "A01";
    // 코너 P1~P4 (면-로컬 u,v). 기본=작은 사각형. 캔버스 클릭으로도 순서대로 지정.
    [ObservableProperty] private double _c1U; [ObservableProperty] private double _c1V;
    [ObservableProperty] private double _c2U = 2.0; [ObservableProperty] private double _c2V;
    [ObservableProperty] private double _c3U = 2.0; [ObservableProperty] private double _c3V = 2.0;
    [ObservableProperty] private double _c4U; [ObservableProperty] private double _c4V = 2.0;
    [ObservableProperty] private int _cornerIndex;   // 다음 클릭이 지정할 코너(0~3)
    [ObservableProperty] private bool _pickMode;     // 도면에서 4점 선택(픽) 모드 — 켜면 캔버스 커서=크로스헤어
    [ObservableProperty] private bool _stationOverride;
    [ObservableProperty] private double _stationX;
    [ObservableProperty] private double _stationY;
    [ObservableProperty] private double _stationTheta;
    // 정차 이격 [m] — 정차점 = 영역 중심 + 내부향 법선 수평성분 × 이격. null(빈값)=서버 설정 기본(0.8m)
    [ObservableProperty] private double? _stationStandoffM;

    // ── 작업 등록 입력 (면 로컬 u,v) ──
    [ObservableProperty] private double _startU;
    [ObservableProperty] private double _startV;
    [ObservableProperty] private double _endU = 1.0;
    [ObservableProperty] private double _endV;

    // 선택 층 로컬 v 오프셋 — (0,0)=그 층 도달 구간 좌하단. VM은 로컬 v로 동작, API 경계에서 ±VOff.
    private double VOff => SelectedWall?.ReachableVBand is { Length: 2 } b ? b[0] : 0;
    private double SliceH => SelectedWall?.ReachableVBand is { Length: 2 } b ? b[1] - b[0] : SelectedWall?.VLen ?? 0;

    public string SelectedWallInfo => SelectedWall is { } w
        ? $"면 {w.WallCode}{(SelectedLevel is { } l ? $" · {l.Label} 로컬" : "")} — u∈[0,{w.ULen:0.###}], v∈[0,{SliceH:0.###}] (좌하단 0,0)"
          + (w.FacingYaw is null ? " (바닥/천장: 정차 수동지정 필요)" : "")
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

    /// <summary>입력 코너 P1~P4 (면-로컬 u,v, v는 층-로컬).</summary>
    private double[][] InputCorners() => new[]
    {
        new[] { C1U, C1V }, new[] { C2U, C2V }, new[] { C3U, C3V }, new[] { C4U, C4V },
    };

    [RelayCommand(CanExecute = nameof(CanRegisterArea))]
    private async Task RegisterAreaAsync()
    {
        if (SelectedWall is not { } w) return;
        var local = InputCorners();
        var (miu, miv, mau, mav) = AreaBboxLocal(local);
        if (mau - miu < 1e-6 || mav - miv < 1e-6) { StatusMessage = "영역이 퇴화했습니다 — 유효한 사각형 4점을 입력하세요."; return; }
        if (mav > SliceH + 1e-6 || miv < -1e-6) { StatusMessage = $"코너 v가 선택 층 구간(0~{SliceH:0.###})을 벗어났습니다."; return; }
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장(각 코너 v)
        var corners = local.Select(p => new[] { p[0], p[1] + off }).ToArray();
        try
        {
            var (_, level) = await _api.CreateAreaAsync(TankId, w.WallCode, AreaName, corners,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                _operatorId, StationStandoffM);
            StatusMessage = $"영역 등록: {w.WallCode}/{AreaName} (4점) → 유도 층 L{level}";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }   // 면범위·층유도 400·중복 409
    }

    private static (double MinU, double MinV, double MaxU, double MaxV) AreaBboxLocal(double[][] pts)
    {
        double miu = double.MaxValue, miv = double.MaxValue, mau = double.MinValue, mav = double.MinValue;
        foreach (var p in pts)
        {
            if (p[0] < miu) miu = p[0]; if (p[0] > mau) mau = p[0];
            if (p[1] < miv) miv = p[1]; if (p[1] > mav) mav = p[1];
        }
        return (miu, miv, mau, mav);
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
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장
        try
        {
            int seq = await _api.CreateAreaTaskAsync(a.AreaId, StartU, StartV + off, EndU, EndV + off, "LINE", "DXF-1", "PROF-1", _operatorId);
            StatusMessage = $"작업 등록: seq {seq} ({StartU},{StartV})–({EndU},{EndV})(로컬)";
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

    /// <summary>영역·작업이 등록/삭제되어 3D 오버레이 갱신이 필요함(등록/삭제가 모두 RefreshAreasAsync 경유).</summary>
    public event EventHandler? PlanningChanged;

    private async Task RefreshAreasAsync()
    {
        // 그 면의 **모든 층** 영역을 로드(_allAreas, 면-전체 v — 전개도가 타 층을 회색으로 표시).
        // 그리드 Areas = 선택 층만 + 층-로컬 v(−VOff) — 입력 규약과 일치.
        double off = VOff; int? sel = SelectedLevel?.Level;
        var list = SelectedWall is { } w
            ? await _api.GetAreasAsync(TankId, w.WallCode)
            : (IReadOnlyList<AreaDto>)Array.Empty<AreaDto>();
        _allAreas.Clear();
        _allAreas.AddRange(list);
        Areas.Clear();
        foreach (var a in list.Where(a => sel is null || a.Level == sel))
            Areas.Add(a with { VMin = a.VMin - off, VMax = a.VMax - off, Corners = OffsetCornersV(a.Corners, -off) });
        await LoadTasksAndProjectAsync();
        PlanningChanged?.Invoke(this, EventArgs.Empty);   // 3D 오버레이 동기화
    }

    private async Task LoadTasksAndProjectAsync()
    {
        double off = VOff;   // 작업 v도 층-로컬(−VOff)로 변환
        var list = SelectedArea is { } a
            ? await _api.GetAreaTasksAsync(a.AreaId)
            : (IReadOnlyList<AreaTaskDto>)Array.Empty<AreaTaskDto>();
        AreaTasks.Clear();
        foreach (var t in list) AreaTasks.Add(t with { StartV = t.StartV - off, EndV = t.EndV - off });
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

    /// <summary>
    /// 선택 면을 **면 전체**로 투영. 면 전체=회색 음영, 선택 층 밴드=활성(밝게). 영역은 선택 층=녹색·타 층=회색.
    /// 입력값은 층-로컬 v이므로 그릴 때 +VOff로 면-전체 v로 변환. (좌표는 auto-fit·v 뒤집기.)
    /// </summary>
    private double _projScale = 1, _projVlen = 1;   // 역투영(캔버스 클릭)용

    private void Project()
    {
        AreaBoxes.Clear(); InactiveAreaBoxes.Clear(); DraftAreas.Clear();
        TaskSegments.Clear(); StationMarkers.Clear(); DraftSegments.Clear();
        if (SelectedWall is not { } w || w.ULen <= 0 || w.VLen <= 0) return;

        double off = VOff;                     // 층-로컬 → 면-전체 v
        double vlen = w.VLen;                  // 캔버스 = 면 전체
        double scale = Math.Min((CanvasSize - 2 * Margin) / w.ULen, (CanvasSize - 2 * Margin) / vlen);
        _projScale = scale; _projVlen = vlen;
        (double x, double y) Proj(double u, double v) => (Margin + u * scale, Margin + (vlen - v) * scale);

        // 코너 배열(면-전체 v) → 캔버스 폴리곤 + 라벨 앵커(centroid).
        AreaPoly Poly(double[][] corners, string label)
        {
            var pc = new PointCollection(corners.Length);
            double cx = 0, cy = 0;
            foreach (var p in corners) { var (x, y) = Proj(p[0], p[1]); pc.Add(new System.Windows.Point(x, y)); cx += x; cy += y; }
            int n = Math.Max(1, corners.Length);
            return new AreaPoly(pc, cx / n, cy / n, label);
        }

        // 면 전체(회색 음영) + 선택 층 활성 밴드(밝게)
        FaceOutline = ProjectPolygon(FaceClipPolygon(w, 0, w.VLen), Proj);
        ActiveBand = SliceH > 1e-9 ? ProjectPolygon(FaceClipPolygon(w, off, off + SliceH), Proj) : new PointCollection();

        // 영역: 그 면 모든 층(면-전체 v 코너). 선택 층=활성(녹색), 타 층=비활성(회색).
        int? sel = SelectedLevel?.Level;
        foreach (var a in _allAreas)
        {
            var corners = a.Corners ?? RectCorners(a.UMin, a.VMin, a.UMax, a.VMax);   // 구데이터 폴백
            var poly = Poly(corners, a.Name);
            if (sel is null || a.Level == sel)
            {
                AreaBoxes.Add(poly);
                StationMarkers.Add(new StationMarker(poly.LabelX - 7, poly.LabelY - 7, a.Name));   // centroid
            }
            else InactiveAreaBoxes.Add(poly);
        }
        // 선택 영역의 작업(로컬 v → +off)
        foreach (var t in AreaTasks)
        {
            var (x1, y1) = Proj(t.StartU, t.StartV + off);
            var (x2, y2) = Proj(t.EndU, t.EndV + off);
            TaskSegments.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
        }
        // 입력 미리보기(코너, 로컬 v → +off) — 활성 밴드 위치에 표시
        var draft = InputCorners();
        var (dmiu, dmiv, dmau, dmav) = AreaBboxLocal(draft);
        if (dmau - dmiu > 1e-6 && dmav - dmiv > 1e-6)
            DraftAreas.Add(Poly(draft.Select(p => new[] { p[0], p[1] + off }).ToArray(), AreaName));
        if (SelectedArea is not null)
        {
            var (px1, py1) = Proj(StartU, StartV + off);
            var (px2, py2) = Proj(EndU, EndV + off);
            DraftSegments.Add(new TaskSeg(px1, py1, px2, py2, px2 - 4, py2 - 4, (px1 + px2) / 2, (py1 + py2) / 2, "new"));
        }
    }

    private static double[][] RectCorners(double uMin, double vMin, double uMax, double vMax) => new[]
    {
        new[] { uMin, vMin }, new[] { uMax, vMin }, new[] { uMax, vMax }, new[] { uMin, vMax },
    };

    private static double[][]? OffsetCornersV(double[][]? corners, double dv) =>
        corners?.Select(p => new[] { p[0], p[1] + dv }).ToArray();

    partial void OnPickModeChanged(bool value) { if (value) CornerIndex = 0; }   // 켜면 P1부터 지정 시작

    /// <summary>픽 모드 해제 — 캔버스 우클릭 또는 ESC 키.</summary>
    [RelayCommand]
    private void CancelPick() => PickMode = false;

    /// <summary>캔버스 클릭(px) → 면-로컬 (u,v, 층-로컬)로 역투영해 현재 코너에 지정하고 다음 코너로. 픽 모드에서만.</summary>
    public void CanvasClick(double px, double py)
    {
        if (!PickMode || SelectedWall is not { } w || _projScale <= 0) return;
        double u = (px - Margin) / _projScale;
        double v = _projVlen - (py - Margin) / _projScale - VOff;   // 층-로컬 v
        u = Math.Clamp(u, 0, w.ULen);
        v = Math.Clamp(v, 0, SliceH > 1e-9 ? SliceH : w.VLen);
        SetCorner(CornerIndex % 4, u, v);
        CornerIndex = (CornerIndex + 1) % 4;
        Project();
    }

    private void SetCorner(int i, double u, double v)
    {
        switch (i)
        {
            case 0: C1U = u; C1V = v; break;
            case 1: C2U = u; C2V = v; break;
            case 2: C3U = u; C3V = v; break;
            default: C4U = u; C4V = v; break;
        }
    }

    /// <summary>(u,v) 폴리곤을 캔버스 px로 투영.</summary>
    private static PointCollection ProjectPolygon(IReadOnlyList<(double u, double v)> uv, Func<double, double, (double x, double y)> proj)
    {
        var pc = new PointCollection(uv.Count);
        foreach (var (u, v) in uv) { var (x, y) = proj(u, v); pc.Add(new System.Windows.Point(x, y)); }
        return pc;
    }

    /// <summary>
    /// 면을 v∈[vLo, vHi]로 클리핑한 (u,v) 정점(면-전체 v, 재원점 없음).
    /// 마구리(F/A)+지오메트리 로드 시 챔퍼로 잘린 팔각/사다리꼴, 그 외 직사각형.
    /// </summary>
    private IReadOnlyList<(double u, double v)> FaceClipPolygon(WallDto w, double vLo, double vHi)
    {
        vLo = Math.Max(0, vLo); vHi = Math.Min(w.VLen, vHi);
        if (vHi <= vLo) return Array.Empty<(double, double)>();

        if (w.WallCode is "F" or "A" && _derH > 0)
        {
            double zw = HLow + HWall;
            var vs = new List<double> { vLo };
            foreach (var knee in new[] { HLow, zw })
                if (knee > vLo + 1e-9 && knee < vHi - 1e-9) vs.Add(knee);
            vs.Add(vHi);
            vs.Sort();
            var pts = new List<(double, double)>(vs.Count * 2);
            foreach (var v in vs) { var (l, _) = HalfWidthU(v); pts.Add((l, v)); }                    // 좌측(u 작은) 아래→위
            for (int i = vs.Count - 1; i >= 0; i--) { var (_, r) = HalfWidthU(vs[i]); pts.Add((r, vs[i])); } // 우측 위→아래
            return pts;
        }
        return new[] { (0.0, vLo), (w.ULen, vLo), (w.ULen, vHi), (0.0, vHi) };
    }

    /// <summary>마구리 팔각의 높이 v(=z)에서 좌/우 경계 u — 하부챔퍼/수직/상부챔퍼 구간별. (u 중심=B/2)</summary>
    private (double uLeft, double uRight) HalfWidthU(double v)
    {
        double b = _derB, hw;   // 반폭
        double wf2 = WFloor / 2, wc2 = _derWCeil / 2, b2 = b / 2, hl = HLow, zw = HLow + HWall, h = _derH;
        if (v <= hl) hw = hl > 1e-9 ? wf2 + (v / hl) * (b2 - wf2) : b2;          // 하부 챔퍼
        else if (v <= zw) hw = b2;                                               // 수직
        else { double hu = h - zw; hw = hu > 1e-9 ? b2 - ((v - zw) / hu) * (b2 - wc2) : wc2; }   // 상부 챔퍼
        return (b2 - hw, b2 + hw);
    }

    partial void OnC1UChanged(double value) => Project();
    partial void OnC1VChanged(double value) => Project();
    partial void OnC2UChanged(double value) => Project();
    partial void OnC2VChanged(double value) => Project();
    partial void OnC3UChanged(double value) => Project();
    partial void OnC3VChanged(double value) => Project();
    partial void OnC4UChanged(double value) => Project();
    partial void OnC4VChanged(double value) => Project();
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
        _derB = d.B; _derWCeil = d.WCeil; _derH = d.H;   // 마구리 팔각 윤곽용
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
