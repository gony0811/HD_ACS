using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Drawing;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 면 위 용접선·Corrugation 드로잉 도구의 상태·명령(프레임워크 중립). 각 UI 헤드의 캔버스가 렌더·포인터 입력을 담당하고,
/// 확정/편집/되돌리기/직렬화는 이 VM이 단일 원천으로 보유한다. 좌표·간격은 실좌표(mm).
/// 캔버스는 진행 중(anchor·cursor·수식자) 상태만 임시 보유하고, 두 번째 클릭에서 CommitLine/CommitGrid를 호출한다.
/// </summary>
public sealed partial class FaceDrawingViewModel : ObservableObject
{
    /// <summary>도형 목록 변경(확정·삭제·이동·되돌리기) — 캔버스가 구독해 재렌더.</summary>
    public event EventHandler? ShapesChanged;

    public FaceRect Face { get; }
    public string FaceCode { get; }

    /// <summary>면 실제 경계 정점(mm). 마구리(F/A)=팔각, 그 외=사각형. null이면 캔버스가 Face 사각형으로 폴백.</summary>
    public IReadOnlyList<Pt2>? Outline { get; }

    /// <summary>각 경계 변 너머에 있는 인접 면(변 중점·바깥 방향·코드). 캔버스가 변 바깥에 표시.</summary>
    public IReadOnlyList<FaceEdgeLabel> EdgeLabels { get; }

    /// <summary>면 꼭짓점 스냅·HUD 대상 정점 — 실제 윤곽(팔각 8정점 등), 없으면 사각형 4꼭짓점.
    /// 마구리(F/A) 팔각 면에서 챔퍼 모서리까지 스냅되도록 캔버스가 이 목록을 사용한다.</summary>
    public IReadOnlyList<Pt2> BoundaryVertices { get; }

    /// <summary>스냅 대상 꼭짓점이 사각형이 아닌 실제 윤곽(팔각)인지.</summary>
    public bool HasPolygonOutline { get; }

    public ObservableCollection<DrawnShape> Shapes { get; } = new();

    // 되돌리기/다시실행 — 도형 목록 스냅샷 스택(불변 record라 얕은 복사로 안전)
    private readonly Stack<List<DrawnShape>> _undo = new();
    private readonly Stack<List<DrawnShape>> _redo = new();

    [ObservableProperty] private DrawType _selectedType = DrawType.WeldLine;
    [ObservableProperty] private DrawMode _selectedMode = DrawMode.Line;
    [ObservableProperty] private double _pitchX = 100;
    [ObservableProperty] private double _pitchY = 100;

    // 커서 이동 단위(Step) — 면 좌하단(0,0) 기준 격자에 커서 반올림. 0이면 연속(자유) 이동.
    [ObservableProperty] private double _stepX;
    [ObservableProperty] private double _stepY;

    // 레이어 표시/숨김(타입별)
    [ObservableProperty] private bool _weldVisible = true;
    [ObservableProperty] private bool _corrugationVisible = true;

    // 용접선 교착점 표시(용접선×용접선 · 용접선×Corrugation 가로/세로)
    [ObservableProperty] private bool _showIntersections = true;

    // 편집(선택) 모드 — 켜면 좌클릭이 도형 선택·드래그 이동
    [ObservableProperty] private bool _editMode;
    [ObservableProperty] private DrawnShape? _selected;

    // HUD(캔버스가 커서 이동 때마다 갱신)
    [ObservableProperty] private string _hudRange = "";
    [ObservableProperty] private string _hudCount = "";
    [ObservableProperty] private string _hudCells = "";
    [ObservableProperty] private string _hudCursor = "";
    [ObservableProperty] private string _hudCorner = "";
    [ObservableProperty] private string _hudPoint = "";   // 확정 선 최근접 끝점 → 커서 거리
    [ObservableProperty] private string _hudSnap = "";
    [ObservableProperty] private string _hudWarning = "";
    [ObservableProperty] private string _hudIntersections = "";   // 교착점 개수(유형별)

    public bool TableMode => SelectedMode == DrawMode.Table;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public FaceDrawingViewModel(string faceCode, double widthMm, double heightMm,
        IReadOnlyList<Pt2>? outline = null, IReadOnlyList<FaceEdgeLabel>? edgeLabels = null)
    {
        FaceCode = faceCode;
        Face = new FaceRect(widthMm, heightMm);
        Outline = outline;
        EdgeLabels = edgeLabels ?? Array.Empty<FaceEdgeLabel>();
        HasPolygonOutline = outline is { Count: >= 3 };
        BoundaryVertices = HasPolygonOutline
            ? outline!.ToArray()
            : new[] { Face.BottomLeft, Face.BottomRight, Face.TopRight, Face.TopLeft };
    }

    partial void OnSelectedModeChanged(DrawMode value) => OnPropertyChanged(nameof(TableMode));

    partial void OnEditModeChanged(bool value)
    {
        if (!value) Selected = null;
    }

    partial void OnSelectedChanged(DrawnShape? value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ApplyPitchToSelectedCommand.NotifyCanExecuteChanged();
        ToggleSelectedTypeCommand.NotifyCanExecuteChanged();
    }

    // ── CAD(DXF) 연동 — 등록 CAD 선분 시드 · 수동 재분류 · 저장 콜백 ─────────────────

    /// <summary>이 면 CAD의 원본 파일명(표시용). 저장 콜백 시 함께 전달.</summary>
    public string? CadSourceFile { get; set; }

    /// <summary>수동 보정된 CAD 분류를 캐시+DB(ref.face_cad)로 반영하는 콜백 — CreateFaceDrawing이 연결.</summary>
    public Func<IReadOnlyList<FaceCadSeg>, Task>? PersistCad { get; set; }

    /// <summary>등록 CAD 선분(면-로컬 mm, 용접선/Corrugation 분류)을 도형으로 시드 — 되돌리기 이력 없이 초기 적재.</summary>
    public void SeedCad(IEnumerable<FaceCadSeg>? segs)
    {
        if (segs is null) return;
        foreach (var s in segs)
        {
            var a = new Pt2(s.Ax, s.Ay);
            var b = new Pt2(s.Bx, s.By);
            if (Dist(a, b) < 1e-6) continue;
            var type = string.Equals(s.Kind, nameof(DrawType.Corrugation), StringComparison.OrdinalIgnoreCase)
                ? DrawType.Corrugation : DrawType.WeldLine;
            Shapes.Add(new DrawnShape(Guid.NewGuid(), type, DrawMode.Line, a, b, 0, 0, 0, 0,
                new[] { DrawGeometry.BuildLine(a, b) }));
        }
        _undo.Clear();
        _redo.Clear();
        AfterChange();
    }

    /// <summary>선택 도형의 분류를 용접선↔Corrugation으로 전환(오분류 수동 보정).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleSelectedType()
    {
        if (Selected is not { } s) return;
        Replace(s, s with { Type = s.Type == DrawType.WeldLine ? DrawType.Corrugation : DrawType.WeldLine });
    }

    // ── Corrugation 연장 · 면 정보 미러링 ──────────────────────────────────────

    /// <summary>표(격자)의 끝라인을 면 경계(그림판 끝)까지 연장 — 세로선은 y[0,H], 가로선은 x[0,W] 전폭.
    /// 선택이 표면 그 표만, 아니면 모든 표에 적용. Corrugation 주름이 면 끝까지 이어지게 한다.</summary>
    [RelayCommand]
    private void ExtendGridsToFace()
    {
        var targets = Selected is { Mode: DrawMode.Table } sel
            ? new[] { sel }
            : Shapes.Where(s => s.Mode == DrawMode.Table).ToArray();
        if (targets.Length == 0) { HudWarning = "연장할 표(격자)가 없습니다 — 표 모드로 그린 뒤 사용하세요."; return; }

        _undo.Push(Snapshot());
        _redo.Clear();
        foreach (var s in targets)
        {
            int idx = Shapes.IndexOf(s);
            if (idx < 0) continue;
            Shapes[idx] = s with { Segments = ExtendToFace(s.Segments) };
        }
        Selected = null;
        AfterChange();
    }

    /// <summary>축정렬 격자선을 면 경계까지 연장(세로선=전체 높이, 가로선=전체 폭). 중복 x/y는 1개로.</summary>
    private IReadOnlyList<DrawSeg> ExtendToFace(IReadOnlyList<DrawSeg> segs)
    {
        var xs = new SortedSet<double>();
        var ys = new SortedSet<double>();
        foreach (var g in segs)
        {
            if (Math.Abs(g.A.X - g.B.X) < 1e-9) xs.Add(g.A.X);        // 세로선(x 고정)
            else if (Math.Abs(g.A.Y - g.B.Y) < 1e-9) ys.Add(g.A.Y);  // 가로선(y 고정)
        }
        var outp = new List<DrawSeg>(xs.Count + ys.Count);
        foreach (var x in xs) outp.Add(new DrawSeg(new Pt2(x, 0), new Pt2(x, Face.H)));
        foreach (var y in ys) outp.Add(new DrawSeg(new Pt2(0, y), new Pt2(Face.W, y)));
        return outp.Count > 0 ? outp : segs;
    }

    /// <summary>면 정보 좌우 반전(x축 W/2 기준 미러) — 대칭 면(좌현/우현 등) 작업용.</summary>
    [RelayCommand]
    private void MirrorHorizontal() => MirrorAll(mirrorX: true);

    /// <summary>면 정보 상하 반전(y축 H/2 기준 미러).</summary>
    [RelayCommand]
    private void MirrorVertical() => MirrorAll(mirrorX: false);

    private void MirrorAll(bool mirrorX)
    {
        if (Shapes.Count == 0) { HudWarning = "미러링할 도형이 없습니다."; return; }
        _undo.Push(Snapshot());
        _redo.Clear();
        for (int i = 0; i < Shapes.Count; i++) Shapes[i] = MirrorShape(Shapes[i], mirrorX);
        Selected = null;
        AfterChange();
    }

    private DrawnShape MirrorShape(DrawnShape s, bool mirrorX)
    {
        Pt2 M(Pt2 p) => mirrorX ? new Pt2(Face.W - p.X, p.Y) : new Pt2(p.X, Face.H - p.Y);
        return s with
        {
            Anchor = M(s.Anchor),
            Extent = M(s.Extent),
            Segments = s.Segments.Select(g => new DrawSeg(M(g.A), M(g.B))).ToArray(),
        };
    }

    /// <summary>현재 도형 분류를 DB(ref.face_cad)에 반영. 서버 미연결 시 캐시에만 보존(프로젝트 저장 시 재시도).</summary>
    [RelayCommand]
    private async Task SaveCad()
    {
        var list = new List<FaceCadSeg>();
        foreach (var sh in Shapes)
            foreach (var g in sh.Segments)
                list.Add(new FaceCadSeg(g.A.X, g.A.Y, g.B.X, g.B.Y, sh.Type.ToString()));
        if (PersistCad is null) { HudWarning = "이 면은 CAD 저장 대상이 아닙니다."; return; }
        try
        {
            await PersistCad(list);
            HudWarning = $"CAD 분류 DB 저장됨 — 선분 {list.Count}개.";
        }
        catch (Exception ex)
        {
            HudWarning = $"CAD 분류를 캐시에 반영했으나 서버 저장 실패: {ex.Message} (관제 서버 확인 후 프로젝트 저장 시 재시도).";
        }
    }

    // ── 확정 ────────────────────────────────────────────────────────────────

    /// <summary>선 모드 확정 — 시작·끝(mm)으로 단일 선분 도형 추가.</summary>
    public DrawnShape? CommitLine(Pt2 a, Pt2 b)
    {
        if (Dist(a, b) < 1e-6) return null;   // 영길이 무시
        var shape = new DrawnShape(Guid.NewGuid(), SelectedType, DrawMode.Line, a, b,
            0, 0, 0, 0, new[] { DrawGeometry.BuildLine(a, b) });
        Push(() => Shapes.Add(shape));
        return shape;
    }

    /// <summary>표 모드 확정 — 기준점·커서(mm)와 현재 간격으로 격자 도형 추가. 온전한 칸 0이면 무시, 상한 초과면 거부.</summary>
    public DrawnShape? CommitGrid(Pt2 anchor, Pt2 cursor)
    {
        var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(anchor, cursor, PitchX, PitchY);
        if (segs.Count == 0) { HudWarning = "온전한 칸이 없습니다 — 범위를 간격보다 크게 잡으세요."; return null; }
        if (segs.Count > DrawGeometry.MaxGridLines)
        { HudWarning = $"선이 너무 많습니다({segs.Count}) — 간격을 키우세요."; return null; }
        var shape = new DrawnShape(Guid.NewGuid(), SelectedType, DrawMode.Table, anchor, cursor,
            PitchX, PitchY, cellsX, cellsY, segs);
        Push(() => Shapes.Add(shape));
        return shape;
    }

    /// <summary>끝점 스냅 대상 — 확정된 모든 도형의 선분 양 끝점(mm).</summary>
    public IReadOnlyList<Pt2> Endpoints()
    {
        var pts = new List<Pt2>();
        foreach (var s in Shapes)
        {
            if (!IsVisible(s.Type)) continue;
            foreach (var g in s.Segments) { pts.Add(g.A); pts.Add(g.B); }
        }
        return pts;
    }

    /// <summary>
    /// 모든 용접선 교착점 — ① 용접선×용접선(서로 다른 도형) ② 용접선×Corrugation(가로) ③ 용접선×Corrugation(세로).
    /// 가시 레이어만 대상. 같은 유형·같은 위치는 1개로 합침(중복 제거).
    /// </summary>
    public IReadOnlyList<WeldIntersection> Intersections()
    {
        var result = new List<WeldIntersection>();
        bool weldOn = IsVisible(DrawType.WeldLine), corrOn = IsVisible(DrawType.Corrugation);
        if (!weldOn) return result;

        var welds = Shapes.Where(s => s.Type == DrawType.WeldLine).ToList();
        var corrs = corrOn ? Shapes.Where(s => s.Type == DrawType.Corrugation).ToList() : new List<DrawnShape>();

        // ① 용접선 × 용접선 — 서로 다른 도형끼리만(같은 도형 내부 선분 접점 제외)
        for (int i = 0; i < welds.Count; i++)
            for (int j = i + 1; j < welds.Count; j++)
                foreach (var a in welds[i].Segments)
                    foreach (var b in welds[j].Segments)
                        if (DrawGeometry.SegmentIntersection(a, b) is { } p)
                            AddIntersection(result, p, IntersectionKind.WeldWeld);

        // ②③ 용접선 × Corrugation(가로/세로 구분)
        foreach (var w in welds)
            foreach (var c in corrs)
                foreach (var a in w.Segments)
                    foreach (var b in c.Segments)
                        if (DrawGeometry.SegmentIntersection(a, b) is { } p)
                            AddIntersection(result, p,
                                DrawGeometry.IsHorizontal(b) ? IntersectionKind.WeldCorrugationH : IntersectionKind.WeldCorrugationV);

        return result;
    }

    private static void AddIntersection(List<WeldIntersection> list, Pt2 p, IntersectionKind kind)
    {
        foreach (var e in list)
            if (e.Kind == kind && Math.Abs(e.Point.X - p.X) < 1e-6 && Math.Abs(e.Point.Y - p.Y) < 1e-6) return;
        list.Add(new WeldIntersection(p, kind));
    }

    /// <summary>최근 계산된 교착점 캐시 — 캔버스가 매 프레임 재계산하지 않도록(밀집 CAD 면 성능 보호). 도형/가시성 변경 시만 갱신.</summary>
    public IReadOnlyList<WeldIntersection> IntersectionList { get; private set; } = System.Array.Empty<WeldIntersection>();

    private void RecomputeIntersections()
    {
        var xs = Intersections();
        IntersectionList = xs;
        if (xs.Count == 0) { HudIntersections = ""; return; }
        int ww = xs.Count(x => x.Kind == IntersectionKind.WeldWeld);
        int ch = xs.Count(x => x.Kind == IntersectionKind.WeldCorrugationH);
        int cv = xs.Count(x => x.Kind == IntersectionKind.WeldCorrugationV);
        HudIntersections = $"교착점 {xs.Count}  (용접×용접 {ww} · 용접×Corr가로 {ch} · 용접×Corr세로 {cv})";
    }

    partial void OnWeldVisibleChanged(bool value) => RecomputeIntersections();
    partial void OnCorrugationVisibleChanged(bool value) => RecomputeIntersections();
    partial void OnShowIntersectionsChanged(bool value) { OnPropertyChanged(nameof(ShowIntersections)); RecomputeIntersections(); }

    // ── 편집 ────────────────────────────────────────────────────────────────

    /// <summary>점(mm)에서 허용 오차 내 최근접 도형(가시 레이어 한정).</summary>
    public DrawnShape? HitTest(Pt2 p, double tolMm)
    {
        DrawnShape? best = null; double bestD = tolMm;
        foreach (var s in Shapes)
        {
            if (!IsVisible(s.Type)) continue;
            foreach (var seg in s.Segments)
            {
                double d = DrawGeometry.DistanceToSegment(p, seg);
                if (d <= bestD) { bestD = d; best = s; }
            }
        }
        return best;
    }

    public bool IsVisible(DrawType t) => t == DrawType.WeldLine ? WeldVisible : CorrugationVisible;

    /// <summary>선택 도형을 델타(mm)만큼 이동(드래그 종료 시 1회 — 되돌리기 1스텝).</summary>
    public void MoveSelected(double dx, double dy)
    {
        if (Selected is not { } s) return;
        var moved = s with
        {
            Anchor = DrawGeometry.Translate(s.Anchor, dx, dy),
            Extent = DrawGeometry.Translate(s.Extent, dx, dy),
            Segments = s.Segments
                .Select(g => new DrawSeg(DrawGeometry.Translate(g.A, dx, dy), DrawGeometry.Translate(g.B, dx, dy)))
                .ToArray(),
        };
        Replace(s, moved);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (Selected is not { } s) return;
        Push(() => Shapes.Remove(s));
        Selected = null;
    }

    /// <summary>선택된 표 도형의 간격을 현재 PitchX/Y로 재조정(격자 재계산).</summary>
    [RelayCommand(CanExecute = nameof(CanReapplyPitch))]
    private void ApplyPitchToSelected()
    {
        if (Selected is not { Mode: DrawMode.Table } s) return;
        var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(s.Anchor, s.Extent, PitchX, PitchY);
        if (segs.Count == 0) { HudWarning = "온전한 칸이 없습니다 — 범위를 간격보다 크게 잡으세요."; return; }
        if (segs.Count > DrawGeometry.MaxGridLines) { HudWarning = "선이 너무 많습니다 — 간격을 키우세요."; return; }
        Replace(s, s with { PitchX = PitchX, PitchY = PitchY, CountX = cellsX, CountY = cellsY, Segments = segs });
    }

    private bool HasSelection() => Selected is not null;
    private bool CanReapplyPitch() => Selected is { Mode: DrawMode.Table };

    // ── 되돌리기 / 다시실행 ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (Shapes.Count == 0) return;
        Push(() => Shapes.Clear());
        Selected = null;
    }

    // ── 직렬화 ────────────────────────────────────────────────────────────────

    private sealed record ShapeDto(
        [property: JsonPropertyName("타입")] string Type,
        [property: JsonPropertyName("모드")] string Mode,
        [property: JsonPropertyName("기준점")] double[] Anchor,
        [property: JsonPropertyName("끝점")] double[] Extent,
        [property: JsonPropertyName("방향")] double[] Direction,
        [property: JsonPropertyName("X간격")] double PitchX,
        [property: JsonPropertyName("Y간격")] double PitchY,
        [property: JsonPropertyName("개수")] int[] Count);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>확정 도형을 {타입,모드,기준점,방향,X간격,Y간격,개수} 계약으로 직렬화(후속 로봇 경로 생성 입력).</summary>
    public string SerializeJson()
    {
        var dtos = Shapes.Select(s => new ShapeDto(
            s.Type.ToString(), s.Mode.ToString(),
            new[] { s.Anchor.X, s.Anchor.Y }, new[] { s.Extent.X, s.Extent.Y },
            new[] { s.Direction.X, s.Direction.Y }, s.PitchX, s.PitchY,
            new[] { s.CountX, s.CountY })).ToList();
        var xs = Intersections()
            .Select(x => new { 유형 = x.Kind.ToString(), 위치 = new[] { x.Point.X, x.Point.Y } })
            .ToList();
        return JsonSerializer.Serialize(new { 면 = FaceCode, 폭 = Face.W, 높이 = Face.H, 도형 = dtos, 교착점 = xs }, JsonOpts);
    }

    // ── 내부: 스냅샷/되돌리기 배관 ─────────────────────────────────────────────

    private List<DrawnShape> Snapshot() => Shapes.ToList();

    /// <summary>변경 전 스냅샷을 undo에 쌓고, redo를 비운 뒤 변경 실행.</summary>
    private void Push(Action mutate)
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        mutate();
        AfterChange();
    }

    private void Replace(DrawnShape oldShape, DrawnShape newShape)
    {
        int idx = Shapes.IndexOf(oldShape);
        if (idx < 0) return;
        _undo.Push(Snapshot());
        _redo.Clear();
        Shapes[idx] = newShape;
        if (ReferenceEquals(Selected, oldShape) || Equals(Selected, oldShape)) Selected = newShape;
        AfterChange();
    }

    private void Restore(List<DrawnShape> snap)
    {
        Shapes.Clear();
        foreach (var s in snap) Shapes.Add(s);
        Selected = null;
        AfterChange();
    }

    private void AfterChange()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        RecomputeIntersections();
        ShapesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double Dist(Pt2 a, Pt2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
