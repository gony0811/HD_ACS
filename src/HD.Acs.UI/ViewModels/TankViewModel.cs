using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <summary>이동 명령 결과/오류 안내(층 불일치·이동불가구역 등).</summary>
    [ObservableProperty] private string? _planGotoStatus;

    // ── 등록 도구(벽 WALL=2점 / 이동불가 NOGO=4점 / 노드 NODE=1점 / 엣지 EDGE=노드2개) ──────
    public enum PlanTool { None, Wall, NoGo, Node, Edge, Hazard }

    /// <summary>현재 등록 도구(None이면 좌클릭은 무시).</summary>
    [ObservableProperty] private PlanTool _activeTool = PlanTool.None;
    /// <summary>도구 진행 안내(클릭 유도·결과).</summary>
    [ObservableProperty] private string? _planToolHint;
    /// <summary>등록 도구 활성 여부(뷰의 커서/게이트용).</summary>
    public bool ToolActive => ActiveTool != PlanTool.None;

    private readonly List<double[]> _pending = new();          // 진행 중 클릭점(도면 좌표)
    private List<MapAnnotationDto> _annotations = new();

    /// <summary>평면도 렌더용 — 벽(선분)·이동 불가 구역(다각형), 이미 px로 투영.</summary>
    public sealed record PlanWallVm(PointCollection Points, string Name, Guid Id);
    public sealed record PlanZoneVm(PointCollection Points, string Name, Guid Id);
    public ObservableCollection<PlanWallVm> PlanWalls { get; } = new();
    public ObservableCollection<PlanZoneVm> PlanNoGos { get; } = new();
    /// <summary>낙상 위험 등 필수 회피 구역(다각형). 안전 자문 표시 — 실제 회피는 AMR 책임.</summary>
    public ObservableCollection<PlanZoneVm> PlanHazards { get; } = new();
    /// <summary>진행 중 클릭점 미리보기(폴리라인).</summary>
    [ObservableProperty] private PointCollection _planDraft = new();

    // 네비 그래프(노드·엣지)
    private List<NodeDto> _nodes = new();
    private List<EdgeDto> _edges = new();
    public sealed record PlanNodeVm(double LeftX, double TopY, double CenterX, double CenterY, string NodeId, string Type);
    public sealed record PlanEdgeVm(PointCollection Points, string EdgeId);
    public ObservableCollection<PlanNodeVm> PlanNodes { get; } = new();
    public ObservableCollection<PlanEdgeVm> PlanEdges { get; } = new();
    private string? _edgeStartNodeId;
    [ObservableProperty] private bool _edgeStartVisible;
    [ObservableProperty] private double _edgeStartLeft;
    [ObservableProperty] private double _edgeStartTop;

    /// <summary>"등록 요소" 탭 통합 목록(벽·이동불가·노드·엣지, 전 층).</summary>
    public sealed record PlanElementRow(string Id, string Category, string Name, string Info);
    public ObservableCollection<PlanElementRow> ElementRows { get; } = new();
    private readonly List<PlanElementRow> _allRows = new();

    /// <summary>등록 요소 타입 필터.</summary>
    public ObservableCollection<string> ElementFilters { get; } = new(new[] { "전체", "노드", "엣지", "벽", "이동 불가", "낙상 위험" });
    [ObservableProperty] private string _selectedElementFilter = "전체";
    partial void OnSelectedElementFilterChanged(string value) => ApplyElementFilter();

    private static string FilterToCategory(string label) => label switch
    {
        "노드" => "NODE", "엣지" => "EDGE", "벽" => "WALL", "이동 불가" => "NOGO", "낙상 위험" => "HAZARD", _ => "ALL"
    };

    private void ApplyElementFilter()
    {
        var cat = FilterToCategory(SelectedElementFilter);
        ElementRows.Clear();
        foreach (var r in _allRows)
            if (cat == "ALL" || r.Category == cat) ElementRows.Add(r);
    }

    // ── 등록 요소 hover 하이라이트(노랑) ──────────────────────────────────
    [ObservableProperty] private string? _highlightedElementId;
    [ObservableProperty] private PointCollection _highlightPoly = new();
    [ObservableProperty] private bool _highlightPolyVisible;
    [ObservableProperty] private bool _highlightNodeVisible;
    [ObservableProperty] private double _highlightNodeLeft;
    [ObservableProperty] private double _highlightNodeTop;

    /// <summary>목록 항목 hover 시 평면도의 해당 오브젝트를 노랑으로 표시. null이면 해제.</summary>
    public void SetHighlight(string? id)
    {
        HighlightedElementId = id;
        HighlightPolyVisible = false; HighlightNodeVisible = false; HighlightPoly = new();
        if (string.IsNullOrEmpty(id)) return;

        var node = PlanNodes.FirstOrDefault(n => n.NodeId == id);
        if (node is not null) { HighlightNodeLeft = node.CenterX - 10; HighlightNodeTop = node.CenterY - 10; HighlightNodeVisible = true; return; }

        var wall = PlanWalls.FirstOrDefault(w => w.Id.ToString() == id);
        if (wall is not null) { HighlightPoly = wall.Points; HighlightPolyVisible = true; return; }

        var zone = PlanNoGos.FirstOrDefault(z => z.Id.ToString() == id)
                   ?? PlanHazards.FirstOrDefault(z => z.Id.ToString() == id);
        if (zone is not null) { var pc = new PointCollection(zone.Points); if (pc.Count > 0) pc.Add(pc[0]); HighlightPoly = pc; HighlightPolyVisible = true; return; }

        var edge = PlanEdges.FirstOrDefault(e => e.EdgeId == id);
        if (edge is not null) { HighlightPoly = edge.Points; HighlightPolyVisible = true; return; }
    }

    [RelayCommand] private void StartWallTool() => BeginTool(PlanTool.Wall);
    [RelayCommand] private void StartNoGoTool() => BeginTool(PlanTool.NoGo);
    [RelayCommand] private void StartHazardTool() => BeginTool(PlanTool.Hazard);
    [RelayCommand] private void StartNodeTool() => BeginTool(PlanTool.Node);
    [RelayCommand] private void StartEdgeTool() => BeginTool(PlanTool.Edge);

    [RelayCommand]
    private void CancelTool()
    {
        ActiveTool = PlanTool.None; _pending.Clear(); PlanDraft = new(); PlanToolHint = null;
        _edgeStartNodeId = null; EdgeStartVisible = false;
    }

    private void BeginTool(PlanTool tool)
    {
        if (SelectedLevel is null) { PlanToolHint = "층(L1~L4)을 먼저 선택하세요. '전체'에서는 등록할 수 없습니다."; return; }
        ActiveTool = tool; _pending.Clear(); PlanDraft = new();
        _edgeStartNodeId = null; EdgeStartVisible = false;
        PlanToolHint = tool switch
        {
            PlanTool.Wall => "벽 생성: 시작점 → 끝점을 클릭하세요 (ESC/우클릭 취소)",
            PlanTool.NoGo => "이동 불가 구역: 4개 지점을 순서대로 클릭하세요 (ESC/우클릭 취소)",
            PlanTool.Hazard => "낙상 위험 구역: 4개 지점을 순서대로 클릭하세요 (ESC/우클릭 취소)",
            PlanTool.Node => "노드 생성: 지점을 클릭하세요 (여러 개 가능, ESC 종료)",
            PlanTool.Edge => "엣지 연결: 시작 노드 → 끝 노드를 클릭하세요 (ESC 종료)",
            _ => null
        };
    }

    /// <summary>도구 활성 시 좌클릭 처리 — Shift=축 정렬. 노드=지점 생성, 엣지=노드 2개 연결, 벽/구역=점 누적.</summary>
    public async Task PlanToolClickAsync(double px, double py, bool shift)
    {
        if (ActiveTool == PlanTool.None || !_planReady) return;
        if (SelectedLevel is not int level) return;

        // 노드 생성(단일 클릭, 모드 유지)
        if (ActiveTool == PlanTool.Node)
        {
            var pt = SnapDrawing(px, py, shift);
            try { await _api.CreateNodeAsync(TankId, level, pt[0], pt[1], null, "WAYPOINT"); PlanToolHint = "노드 추가됨 (계속 클릭 · ESC 종료)"; await LoadGraphAsync(); }
            catch (Exception ex) { PlanToolHint = $"노드 실패: {ex.Message}"; }
            return;
        }

        // 엣지 연결(노드 2개 선택, 모드 유지)
        if (ActiveTool == PlanTool.Edge)
        {
            var picked = PickNodeAt(px, py);
            if (picked is null) { PlanToolHint = "노드를 클릭하세요 (엣지 연결)"; return; }
            if (_edgeStartNodeId is null)
            {
                _edgeStartNodeId = picked.NodeId; EdgeStartLeft = picked.LeftX - 3; EdgeStartTop = picked.TopY - 3; EdgeStartVisible = true;
                PlanToolHint = "끝 노드를 클릭하세요";
                return;
            }
            var start = _edgeStartNodeId; _edgeStartNodeId = null; EdgeStartVisible = false;
            try { await _api.CreateEdgeAsync(start, picked.NodeId, true, "TRAVEL"); PlanToolHint = "엣지 연결됨 (계속 · ESC 종료)"; await LoadGraphAsync(); }
            catch (Exception ex) { PlanToolHint = $"엣지 실패: {ex.Message}"; }
            return;
        }

        // 벽/이동 불가 구역: 점 누적
        _pending.Add(SnapDrawing(px, py, shift));   // 직전 꼭짓점 기준 수평/수직 스냅(Shift)
        UpdateDraft();

        int need = ActiveTool == PlanTool.Wall ? 2 : 4;
        if (_pending.Count < need)
        {
            PlanToolHint = ActiveTool switch
            {
                PlanTool.Wall => "끝점을 클릭하세요",
                PlanTool.Hazard => $"낙상 위험 구역: {_pending.Count}/4 점",
                _ => $"이동 불가 구역: {_pending.Count}/4 점"
            };
            return;
        }

        var kind = ActiveTool switch { PlanTool.Wall => "WALL", PlanTool.Hazard => "HAZARD", _ => "NOGO" };
        var pts = _pending.ToArray();
        try
        {
            await _api.CreateMapAnnotationAsync(TankId, level, kind, "", pts, "operator");
            PlanToolHint = kind switch { "WALL" => "벽 등록 완료", "HAZARD" => "낙상 위험 구역 등록 완료", _ => "이동 불가 구역 등록 완료" };
            await LoadAnnotationsAsync();
        }
        catch (Exception ex) { PlanToolHint = $"등록 실패: {ex.Message}"; }

        ActiveTool = PlanTool.None; _pending.Clear(); PlanDraft = new();
    }

    private void UpdateDraft()
    {
        var pc = new PointCollection();
        foreach (var p in _pending) pc.Add(ToPx(p));
        PlanDraft = pc;
    }

    /// <summary>도면 좌표 → 캔버스 px.</summary>
    private System.Windows.Point ToPx(double[] d) =>
        new(_pOffX + (d[0] - _pXMin) * _pScale, _pOffY + (_pYMax - d[1]) * _pScale);

    /// <summary>포인터 px → 도면 좌표. Shift+직전 꼭짓점 있으면 수평/수직(우세 축)으로 스냅.</summary>
    private double[] SnapDrawing(double px, double py, bool shift)
    {
        double dx = _pXMin + (px - _pOffX) / _pScale;
        double dy = _pYMax - (py - _pOffY) / _pScale;
        if (shift && _pending.Count > 0)
        {
            var last = _pending[^1];
            if (Math.Abs(dx - last[0]) >= Math.Abs(dy - last[1])) dy = last[1];   // 수평(Δy=0)
            else dx = last[0];                                                    // 수직(Δx=0)
        }
        return new[] { dx, dy };
    }

    /// <summary>등록된 맵 주석(전 층)을 불러와 목록·평면도 렌더를 갱신한다.</summary>
    public async Task LoadAnnotationsAsync()
    {
        try { _annotations = (await _api.GetMapAnnotationsAsync(TankId)).ToList(); }
        catch { _annotations = new(); }
        BuildPlanAnnotations();
        BuildElementRows();
    }

    /// <summary>네비 그래프(노드·엣지, 전 층)를 불러와 평면도 렌더·목록 갱신.</summary>
    public async Task LoadGraphAsync()
    {
        try { _nodes = (await _api.GetNodesAsync(TankId)).ToList(); } catch { _nodes = new(); }
        try { _edges = (await _api.GetEdgesAsync(TankId)).ToList(); } catch { _edges = new(); }
        BuildPlanGraph();
        BuildElementRows();
    }

    private void BuildPlanAnnotations()
    {
        PlanWalls.Clear(); PlanNoGos.Clear(); PlanHazards.Clear();
        if (!_planReady) return;
        int? lvl = SelectedLevel;   // 전체=null → 모든 층, L{n}=그 층만
        foreach (var a in _annotations)
        {
            if (lvl is int L && a.Level != L) continue;
            var pc = new PointCollection();
            foreach (var p in a.Points)
                if (p is { Length: >= 2 }) pc.Add(ToPx(p));
            switch (a.Kind)
            {
                case "WALL": PlanWalls.Add(new PlanWallVm(pc, a.Name, a.AnnotationId)); break;
                case "HAZARD": PlanHazards.Add(new PlanZoneVm(pc, a.Name, a.AnnotationId)); break;
                default: PlanNoGos.Add(new PlanZoneVm(pc, a.Name, a.AnnotationId)); break;
            }
        }
    }

    private void BuildPlanGraph()
    {
        PlanNodes.Clear(); PlanEdges.Clear();
        if (!_planReady) return;
        int? lvl = SelectedLevel;
        var byId = new Dictionary<string, System.Windows.Point>();
        foreach (var n in _nodes)
        {
            if (lvl is int L && n.Level != L) continue;
            var c = ToPx(new[] { n.DrawingX, n.DrawingY });
            byId[n.NodeId] = c;
            PlanNodes.Add(new PlanNodeVm(c.X - 6, c.Y - 6, c.X, c.Y, n.NodeId, n.NodeType));
        }
        foreach (var e in _edges)
        {
            if (!byId.TryGetValue(e.StartNodeId, out var a) || !byId.TryGetValue(e.EndNodeId, out var b)) continue;
            PlanEdges.Add(new PlanEdgeVm(new PointCollection { a, b }, e.EdgeId));
        }
    }

    /// <summary>클릭 px에서 임계(16px) 내 최근접 렌더 노드를 반환(엣지 연결용).</summary>
    private PlanNodeVm? PickNodeAt(double px, double py, double thresholdPx = 16)
    {
        PlanNodeVm? best = null; double bestD2 = thresholdPx * thresholdPx;
        foreach (var n in PlanNodes)
        {
            double d2 = (n.CenterX - px) * (n.CenterX - px) + (n.CenterY - py) * (n.CenterY - py);
            if (d2 <= bestD2) { bestD2 = d2; best = n; }
        }
        return best;
    }

    /// <summary>등록 요소 통합 목록 재구성(벽·이동불가·노드·엣지) → _allRows, 필터 적용.</summary>
    private void BuildElementRows()
    {
        _allRows.Clear();
        foreach (var a in _annotations.OrderBy(a => a.Level).ThenBy(a => a.Kind))
        {
            string label = a.Kind switch { "WALL" => "벽", "HAZARD" => "낙상 위험", _ => "이동불가" };
            _allRows.Add(new PlanElementRow(a.AnnotationId.ToString(), a.Kind, a.Name, $"L{a.Level} · {label} · {a.Points.Length}점"));
        }
        foreach (var n in _nodes.OrderBy(n => n.Level))
            _allRows.Add(new PlanElementRow(n.NodeId, "NODE", n.NodeId, $"L{n.Level} · 노드 · {n.NodeType} · ({n.X:0.#},{n.Y:0.#})"));
        foreach (var e in _edges.OrderBy(e => e.MapId))
            _allRows.Add(new PlanElementRow(e.EdgeId, "EDGE", e.EdgeId, $"엣지 · {e.EdgeType}{(e.Bidirectional ? " ↔" : " →")}"));
        ApplyElementFilter();
    }

    [RelayCommand]
    private async Task DeleteElementAsync(PlanElementRow? row)
    {
        if (row is null) return;
        try
        {
            switch (row.Category)
            {
                case "WALL": case "NOGO": case "HAZARD":
                    if (Guid.TryParse(row.Id, out var gid)) { await _api.DeleteMapAnnotationAsync(gid); await LoadAnnotationsAsync(); }
                    break;
                case "NODE": await _api.DeleteNodeAsync(row.Id); await LoadGraphAsync(); break;
                case "EDGE": await _api.DeleteEdgeAsync(row.Id); await LoadGraphAsync(); break;
            }
        }
        catch (Exception ex) { PlanToolHint = $"삭제 실패: {ex.Message}"; }
    }

    partial void OnActiveToolChanged(PlanTool value) => OnPropertyChanged(nameof(ToolActive));

    // ── 마우스 hover 좌표 리드아웃 ─────────────────────────────────────────
    [ObservableProperty] private bool _planHoverVisible;
    [ObservableProperty] private string _planHoverText = "";
    [ObservableProperty] private double _planHoverLabelX;
    [ObservableProperty] private double _planHoverLabelY;

    /// <summary>포인터 px → 도면 좌표(m) 리드아웃 + (도구 활성 시) 러버밴드. Shift=수평/수직 스냅.</summary>
    public void PlanHover(double px, double py, bool shift)
    {
        if (!_planReady) { PlanHoverVisible = false; return; }

        if (ToolActive && _pending.Count > 0)
        {
            // 스냅된 지점(= 실제 클릭이 놓일 위치)으로 러버밴드·리드아웃 일치
            var snapped = SnapDrawing(px, py, shift);
            var sp = ToPx(snapped);

            var pc = new PointCollection();
            foreach (var p in _pending) pc.Add(ToPx(p));
            pc.Add(sp);                       // 마지막 꼭짓점 → (스냅된) 포인터
            PlanDraft = pc;

            PlanHoverText = $"x={snapped[0]:0.##}, y={snapped[1]:0.##} m" + (shift ? "  ⇥ 정렬" : "");
            PlanHoverLabelX = sp.X + 12;
            PlanHoverLabelY = sp.Y + 6;
        }
        else
        {
            double dx = _pXMin + (px - _pOffX) / _pScale;
            double dy = _pYMax - (py - _pOffY) / _pScale;
            PlanHoverText = $"x={dx:0.##}, y={dy:0.##} m";
            PlanHoverLabelX = px + 12;   // 포인터 오른쪽
            PlanHoverLabelY = py + 6;
        }
        PlanHoverVisible = true;
    }

    public void PlanHoverLeave()
    {
        PlanHoverVisible = false;
        if (ToolActive) UpdateDraft();   // 포인터로 뻗은 러버밴드 제거(확정 점만 남김)
    }

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
        BuildPlanAnnotations();   // 벽·이동 불가 구역 렌더 갱신(뷰 변환 의존)
        BuildPlanGraph();         // 노드·엣지 렌더 갱신
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

    /// <summary>
    /// 2D 평면도 우클릭 "이 노드로 이동" — 우클릭 지점 임계 내 노드를 집어, 클릭 픽셀이 아니라
    /// 그 노드의 **등록 도면 좌표**로 선택 로봇에 이동 명령. 가드는 GotoHere와 동일(층·로봇). 경로는 HD_AMR.
    /// </summary>
    public async Task GotoNodeAsync(double canvasPxX, double canvasPxY)
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

        var picked = PickNodeAt(canvasPxX, canvasPxY);
        if (picked is null) { PlanGotoStatus = "노드 위에서 우클릭하세요(임계 16px)."; return; }
        var node = _nodes.FirstOrDefault(n => n.NodeId == picked.NodeId);
        if (node is null) { PlanGotoStatus = "노드 정보를 찾을 수 없습니다."; return; }

        string mapId = $"{TankId}-L{level}";
        try
        {
            await _api.GotoAsync(SelectedRobotId!, mapId, node.DrawingX, node.DrawingY, null, "operator");
            PlanGotoStatus = $"이동 명령 전송: {SelectedRobotId} → 노드 {node.NodeId} 도면({node.DrawingX:0.##}, {node.DrawingY:0.##}) @ {mapId}";
        }
        catch (Exception ex)
        {
            PlanGotoStatus = $"이동 불가: {ex.Message}";   // 층 불일치(409)·NOGO/HAZARD 등 서버 메시지 노출
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
            await LoadAnnotationsAsync();   // 벽·이동 불가 구역 로드
            await LoadGraphAsync();         // 노드·엣지 로드
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
