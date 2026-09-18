using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Desktop.Infrastructure;
using HD.Acs.UI.Drawing;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 면 드로잉 캔버스 — 실좌표(mm) 도형을 DrawingContext로 그리고 포인터/키보드 입력을 FaceDrawingViewModel로 연결한다.
/// 화면은 zoom/pan 배율 변환만 담당(모델은 항상 mm). 좌하단 원점·y 위로 증가.
/// 조작: 좌클릭=그리기(선 2클릭 / 표 2클릭) · (편집 모드) 좌클릭 선택·드래그 이동 · 우/중드래그=팬 · 휠=줌.
/// 보조: ALT=엣지 스냅 · SHIFT=직교 · ALT+SHIFT=직교 후 엣지 · ESC=취소 · Ctrl+Z/Y=되돌리기 · Del=삭제.
/// </summary>
public sealed class FaceDrawCanvas : Control
{
    private const double Pad = 28;
    private const int PreviewLineCap = 1200;   // 미리보기 성능 보호 상한

    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.Parse("#1B2631"));
    private static readonly Rgba WeldColor = Rgba.FromRgb(0xE6, 0x7E, 0x22);
    private static readonly Rgba CorrColor = Rgba.FromRgb(0x3D, 0x9B, 0xE9);
    private static readonly Rgba SelColor = Rgba.FromRgb(0xF1, 0xC4, 0x0F);
    private static readonly Rgba FaceLine = Rgba.FromRgb(0x5D, 0x6D, 0x7E);
    private static readonly Rgba Guide = Rgba.FromRgb(0xEC, 0xF0, 0xF1);
    private static readonly Rgba PointGuide = Rgba.FromRgb(0x1A, 0xBC, 0x9C);   // 확정 선 끝점→커서 거리(청록)
    private static readonly Rgba EdgeLabelColor = Rgba.FromRgb(0xF5, 0xF7, 0xF8);
    private static readonly Rgba XWeldWeld = Rgba.FromRgb(0xE7, 0x4C, 0x3C);    // 교착점: 용접선×용접선(적)
    private static readonly Rgba XCorrH = Rgba.FromRgb(0x2E, 0xCC, 0x71);       // 교착점: 용접선×Corr 가로(녹)
    private static readonly Rgba XCorrV = Rgba.FromRgb(0x9B, 0x59, 0xB6);       // 교착점: 용접선×Corr 세로(보라)

    private readonly Typeface _typeface = new("Segoe UI, Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, sans-serif");
    private readonly Dictionary<Rgba, IImmutableBrush> _brushes = new();

    private FaceDrawingViewModel? _vm;
    private double _zoom = 1;
    private Point _pan;
    private bool _fitPending = true;

    // 진행 중(임시) 상태 — 확정 전
    private Pt2? _anchor;         // 그리기 1클릭
    private Pt2 _rawCursor;       // 최근 커서(모델, 수식자 적용 전)
    private bool _hasCursor;
    private bool _panning;
    private Point _lastPointer;

    // 편집 드래그
    private DrawnShape? _dragShape;
    private Pt2 _dragStart, _dragCurrent;
    private bool _dragging;

    public FaceDrawCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) => Attach(DataContext as FaceDrawingViewModel);
    }

    private void Attach(FaceDrawingViewModel? vm)
    {
        if (_vm is not null) { _vm.ShapesChanged -= OnShapes; _vm.PropertyChanged -= OnVmProp; }
        _vm = vm;
        if (_vm is not null) { _vm.ShapesChanged += OnShapes; _vm.PropertyChanged += OnVmProp; }
        _fitPending = true;
        InvalidateVisual();
    }

    private void OnShapes(object? sender, EventArgs e) => InvalidateVisual();

    private void OnVmProp(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FaceDrawingViewModel.WeldVisible) or nameof(FaceDrawingViewModel.CorrugationVisible)
            or nameof(FaceDrawingViewModel.Selected) or nameof(FaceDrawingViewModel.SelectedMode)
            or nameof(FaceDrawingViewModel.SelectedType) or nameof(FaceDrawingViewModel.EditMode)
            or nameof(FaceDrawingViewModel.ShowIntersections))
        {
            if (e.PropertyName is nameof(FaceDrawingViewModel.EditMode) or nameof(FaceDrawingViewModel.SelectedMode))
                CancelDraw();
            InvalidateVisual();
        }
    }

    // ── 좌표 변환(mm ↔ px) ──────────────────────────────────────────────
    private Point ToScreen(Pt2 m) => new(_pan.X + m.X * _zoom, _pan.Y - m.Y * _zoom);
    private Pt2 ToModel(Point s) => new((s.X - _pan.X) / _zoom, (_pan.Y - s.Y) / _zoom);

    private void Fit()
    {
        if (_vm is not { } vm) return;
        var b = Bounds;
        if (b.Width < 1 || b.Height < 1 || vm.Face.W <= 0 || vm.Face.H <= 0) return;

        // 콘텐츠 범위 = 실제 윤곽(팔각 포함) bbox, 없으면 Face 사각형. 변 바깥 라벨 여백(Pad*2)까지 고려.
        double minX = 0, minY = 0, maxX = vm.Face.W, maxY = vm.Face.H;
        if (vm.Outline is { Count: >= 3 } o)
        {
            minX = o.Min(p => p.X); minY = o.Min(p => p.Y);
            maxX = o.Max(p => p.X); maxY = o.Max(p => p.Y);
        }
        // 등록 CAD 선분이 파라미터 면 범위를 넘어갈 수 있으므로 도형 bbox까지 포함해 모두 보이게 맞춤
        foreach (var s in vm.Shapes)
            foreach (var g in s.Segments)
            {
                minX = Math.Min(minX, Math.Min(g.A.X, g.B.X)); minY = Math.Min(minY, Math.Min(g.A.Y, g.B.Y));
                maxX = Math.Max(maxX, Math.Max(g.A.X, g.B.X)); maxY = Math.Max(maxY, Math.Max(g.A.Y, g.B.Y));
            }
        double cw = Math.Max(1e-6, maxX - minX), ch = Math.Max(1e-6, maxY - minY);
        _zoom = Math.Min((b.Width - 4 * Pad) / cw, (b.Height - 4 * Pad) / ch);
        if (_zoom <= 0 || double.IsInfinity(_zoom)) _zoom = 1;
        double ccx = (minX + maxX) / 2, ccy = (minY + maxY) / 2;
        _pan = new Point(b.Width / 2 - ccx * _zoom, b.Height / 2 + ccy * _zoom);
        _fitPending = false;
    }

    /// <summary>전체 형상에 맞춤(툴바 버튼).</summary>
    public void ZoomToFit() { _fitPending = true; InvalidateVisual(); }

    // ── 렌더 ────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Background, new Rect(0, 0, b.Width, b.Height));
        if (b.Width < 1 || b.Height < 1 || _vm is not { } vm) return;
        if (_fitPending) Fit();

        // 면 경계 — 마구리(F/A)=팔각 윤곽, 그 외=사각형
        DrawFaceBoundary(ctx, vm);
        // 각 변 너머 인접 면 표시(화살표 + 코드) — 지금 어느 면을 수정 중인지 안내
        DrawEdgeLabels(ctx, vm);

        // 확정 도형(가시 레이어)
        foreach (var s in vm.Shapes)
        {
            if (!vm.IsVisible(s.Type)) continue;
            bool sel = ReferenceEquals(vm.Selected, s) || Equals(vm.Selected, s);
            var draw = (_dragging && ReferenceEquals(_dragShape, s))
                ? Translate(s, _dragCurrent.X - _dragStart.X, _dragCurrent.Y - _dragStart.Y)
                : s;
            DrawShape(ctx, draw, sel, dashed: s.Type == DrawType.Corrugation, preview: false);
        }

        // 용접선 교착점(용접선×용접선 · 용접선×Corrugation 가로/세로) — 캐시 사용(매 프레임 재계산 방지)
        if (vm.ShowIntersections)
            foreach (var x in vm.IntersectionList)
                DrawIntersection(ctx, x);

        if (_hasCursor)
        {
            var eff = ResolveCursor();

            // 미리보기(진행 중 그리기)
            if (!vm.EditMode && _anchor is { } a)
            {
                var segs = vm.SelectedMode == DrawMode.Line
                    ? new List<DrawSeg> { DrawGeometry.BuildLine(a, eff) }
                    : DrawGeometry.BuildGrid(a, eff, vm.PitchX, vm.PitchY).Segs.ToList();
                var color = vm.SelectedType == DrawType.WeldLine ? WeldColor : CorrColor;
                if (segs.Count == 0)   // 표: 온전한 칸 없음 → 자투리 경계만 옅게
                { if (vm.SelectedMode == DrawMode.Table) DrawPreviewBounds(ctx, a, eff, color); }
                else if (segs.Count <= PreviewLineCap)
                    foreach (var g in segs) DrawSegPx(ctx, g, color with { A = 0x99 }, 1.4, dashed: true);
                else   // 성능 보호 — 개별선 생략, 경계 박스만
                    DrawPreviewBounds(ctx, a, eff, color);
                DrawMarker(ctx, a, WeldColor);
            }

            // 최근접 꼭짓점 보조선 + 라벨 (팔각 면은 실제 윤곽 정점까지 인식)
            var nc = NearestVertex(eff, vm);
            DrawSegPx(ctx, new DrawSeg(eff, nc.Point), Guide with { A = 0x88 }, 1.0, dashed: true);
            DrawMarker(ctx, nc.Point, Guide);
            var mid = ToScreen(new Pt2((eff.X + nc.Point.X) / 2, (eff.Y + nc.Point.Y) / 2));
            DrawText(ctx, $"{nc.Label} Δ({nc.Dx:F0},{nc.Dy:F0})", mid.X + 6, mid.Y - 6, Guide, 11);

            // 확정 선(도형)의 최근접 끝점 → 커서 거리 보조선 + 라벨
            if (DrawGeometry.Nearest(eff, vm.Endpoints()) is { } np)
            {
                DrawSegPx(ctx, new DrawSeg(eff, np.Point), PointGuide with { A = 0xAA }, 1.0, dashed: true);
                DrawMarker(ctx, np.Point, PointGuide);
                var pm = ToScreen(new Pt2((eff.X + np.Point.X) / 2, (eff.Y + np.Point.Y) / 2));
                DrawText(ctx, $"점 {np.Distance:F0}mm", pm.X + 6, pm.Y + 6, PointGuide, 11);
            }

            // 스냅 흡착 하이라이트(끝점/꼭짓점)
            if (_snapHi is { } hi)
            {
                var hp = ToScreen(hi);
                ctx.DrawEllipse(null, Pen(SelColor, 1.8), hp, 7, 7);
            }
        }
    }

    /// <summary>면 경계 렌더 — Outline(F/A=팔각, 그 외=사각형) 폴리곤, 없으면 Face 사각형 폴백.</summary>
    private void DrawFaceBoundary(DrawingContext ctx, FaceDrawingViewModel vm)
    {
        var fill = Brush(Rgba.FromArgb(0x12, 0x2E, 0x86, 0xC1));
        var pen = Pen(FaceLine, 1.4);
        if (vm.Outline is { Count: >= 3 } outline)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                var p0 = ToScreen(outline[0]);
                g.BeginFigure(new Point(p0.X, p0.Y), true);
                for (int i = 1; i < outline.Count; i++) { var p = ToScreen(outline[i]); g.LineTo(new Point(p.X, p.Y)); }
                g.EndFigure(true);
            }
            ctx.DrawGeometry(fill, pen, geo);
        }
        else
        {
            var tl = ToScreen(vm.Face.TopLeft); var br = ToScreen(vm.Face.BottomRight);
            ctx.DrawRectangle(fill, pen, new Rect(tl.X, tl.Y, Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y)));
        }
    }

    /// <summary>각 경계 변 바깥에 인접 면 화살표 + 코드 표시(그림 예시처럼). 화면 y는 뒤집힘.</summary>
    private void DrawEdgeLabels(DrawingContext ctx, FaceDrawingViewModel vm)
    {
        var pen = Pen(EdgeLabelColor, 2);
        foreach (var lab in vm.EdgeLabels)
        {
            var m = ToScreen(lab.Mid);
            double dx = lab.OutwardDir.X, dy = -lab.OutwardDir.Y;   // 모델 y↑ → 화면 y↓
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 0; dy = -1; } else { dx /= len; dy /= len; }
            var tail = new Point(m.X + dx * 8, m.Y + dy * 8);
            var tip = new Point(m.X + dx * 30, m.Y + dy * 30);
            ctx.DrawLine(pen, tail, tip);
            double px = -dy, py = dx;   // 화살촉
            ctx.DrawLine(pen, tip, new Point(tip.X - dx * 8 + px * 5, tip.Y - dy * 8 + py * 5));
            ctx.DrawLine(pen, tip, new Point(tip.X - dx * 8 - px * 5, tip.Y - dy * 8 - py * 5));
            var ft = new FormattedText(lab.Code, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, 14, Brush(EdgeLabelColor));
            ctx.DrawText(ft, new Point(tip.X + dx * 10 - ft.Width / 2, tip.Y + dy * 10 - ft.Height / 2));
        }
    }

    private void DrawShape(DrawingContext ctx, DrawnShape s, bool selected, bool dashed, bool preview)
    {
        var color = s.Type == DrawType.WeldLine ? WeldColor : CorrColor;
        if (selected)
            foreach (var g in s.Segments) DrawSegPx(ctx, g, SelColor, 4.0, dashed: false);   // 강조 하이라이트 뒤
        double th = s.Type == DrawType.WeldLine ? 2.0 : 1.5;
        foreach (var g in s.Segments) DrawSegPx(ctx, g, preview ? color with { A = 0x99 } : color, th, dashed);
    }

    private void DrawPreviewBounds(DrawingContext ctx, Pt2 a, Pt2 c, Rgba color)
    {
        var p0 = ToScreen(new Pt2(Math.Min(a.X, c.X), Math.Max(a.Y, c.Y)));
        var p1 = ToScreen(new Pt2(Math.Max(a.X, c.X), Math.Min(a.Y, c.Y)));
        ctx.DrawRectangle(null, Pen(color with { A = 0xAA }, 1.2, dashed: true),
            new Rect(p0.X, p0.Y, Math.Abs(p1.X - p0.X), Math.Abs(p1.Y - p0.Y)));
    }

    private void DrawSegPx(DrawingContext ctx, DrawSeg g, Rgba color, double thickness, bool dashed)
        => ctx.DrawLine(Pen(color, thickness, dashed), ToScreen(g.A), ToScreen(g.B));

    /// <summary>교착점 마커 — 유형별 색의 속찬 원 + 흰 테두리(도형선 위에서 또렷하게).</summary>
    private void DrawIntersection(DrawingContext ctx, WeldIntersection x)
    {
        var color = x.Kind switch
        {
            IntersectionKind.WeldWeld => XWeldWeld,
            IntersectionKind.WeldCorrugationH => XCorrH,
            _ => XCorrV,
        };
        var p = ToScreen(x.Point);
        ctx.DrawEllipse(Brush(color), Pen(Guide, 1.4), p, 4.5, 4.5);
    }

    private void DrawMarker(DrawingContext ctx, Pt2 m, Rgba color)
    {
        var p = ToScreen(m);
        ctx.DrawEllipse(Brush(color), null, p, 3.5, 3.5);
    }

    private void DrawText(DrawingContext ctx, string text, double x, double y, Rgba color, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size, Brush(color));
        ctx.FillRectangle(Brush(Rgba.FromArgb(0xC0, 0x12, 0x1B, 0x22)), new Rect(x - 3, y - 2, ft.Width + 6, ft.Height + 4));
        ctx.DrawText(ft, new Point(x, y));
    }

    private static DrawnShape Translate(DrawnShape s, double dx, double dy) => s with
    {
        Anchor = DrawGeometry.Translate(s.Anchor, dx, dy),
        Extent = DrawGeometry.Translate(s.Extent, dx, dy),
        Segments = s.Segments.Select(g => new DrawSeg(DrawGeometry.Translate(g.A, dx, dy), DrawGeometry.Translate(g.B, dx, dy))).ToArray(),
    };

    // ── 수식자 적용 커서 + 스냅 우선순위 ─────────────────────────────────
    // 이동단위(Step)를 켜면 커서가 Step 격자 단위로만 움직이게 **Step을 최우선**으로 둔다
    // (끝점·꼭짓점 자석이 Step을 밀어내던 문제 해소). 명시적 키만 Step보다 앞: Ctrl=전 스냅 해제 · ALT=엣지 스냅.
    // Step이 꺼져 있을 때만 끝점 → 꼭짓점 자석이 동작. SHIFT(직교)는 어느 경우에도 축 구속으로 합성.
    private Pt2 _effCursor;
    private string _snapKind = "";
    private Pt2? _snapHi;   // 흡착 대상 하이라이트(끝점/꼭짓점)

    private Pt2 EffectiveCursor() => ResolveCursor();

    private Pt2 ResolveCursor()
    {
        if (_vm is not { } vm) { _snapKind = ""; _snapHi = null; return _rawCursor; }
        _snapHi = null; _snapKind = "";
        Pt2 raw = _rawCursor;

        if (LastCtrl) { _snapKind = "해제(Ctrl)"; return _effCursor = vm.Face.Clamp(raw); }

        double tol = _zoom > 0 ? 10.0 / _zoom : 10.0;   // 화면 10px → mm (zoom 무관 고정)
        Pt2? from = _anchor;
        bool shift = LastShift && from is not null;
        Pt2 basePt = shift ? DrawGeometry.OrthoConstrain(from!.Value, raw) : raw;   // SHIFT 직교 기준

        // 1) ALT 엣지 스냅 (명시적 키)
        if (LastAlt)
        {
            Pt2 p = shift ? DrawGeometry.OrthoThenSnap(from!.Value, raw, vm.Face)
                          : DrawGeometry.SnapToNearestEdge(basePt, vm.Face);
            _snapKind = "엣지"; return _effCursor = vm.Face.Clamp(p);
        }

        // 2) Step 격자 스냅 — 켜져 있으면 커서를 Step 단위로 이동(끝점/꼭짓점보다 우선)
        if (vm.StepX > 0 || vm.StepY > 0)
        {
            // Step 격자 원점 = 가장 가까운 확정 끝점(있으면). 그 점으로부터 이동단위 배수 거리에 점을 찍는다
            // (다음 선을 기존 점 기준으로 정확한 거리에 시작/끝내기). 확정 점이 없으면 면 좌하단(0,0) 기준.
            var ends = vm.Endpoints();
            Pt2 origin = DrawGeometry.Nearest(basePt, ends) is { } np0 ? np0.Point : default;
            Pt2 s = DrawGeometry.StepSnap(basePt, vm.StepX, vm.StepY, origin);
            if (shift)   // 직교 축 유지 — 자유 축만 Step
            {
                bool horizontal = Math.Abs(basePt.Y - from!.Value.Y) < 1e-9;
                s = horizontal ? new Pt2(s.X, from.Value.Y) : new Pt2(from!.Value.X, s.Y);
            }
            _snapKind = ends.Count > 0 ? "Step(점 기준)" : "Step";
            return _effCursor = vm.Face.Clamp(s);
        }

        // 3) 끝점 스냅 (Step 꺼짐)
        if (DrawGeometry.NearestWithin(basePt, vm.Endpoints(), tol) is { } ep
            && (shift ? FixOrtho(from!.Value, ep, basePt, tol) : ep) is { } q1)
        { _snapHi = ep; _snapKind = "끝점"; return _effCursor = vm.Face.Clamp(q1); }

        // 4) 면 꼭짓점 스냅 (Step 꺼짐) — 팔각 면은 실제 윤곽 정점(챔퍼 모서리)까지 대상
        var nc = NearestVertex(basePt, vm);
        if (nc.Distance <= tol
            && (shift ? FixOrtho(from!.Value, nc.Point, basePt, tol) : nc.Point) is { } q2)
        { _snapHi = nc.Point; _snapKind = "꼭짓점"; return _effCursor = vm.Face.Clamp(q2); }

        // 5) SHIFT 직교만
        _snapKind = shift ? "직교" : "";
        return _effCursor = vm.Face.Clamp(basePt);
    }

    /// <summary>
    /// 면 꼭짓점(스냅·HUD) 최근접 — 마구리(F/A) 팔각 면은 실제 윤곽 정점(챔퍼 8모서리)까지,
    /// 그 외 사각형 면은 4꼭짓점(방향 라벨 유지). 항상 최소 1개 정점이 있으므로 null 아님.
    /// </summary>
    private static (Pt2 Point, double Dx, double Dy, double Distance, string Label) NearestVertex(Pt2 p, FaceDrawingViewModel vm)
    {
        if (vm.HasPolygonOutline && DrawGeometry.NearestVertex(p, vm.BoundaryVertices) is { } v)
            return (v.Point, v.Dx, v.Dy, v.Distance, "꼭짓점");
        var nc = DrawGeometry.FindNearestCorner(p, vm.Face);
        return (nc.Point, nc.Dx, nc.Dy, nc.Distance, nc.Label);
    }

    /// <summary>SHIFT 직교 축을 유지한 채, 후보 점이 그 축 임계값 내면 흡착(자유 축만 이동). 아니면 null.</summary>
    private static Pt2? FixOrtho(Pt2 from, Pt2 c, Pt2 basePt, double tol)
    {
        bool horizontal = Math.Abs(basePt.Y - from.Y) < 1e-9;   // y 고정(수평)
        if (horizontal) return Math.Abs(c.Y - from.Y) <= tol ? new Pt2(c.X, from.Y) : null;
        return Math.Abs(c.X - from.X) <= tol ? new Pt2(from.X, c.Y) : null;
    }

    private bool LastAlt, LastShift, LastCtrl;

    // ── 포인터 ───────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_vm is not { } vm) return;
        var pt = e.GetCurrentPoint(this);
        _lastPointer = pt.Position;
        UpdateModifiers(e.KeyModifiers);

        if (pt.Properties.IsLeftButtonPressed)
        {
            _rawCursor = ToModel(pt.Position); _hasCursor = true;
            var eff = EffectiveCursor();
            if (vm.EditMode)
            {
                var hit = vm.HitTest(eff, HitTolMm());
                vm.Selected = hit;
                if (hit is not null) { _dragShape = hit; _dragStart = _dragCurrent = eff; _dragging = false; }
            }
            else if (_anchor is null)
            {
                _anchor = eff;   // 1클릭: 기준점 확정
            }
            else
            {
                if (vm.SelectedMode == DrawMode.Line) vm.CommitLine(_anchor.Value, eff);
                else vm.CommitGrid(_anchor.Value, eff);
                _anchor = null;   // 2클릭: 확정 후 리셋
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else   // 우/중 = 팬
        {
            _panning = true;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is not { } vm) return;
        var p = e.GetPosition(this);
        UpdateModifiers(e.KeyModifiers);
        _rawCursor = ToModel(p); _hasCursor = true;

        if (_panning && ReferenceEquals(e.Pointer.Captured, this))
        {
            _pan = new Point(_pan.X + (p.X - _lastPointer.X), _pan.Y + (p.Y - _lastPointer.Y));
            _lastPointer = p;
            InvalidateVisual();
            return;
        }
        if (_dragShape is not null && ReferenceEquals(e.Pointer.Captured, this))
        {
            _dragCurrent = EffectiveCursor();
            if (!_dragging && Dist(_dragStart, _dragCurrent) > HitTolMm() * 0.3) _dragging = true;
            InvalidateVisual();
            return;
        }
        UpdateHud();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        _panning = false;
        if (_dragShape is not null)
        {
            if (_dragging) _vm?.MoveSelected(_dragCurrent.X - _dragStart.X, _dragCurrent.Y - _dragStart.Y);
            _dragShape = null; _dragging = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var before = ToModel(e.GetPosition(this));
        _zoom *= e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoom = Math.Clamp(_zoom, 1e-4, 1e4);
        var after = ToModel(e.GetPosition(this));   // 커서 아래 지점 고정
        _pan = new Point(_pan.X + (after.X - before.X) * _zoom, _pan.Y - (after.Y - before.Y) * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hasCursor = false;
        InvalidateVisual();
    }

    // ── 키보드 ───────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is not { } vm) return;
        UpdateModifiers(e.KeyModifiers);
        switch (e.Key)
        {
            case Key.Escape: CancelDraw(); e.Handled = true; break;
            case Key.Delete or Key.Back when vm.EditMode:
                vm.DeleteSelectedCommand.Execute(null); e.Handled = true; break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.UndoCommand.Execute(null); e.Handled = true; break;
            case Key.Y when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.RedoCommand.Execute(null); e.Handled = true; break;
        }
        if (_hasCursor) { UpdateHud(); InvalidateVisual(); }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        UpdateModifiers(e.KeyModifiers);
        if (_hasCursor) { UpdateHud(); InvalidateVisual(); }
    }

    private void UpdateModifiers(KeyModifiers m)
    {
        LastAlt = m.HasFlag(KeyModifiers.Alt);
        LastShift = m.HasFlag(KeyModifiers.Shift);
        LastCtrl = m.HasFlag(KeyModifiers.Control);
    }

    private void CancelDraw()
    {
        _anchor = null;
        _dragShape = null; _dragging = false;
        InvalidateVisual();
    }

    private double HitTolMm() => _zoom > 0 ? 8 / _zoom : 8;   // 8px 상당

    private void UpdateHud()
    {
        if (_vm is not { } vm || !_hasCursor) return;
        var eff = ResolveCursor();
        vm.HudCursor = $"커서  X {eff.X:F0}  Y {eff.Y:F0} mm";
        var nc = NearestVertex(eff, vm);
        vm.HudCorner = $"{nc.Label} 기준  ΔX {nc.Dx:F0}  ΔY {nc.Dy:F0}  거리 {nc.Distance:F0} mm";
        vm.HudPoint = DrawGeometry.Nearest(eff, vm.Endpoints()) is { } np
            ? $"최근접 점  ΔX {eff.X - np.Point.X:F0}  ΔY {eff.Y - np.Point.Y:F0}  거리 {np.Distance:F0} mm"
            : "";
        vm.HudSnap = _snapKind.Length > 0 ? $"스냅: {_snapKind}" : "";
        vm.HudWarning = vm.Face.Contains(_rawCursor) ? "" : "면 경계를 벗어남 — 경계로 클리핑됩니다.";

        if (_anchor is { } a && !vm.EditMode)
        {
            double rx = Math.Abs(eff.X - a.X), ry = Math.Abs(eff.Y - a.Y);
            vm.HudRange = $"X범위 {rx:F0}  Y범위 {ry:F0} mm";
            if (vm.SelectedMode == DrawMode.Line)
            {
                vm.HudCount = $"길이 {Dist(a, eff):F0} mm";
                vm.HudCells = "";
            }
            else
            {
                var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(a, eff, vm.PitchX, vm.PitchY);
                vm.HudCells = $"칸 {cellsX} × {cellsY} = {cellsX * cellsY}칸";
                vm.HudCount = segs.Count > 0 ? $"선 {segs.Count}개" : "온전한 칸 없음";
            }
        }
        else { vm.HudRange = ""; vm.HudCount = ""; vm.HudCells = ""; }
    }

    // ── 브러시/펜 캐시 ───────────────────────────────────────────────────
    private IImmutableBrush Brush(Rgba c)
    {
        if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new ImmutableSolidColorBrush(c.ToColor());
        return b;
    }

    private IPen Pen(Rgba c, double thickness, bool dashed = false)
    {
        var pen = new Pen(Brush(c), thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        return pen;
    }

    private static double Dist(Pt2 a, Pt2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _fitPending = true;
        InvalidateVisual();
    }
}
