using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 좌측 화물창 뷰(3D + 전개도) 데이터. 벽면 코드/좌표계는 TankLayout(전개도·3D 공유 기준)에서 온다.
/// 3D 셸은 지오메트리 API(GetWallsAsync)의 실제 10면을 소비하며, 선택 층 도달 밴드를 강조한다.
/// 벽면별 완료율 집계 API는 아직 없어 로봇 현재 위치만 실데이터로 오버레이한다.
/// </summary>
public sealed partial class TankViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<WallCode> Walls { get; } = new(TankLayout.Walls);

    /// <summary>3D 셸 = 지오메트리 API의 실제 10면(도면 3D 프레임).</summary>
    public ObservableCollection<WallDto> ShellWalls { get; } = new();
    /// <summary>선택 층에서 도달 가능한 면 + reachableVBand (층 z-밴드 강조용).</summary>
    public ObservableCollection<WallDto> LevelWalls { get; } = new();

    /// <summary>영역+소속 작업(용접선) 묶음 — 3D 오버레이용.</summary>
    public sealed record AreaOverlay(AreaDto Area, IReadOnlyList<AreaTaskDto> Tasks);

    /// <summary>등록된 영역·작업 (3D 도면 오버레이). 뷰 모드로 필터해 렌더.</summary>
    public ObservableCollection<AreaOverlay> Overlays { get; } = new();

    /// <summary>전개도 탭 — 면별 2D 도면(실제 비율 형상 + 영역·작업 오버레이, 이미 캔버스 px로 투영).</summary>
    public sealed record FacePlot(string Code, string Dim, PointCollection Outline,
        IReadOnlyList<AreaPoly> Areas, IReadOnlyList<TaskSeg> Tasks);
    public ObservableCollection<FacePlot> FacePlots { get; } = new();

    // 전개도 셀(면당) 렌더 크기(px)
    private const double CellW = 240, CellH = 150, CellMargin = 14;

    /// <summary>3D 뷰 모드: "전체" + 층 목록(L1~L4). 전체=개관, L{n}=그 층 슬라이스 격리.</summary>
    public ObservableCollection<string> ViewModes { get; } =
        new(new[] { AllMode }.Concat(TankLayout.Floors.Select(f => f.Level)));

    private const string AllMode = "전체";

    /// <summary>셸/강조를 다시 빌드해야 함(데이터 로드·뷰 모드 변경). 뷰가 구독.</summary>
    public event EventHandler? ViewChanged;

    [ObservableProperty] private string _tankId = TankLayout.DefaultTankId;
    [ObservableProperty] private string _selectedViewMode = AllMode;
    [ObservableProperty] private bool _showOverlays = true;   // 영역·작업 3D 표시 토글
    [ObservableProperty] private TankFloor? _selectedFloor;

    /// <summary>선택 뷰 모드의 층 번호(1-based). "전체"면 null.</summary>
    public int? SelectedLevel =>
        SelectedViewMode is { } m && m != AllMode &&
        int.TryParse(m.TrimStart('L', 'l'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv)
            ? lv : null;

    /// <summary>층 슬라이스 격리 모드인지(전체가 아니면 true) — 뷰가 채움 면을 생략하고 슬라이스를 강조.</summary>
    public bool IsolateLevel => SelectedLevel is not null;
    [ObservableProperty] private double? _robotX;
    [ObservableProperty] private double? _robotY;
    [ObservableProperty] private string? _robotMapId;

    public bool HasRobotPosition => RobotX is not null && RobotY is not null;

    // ── 평면도(2D): 상면 투영(도면 x-y). 층별 로봇 이동 가능 구역 = 데크 높이의 팔각 단면 footprint ──
    private const double PlanW = 900, PlanH = 520, PlanMargin = 40;

    /// <summary>전폭 엔벨로프(맥락, 점선) — 상면 L×B 사각형(px).</summary>
    [ObservableProperty] private PointCollection _planEnvelope = new();
    /// <summary>선택 층 데크에서 로봇이 이동 가능한 구역(채움) — L×2·HalfWidth(deckZ) 사각형(px).</summary>
    [ObservableProperty] private PointCollection _planReach = new();
    [ObservableProperty] private string _planCaption = "선창 미로드 — 프로젝트를 열거나 생성하세요.";
    [ObservableProperty] private string _planBowLabel = "";
    [ObservableProperty] private double _planOriginX;
    [ObservableProperty] private double _planOriginY;
    [ObservableProperty] private double _planRobotX;
    [ObservableProperty] private double _planRobotY;
    [ObservableProperty] private bool _planHasRobot;
    [ObservableProperty] private double _planRobotOpacity = 1.0;

    /// <summary>운영 바에서 선택한 로봇(ShellViewModel이 Mission.SelectedRobotId를 동기화). 이동 명령 대상.</summary>
    [ObservableProperty] private string? _selectedRobotId;
    /// <summary>이동 명령 결과/오류 안내(층 불일치 등).</summary>
    [ObservableProperty] private string? _planGotoStatus;

    // 상면 투영 파라미터(로봇 마커가 footprint와 동일 변환을 쓰도록 저장)
    private double _pScale, _pOffX, _pOffY, _pXMin, _pYMax;
    private bool _planReady;

    /// <summary>선택 층 데크의 상면 이동 구역 + 전폭 엔벨로프를 px로 투영한다(Geometry·SelectedLevel 의존).</summary>
    private void BuildPlan()
    {
        if (Geometry is not { } g)
        {
            _planReady = false; PlanHasRobot = false;
            PlanEnvelope = new(); PlanReach = new();
            PlanCaption = "선창 정보 없음 — 프로젝트를 열거나 생성하세요."; PlanBowLabel = "";
            return;
        }

        double L = g.LengthL, B = g.Derived.B, ox = g.OriginOx, oy = g.OriginOy;
        double xMin = ox - L / 2, xMax = ox + L / 2, yMin = oy - B / 2, yMax = oy + B / 2;
        double scale = Math.Min((PlanW - 2 * PlanMargin) / L, (PlanH - 2 * PlanMargin) / B);
        _pScale = scale;
        _pOffX = (PlanW - L * scale) / 2;
        _pOffY = (PlanH - B * scale) / 2;
        _pXMin = xMin; _pYMax = yMax; _planReady = true;

        System.Windows.Point P(double x, double y) =>
            new(_pOffX + (x - _pXMin) * scale, _pOffY + (_pYMax - y) * scale);

        PlanEnvelope = new PointCollection { P(xMin, yMin), P(xMax, yMin), P(xMax, yMax), P(xMin, yMax) };

        double hw;
        if (SelectedLevel is int lv && g.LevelZ is { Length: > 0 } lz && lv - 1 < lz.Length)
        {
            double deckZ = lz[lv - 1];
            hw = HalfWidth(g, deckZ);
            PlanCaption = $"L{lv} 데크 z={deckZ:0.##} m · 이동 구역 폭 {2 * hw:0.##} × 길이 {L:0.##} m";
        }
        else
        {
            hw = B / 2;
            PlanCaption = $"전체 개관 · 전폭 {B:0.##} × 길이 {L:0.##} m (상면 투영)";
        }
        PlanReach = new PointCollection { P(xMin, oy - hw), P(xMax, oy - hw), P(xMax, oy + hw), P(xMin, oy + hw) };

        var o = P(ox, oy); PlanOriginX = o.X - 5; PlanOriginY = o.Y - 5;   // 원점 마커(지름 10) 중심 보정
        PlanBowLabel = "▲ +y 좌현(Port)    ▶ +x 선수(F)";
        BuildPlanRobot();
    }

    /// <summary>로봇 상면 마커를 현재 footprint 변환으로 갱신(다른 층이면 흐리게).</summary>
    private void BuildPlanRobot()
    {
        if (!_planReady || RobotX is not double rx || RobotY is not double ry) { PlanHasRobot = false; return; }
        PlanHasRobot = true;
        PlanRobotX = _pOffX + (rx - _pXMin) * _pScale - 7;    // 마커(지름 14) 중심 보정
        PlanRobotY = _pOffY + (_pYMax - ry) * _pScale - 7;
        PlanRobotOpacity = RobotOnSelectedFloor ? 1.0 : 0.35;
    }

    /// <summary>
    /// 2D 평면도 우클릭 "여기로 이동" — 캔버스 px 클릭점을 도면 좌표로 역투영해 선택 로봇에 이동 명령.
    /// 가드: 층(L{n}) 미선택('전체')·로봇 미선택 시 거부. 서버가 로봇 층 ≠ 대상 층이면 409(이동 금지)로 반려.
    /// </summary>
    public async Task GotoHereAsync(double canvasPxX, double canvasPxY)
    {
        PlanGotoStatus = null;
        if (!_planReady) { PlanGotoStatus = "선창 미로드 — 이동할 수 없습니다."; return; }
        if (SelectedLevel is not int level)
        {
            PlanGotoStatus = "층(L1~L4)을 먼저 선택하세요. '전체' 뷰에서는 이동할 수 없습니다.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedRobotId))
        {
            PlanGotoStatus = "이동할 로봇을 먼저 선택하세요(운영 바의 로봇 콤보).";
            return;
        }

        // 캔버스 px → 도면 좌표 (BuildPlan 투영의 역변환)
        double dx = _pXMin + (canvasPxX - _pOffX) / _pScale;
        double dy = _pYMax - (canvasPxY - _pOffY) / _pScale;
        string mapId = $"{TankId}-L{level}";
        try
        {
            await _api.GotoAsync(SelectedRobotId!, mapId, dx, dy, null, "operator");
            PlanGotoStatus = $"이동 명령 전송: {SelectedRobotId} → 도면({dx:0.##}, {dy:0.##}) @ {mapId}";
        }
        catch (Exception ex)
        {
            PlanGotoStatus = $"이동 불가: {ex.Message}";   // 층 불일치(409) 등 서버 메시지 노출
        }
    }

    /// <summary>팔각 단면 반폭 y(z) — 하부챔퍼/수직벽/상부챔퍼 구간별 선형(3D HalfWidth와 동일 정의).</summary>
    private static double HalfWidth(TankGeometryDto g, double z)
    {
        double b2 = g.Derived.B / 2, wf2 = g.WFloor / 2, wc2 = g.Derived.WCeil / 2;
        double hLow = g.HLow, zWall = g.HLow + g.HWall, h = g.Derived.H;
        if (z <= hLow) return hLow > 1e-9 ? wf2 + (z / hLow) * (b2 - wf2) : b2;   // 하부 챔퍼
        if (z <= zWall) return b2;                                               // 수직벽
        double hUp = h - zWall;
        return hUp > 1e-9 ? b2 - ((z - zWall) / hUp) * (b2 - wc2) : wc2;         // 상부 챔퍼
    }

    /// <summary>선택 뷰 층에 로봇이 있는지 — 다른 층이면 마커를 흐리게. "전체"면 항상 표시(비교 안 함).</summary>
    public bool RobotOnSelectedFloor =>
        SelectedLevel is null ||
        string.Equals(RobotMapId, $"{TankId}-L{SelectedLevel}", StringComparison.OrdinalIgnoreCase);

    public TankViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.RobotStateReceived += OnRobotState;
    }

    /// <summary>선창 파라미터(팔각 치수·유도값). 마구리(F/A) 팔각 윤곽 렌더에 사용.</summary>
    public TankGeometryDto? Geometry { get; private set; }

    /// <summary>선창 면 전체를 불러와 3D 셸을 구성하고, 현재 뷰 모드 강조를 로드한다.</summary>
    public async Task LoadAsync()
    {
        try
        {
            Geometry = await _api.GetTankGeometryAsync(TankId);   // 마구리 팔각 윤곽용 치수
            var walls = await _api.GetWallsAsync(TankId);
            ShellWalls.Clear();
            foreach (var w in walls) ShellWalls.Add(w);
            BuildPlan();                   // 평면도(2D) 상면 투영 갱신
            await LoadOverlaysAsync();     // 영역·작업 로드 후 ViewChanged
            await LoadLevelWallsAsync();   // ViewChanged 발생(셸+강조 재빌드)
        }
        catch
        {
            // 서버 미기동/면 미생성 — 빈 셸 유지(무해).
            Geometry = null;
            ShellWalls.Clear();
            LevelWalls.Clear();
            Overlays.Clear();
            BuildPlan();                   // 빈 상태로 평면도 갱신(캡션 안내)
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>등록된 영역·작업을 불러와 3D 오버레이를 재구성한다. 2D 탭 변경 시 외부에서 재호출.</summary>
    public async Task LoadOverlaysAsync()
    {
        Overlays.Clear();
        try
        {
            var areas = await _api.GetAreasAsync(TankId);
            foreach (var a in areas)
            {
                var tasks = await _api.GetAreaTasksAsync(a.AreaId);
                Overlays.Add(new AreaOverlay(a, tasks));
            }
        }
        catch { /* 조회 실패는 무해 — 오버레이 없이 셸만 */ }
        BuildFacePlots();   // 전개도(면별 2D 도면) 갱신
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>전개도: 각 면(ShellWalls)을 실제 비율 2D 도형(사각형/마구리 팔각)으로, 영역·작업 오버레이 포함해 셀에 투영.</summary>
    private void BuildFacePlots()
    {
        FacePlots.Clear();
        foreach (var w in ShellWalls)
        {
            if (w.ULen <= 0 || w.VLen <= 0) continue;
            double scale = Math.Min((CellW - 2 * CellMargin) / w.ULen, (CellH - 2 * CellMargin) / w.VLen);
            (double x, double y) Proj(double u, double v) => (CellMargin + u * scale, CellMargin + (w.VLen - v) * scale);
            PointCollection PC(IEnumerable<double[]> uv)
            {
                var pc = new PointCollection();
                foreach (var p in uv) { var (x, y) = Proj(p[0], p[1]); pc.Add(new System.Windows.Point(x, y)); }
                return pc;
            }

            var outline = PC(FaceOutlineUv(w));

            var areas = new List<AreaPoly>();
            var tasks = new List<TaskSeg>();
            if (ShowOverlays)
            {
                foreach (var ov in Overlays.Where(o => o.Area.WallCode == w.WallCode))
                {
                    var corners = ov.Area.Corners ?? RectUv(ov.Area.UMin, ov.Area.VMin, ov.Area.UMax, ov.Area.VMax);
                    var pc = PC(corners);
                    double cx = pc.Count > 0 ? pc.Average(p => p.X) : 0, cy = pc.Count > 0 ? pc.Average(p => p.Y) : 0;
                    areas.Add(new AreaPoly(pc, cx, cy, ov.Area.Name));
                    foreach (var t in ov.Tasks)
                    {
                        var (x1, y1) = Proj(t.StartU, t.StartV);
                        var (x2, y2) = Proj(t.EndU, t.EndV);
                        tasks.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
                    }
                }
            }

            FacePlots.Add(new FacePlot(w.WallCode, $"{w.ULen:0.#} × {w.VLen:0.#} m", outline, areas, tasks));
        }
    }

    private static double[][] RectUv(double uMin, double vMin, double uMax, double vMax) => new[]
    {
        new[] { uMin, vMin }, new[] { uMax, vMin }, new[] { uMax, vMax }, new[] { uMin, vMax },
    };

    /// <summary>면 경계 (u,v) 정점. 마구리(F/A)+Geometry 로드 시 팔각, 그 외 사각형.</summary>
    private IReadOnlyList<double[]> FaceOutlineUv(WallDto w)
    {
        if (w.WallCode is "F" or "A" && Geometry is { } g)
        {
            double b = g.Derived.B, wf = g.WFloor, wc = g.Derived.WCeil, hl = g.HLow, zw = g.HLow + g.HWall, h = g.Derived.H;
            return new[]
            {
                new[] { b / 2 - wf / 2, 0 }, new[] { 0.0, hl }, new[] { 0.0, zw }, new[] { b / 2 - wc / 2, h },
                new[] { b / 2 + wc / 2, h }, new[] { b, zw }, new[] { b, hl }, new[] { b / 2 + wf / 2, 0.0 },
            };
        }
        return RectUv(0, 0, w.ULen, w.VLen);
    }

    /// <summary>선택 뷰 모드가 층(L{n})이면 그 층 도달 면+reachableVBand 로드, "전체"면 clear. 후 ViewChanged.</summary>
    private async Task LoadLevelWallsAsync()
    {
        LevelWalls.Clear();
        if (ShellWalls.Count > 0 && SelectedLevel is int level)
        {
            try
            {
                foreach (var w in await _api.GetWallsAsync(TankId, level)) LevelWalls.Add(w);
            }
            catch { /* 강조 실패는 무해 — 와이어만 표시 */ }
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        RobotX = s.ReportedX;
        RobotY = s.ReportedY;
        RobotMapId = s.ReportedMapId;
        OnPropertyChanged(nameof(HasRobotPosition));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
        BuildPlanRobot();   // 평면도(2D) 로봇 마커 갱신
    }

    partial void OnSelectedViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedLevel));
        OnPropertyChanged(nameof(IsolateLevel));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
        BuildPlan();                 // 평면도(2D) 층별 이동 구역 갱신
        _ = LoadLevelWallsAsync();   // 뷰 모드 변경 시 슬라이스 갱신
    }

    partial void OnShowOverlaysChanged(bool value) { BuildFacePlots(); ViewChanged?.Invoke(this, EventArgs.Empty); }
}
