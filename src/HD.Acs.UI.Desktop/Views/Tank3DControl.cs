using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Desktop.Infrastructure;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 화물창 3D 뷰 — 소프트웨어 투영 렌더러(코어 Camera3·SceneRenderer)를 DrawingContext로 그리는 컨트롤. HelixViewport3D 대체.
/// DataContext=TankViewModel. VM의 ViewChanged/속성 변화에 씬을 재구성(TankSceneBuilder)하고 카메라는 ZoomExtents.
/// 조작: 좌드래그=오빗 · 우/중드래그=팬 · 휠=줌 · (수동 이동 모드) 바닥에서 좌드래그=위치+정차 방향 goto.
/// </summary>
public sealed class Tank3DControl : Control
{
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.Parse("#1B2631"));
    private const double DragThresholdPx = 4;

    private readonly Camera3 _camera = new();
    private readonly Dictionary<Rgba, IImmutableBrush> _brushes = new();
    private readonly Dictionary<(Rgba, double), IPen> _pens = new();
    private readonly Typeface _typeface = new("Segoe UI, Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, sans-serif");

    private TankViewModel? _vm;
    private Scene3 _scene = new();
    private TankSceneInput? _input;
    private Pt3? _manualMoveStart;
    private double? _previewHeading;
    private bool _needsZoomExtents = true;

    private Point _lastPointer, _pressPointer;
    private PointerUpdateKind _dragButton;
    private bool _dragging;

    public Tank3DControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as TankViewModel);
    }

    private void AttachViewModel(TankViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ViewChanged -= OnViewChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ViewChanged += OnViewChanged;
        }
        _needsZoomExtents = true;
        Rebuild();
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        _manualMoveStart = null;
        _previewHeading = null;
        _needsZoomExtents = true;   // WPF와 동일: 셸/뷰 모드 변경 시 ZoomExtents
        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TankViewModel.RobotDrawingX) or nameof(TankViewModel.RobotDrawingY)
            or nameof(TankViewModel.RobotDrawingZ) or nameof(TankViewModel.RobotDrawingTheta) or nameof(TankViewModel.HasRobotPosition)
            or nameof(TankViewModel.ShowOverlays) or nameof(TankViewModel.ManualMoveMode)
            or nameof(TankViewModel.MoveMarker) or nameof(TankViewModel.MoveHeading))
            Rebuild();
    }

    /// <summary>VM 스냅샷으로 씬 재구성 후 다시 그리기.</summary>
    public void Rebuild()
    {
        if (_vm is null)
        {
            _scene = new Scene3(); _input = null;
            InvalidateVisual();
            return;
        }

        Pt3? moveMarker = _manualMoveStart is { } s ? new Pt3(s.X, s.Y, s.Z + 0.25) : _vm.MoveMarker;
        double? moveHeading = _manualMoveStart is not null ? _previewHeading : _vm.MoveHeading;

        _input = new TankSceneInput(
            _vm.ShellWalls.ToArray(), _vm.LevelWalls.ToArray(), _vm.Geometry,
            _vm.Overlays.ToArray(), _vm.ShowOverlays, _vm.SelectedLevel,
            _vm.WorkItemStatusOf, _vm.TaskStatusOf,
            _vm.HasRobotPosition, new Pt3(_vm.RobotDrawingX, _vm.RobotDrawingY, _vm.RobotDrawingZ),
            moveMarker, _vm.RobotDrawingTheta, moveHeading);
        _scene = TankSceneBuilder.Build(_input);
        InvalidateVisual();
    }

    /// <summary>현재 카메라·크기로 그리기 목록 생성(테스트·진단용).</summary>
    public IReadOnlyList<Draw2> BuildDrawList(double width, double height)
    {
        if (_needsZoomExtents && _scene.ExtentPoints.Count > 0 && width > 0 && height > 0)
        {
            _camera.ZoomExtents(_scene.ExtentPoints, width, height);
            _needsZoomExtents = false;
        }
        return SceneRenderer.Render(_scene, _camera, width, height);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Background, new Rect(0, 0, b.Width, b.Height));
        if (b.Width < 1 || b.Height < 1) return;

        foreach (var d in BuildDrawList(b.Width, b.Height))
        {
            switch (d)
            {
                case Face2 f:
                    DrawPolygon(ctx, f);
                    break;
                case Segment2 s:
                    ctx.DrawLine(Pen(s.Color, s.Thickness), new Point(s.A.X, s.A.Y), new Point(s.B.X, s.B.Y));
                    break;
                case Marker2 m:
                    ctx.DrawEllipse(Brush(m.Fill), m.Stroke is { } st ? Pen(st, 1.2) : null, new Point(m.Center.X, m.Center.Y), m.Radius, m.Radius);
                    break;
                case Label2 l:
                    var text = new FormattedText(l.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, l.FontSize, Brush(l.Color));
                    ctx.DrawText(text, new Point(l.Position.X - text.Width / 2, l.Position.Y - text.Height / 2));
                    break;
            }
        }
        DrawOrientationCube(ctx, b.Width);
    }

    /// <summary>Helix ViewCube에 대응하는 선창 도면 좌표계. +X=선수, -X=선미, +Y=좌현, -Y=우현.</summary>
    private void DrawOrientationCube(DrawingContext ctx, double width)
    {
        if (width < 150) return;
        const double size = 24;
        var center = new Pt2(width - 62, 57);
        var (forward, right, up) = _camera.Basis();
        var eyeDirection = -forward;
        var faces = new[]
        {
            (Normal: new Pt3( 1, 0, 0), Points: new[] { new Pt3(1,-1,-1), new Pt3(1,1,-1), new Pt3(1,1,1), new Pt3(1,-1,1) }, Label: "선수", Color: Rgba.FromRgb(0xC0,0x39,0x2B)),
            (Normal: new Pt3(-1, 0, 0), Points: new[] { new Pt3(-1,1,-1), new Pt3(-1,-1,-1), new Pt3(-1,-1,1), new Pt3(-1,1,1) }, Label: "선미", Color: Rgba.FromRgb(0x7F,0x8C,0x8D)),
            (Normal: new Pt3(0, 1, 0), Points: new[] { new Pt3(1,1,-1), new Pt3(-1,1,-1), new Pt3(-1,1,1), new Pt3(1,1,1) }, Label: "좌현", Color: Rgba.FromRgb(0x27,0xAE,0x60)),
            (Normal: new Pt3(0,-1, 0), Points: new[] { new Pt3(-1,-1,-1), new Pt3(1,-1,-1), new Pt3(1,-1,1), new Pt3(-1,-1,1) }, Label: "우현", Color: Rgba.FromRgb(0x2E,0x86,0xC1)),
            (Normal: new Pt3(0, 0, 1), Points: new[] { new Pt3(-1,-1,1), new Pt3(1,-1,1), new Pt3(1,1,1), new Pt3(-1,1,1) }, Label: "상", Color: Rgba.FromRgb(0xD4,0xAC,0x0D)),
            (Normal: new Pt3(0, 0,-1), Points: new[] { new Pt3(-1,1,-1), new Pt3(1,1,-1), new Pt3(1,-1,-1), new Pt3(-1,-1,-1) }, Label: "하", Color: Rgba.FromRgb(0x34,0x49,0x5E)),
        };

        ctx.FillRectangle(Brush(Rgba.FromArgb(0xA8, 0x12, 0x1B, 0x22)), new Rect(width - 124, 8, 116, 104));
        foreach (var face in faces.OrderByDescending(face => face.Points.Average(p => p.Dot(forward))))
        {
            var points = face.Points.Select(p => new Pt2(center.X + p.Dot(right) * size, center.Y - p.Dot(up) * size)).ToArray();
            DrawPolygon(ctx, new Face2(points, face.Color with { A = 0xD8 }, Rgba.FromRgb(0xEC, 0xF0, 0xF1), 0.8, 0));
            if (face.Normal.Dot(eyeDirection) <= 0.08) continue;
            var labelAt = new Pt2(points.Average(p => p.X), points.Average(p => p.Y));
            var label = new FormattedText(face.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 10, Brush(Rgba.FromRgb(0xFF, 0xFF, 0xFF)));
            ctx.DrawText(label, new Point(labelAt.X - label.Width / 2, labelAt.Y - label.Height / 2));
        }

        var legend = new FormattedText("+X 선수  +Y 좌현", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, 9, Brush(Rgba.FromRgb(0xEC, 0xF0, 0xF1)));
        ctx.DrawText(legend, new Point(width - 120, 94));
    }

    private void DrawPolygon(DrawingContext ctx, Face2 f)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(f.Points[0].X, f.Points[0].Y), f.Fill is not null);
            for (int i = 1; i < f.Points.Length; i++) g.LineTo(new Point(f.Points[i].X, f.Points[i].Y));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(f.Fill is { } fill ? Brush(fill) : null, f.Stroke is { } stroke ? Pen(stroke, f.StrokeThickness) : null, geo);
    }

    private IImmutableBrush Brush(Rgba c)
    {
        if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new ImmutableSolidColorBrush(c.ToColor());
        return b;
    }

    private IPen Pen(Rgba c, double thickness)
    {
        var key = (c, thickness);
        if (!_pens.TryGetValue(key, out var p)) _pens[key] = p = new ImmutablePen(Brush(c), thickness);
        return p;
    }

    // ── 포인터 조작: 오빗/팬/줌 + 수동 이동 클릭 ──
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        _dragButton = pt.Properties.PointerUpdateKind;
        _lastPointer = _pressPointer = pt.Position;
        _dragging = false;
        if (_dragButton == PointerUpdateKind.LeftButtonPressed && _vm?.ManualMoveMode == true
            && TryHitFloor(pt.Position, requireInside: true) is { } hit)
        {
            _manualMoveStart = hit;
            _previewHeading = null;
            Rebuild();
        }
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        var p = e.GetPosition(this);
        var dx = p.X - _lastPointer.X; var dy = p.Y - _lastPointer.Y;
        _lastPointer = p;
        if (_manualMoveStart is { } start)
        {
            if (TryHitFloor(p, requireInside: false) is { } current)
            {
                double hx = current.X - start.X, hy = current.Y - start.Y;
                _previewHeading = hx * hx + hy * hy >= 0.0025 ? Math.Atan2(hy, hx) : null;
                Rebuild();
            }
            return;
        }
        if (!_dragging && (Math.Abs(p.X - _pressPointer.X) > DragThresholdPx || Math.Abs(p.Y - _pressPointer.Y) > DragThresholdPx))
            _dragging = true;
        if (!_dragging) return;

        if (_dragButton == PointerUpdateKind.LeftButtonPressed)
            _camera.Orbit(-dx * 0.4, dy * 0.3);
        else
            _camera.Pan(dx, dy, Bounds.Height);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        if (_manualMoveStart is { } start)
        {
            if (TryHitFloor(e.GetPosition(this), requireInside: false) is { } current)
            {
                double hx = current.X - start.X, hy = current.Y - start.Y;
                _previewHeading = hx * hx + hy * hy >= 0.0025 ? Math.Atan2(hy, hx) : null;
            }
            var theta = _previewHeading;
            _manualMoveStart = null;
            _previewHeading = null;
            if (_vm is not null) _ = _vm.RequestMoveAsync(start.X, start.Y, theta, start.Z);
            Rebuild();
            return;
        }
        if (_dragging || _dragButton != PointerUpdateKind.LeftButtonPressed) return;
    }

    private Pt3? TryHitFloor(Point screen, bool requireInside)
    {
        if (_input is null || TankSceneBuilder.FloorPlane(_input) is not var (z, x0, y0, x1, y1)) return null;
        var hit = _camera.HitPlaneZ(new Pt2(screen.X, screen.Y), z, Bounds.Width, Bounds.Height);
        if (hit is not { } h) return null;
        if (requireInside && (h.X < x0 || h.X > x1 || h.Y < y0 || h.Y > y1)) return null;
        return h;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.Zoom(e.Delta.Y > 0 ? 0.85 : 1.0 / 0.85);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    /// <summary>카메라를 전체 형상에 맞춘다(툴바 "맞춤").</summary>
    public void ZoomExtents()
    {
        _needsZoomExtents = true;
        InvalidateVisual();
    }
}
