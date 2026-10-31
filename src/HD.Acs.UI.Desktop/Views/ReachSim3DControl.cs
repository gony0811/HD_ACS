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
/// 리치·간섭 3D 뷰 — <see cref="ReachSimViewModel.Scene"/> 을 코어 소프트웨어 렌더러(Camera3·SceneRenderer)로 그린다.
/// 조작은 화물창 3D 뷰와 같다: 좌드래그 = 오빗 · 우/중드래그 = 팬 · 휠 = 줌.
/// 시점 프리셋(단면·정면·위·3D)은 정차 차체 방향을 기준으로 잡는다 — 단면은 차체 좌측에서 벽을 옆으로 본다.
/// </summary>
public sealed class ReachSim3DControl : Control
{
    public enum Preset { Section, Front, Top, Iso }

    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.Parse("#1B2631"));
    private static readonly IBrush LabelBack = new ImmutableSolidColorBrush(Color.Parse("#CC12181D"));
    private const double DragThresholdPx = 4;

    private readonly Camera3 _camera = new() { FovDeg = 40 };
    private readonly Dictionary<Rgba, IImmutableBrush> _brushes = new();
    private readonly Dictionary<(Rgba, double), IPen> _pens = new();
    private readonly Typeface _typeface = new("Segoe UI, Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, sans-serif");

    private ReachSimViewModel? _vm;
    private Preset _preset = Preset.Section;
    private bool _needsPreset = true;
    private Point _lastPointer, _pressPointer;
    private PointerUpdateKind _dragButton;
    private bool _dragging;

    public ReachSim3DControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) => Attach(DataContext as ReachSimViewModel);
    }

    private void Attach(ReachSimViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.SceneChanged -= OnSceneChanged;
            _vm.ViewReset -= OnViewReset;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.SceneChanged += OnSceneChanged;
            _vm.ViewReset += OnViewReset;
        }
        _needsPreset = true;
        InvalidateVisual();
    }

    private void OnSceneChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnViewReset(object? sender, EventArgs e)
    {
        _needsPreset = true;
        InvalidateVisual();
    }

    /// <summary>시점 프리셋 적용(툴바).</summary>
    public void ApplyPreset(Preset preset)
    {
        _preset = preset;
        _needsPreset = true;
        InvalidateVisual();
    }

    private void ApplyPresetNow(double width, double height)
    {
        if (_vm?.Detail is not { } d || _vm.Scene.ExtentPoints.Count == 0) return;
        var psi = d.StationYaw * 180 / Math.PI;
        (_camera.YawDeg, _camera.PitchDeg) = _preset switch
        {
            Preset.Section => (psi - 90, 8.0),    // 차체 우측 → 좌측을 본다(단면 자르기가 남긴 쪽)
            Preset.Front => (psi + 180, 12.0),    // 차체 뒤에서 벽을 본다
            Preset.Top => (psi + 180, 85.0),
            _ => (psi - 135, 25.0),
        };
        _camera.ZoomExtents(_vm.Scene.ExtentPoints, width, height, 1.35);
        _needsPreset = false;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Background, new Rect(0, 0, b.Width, b.Height));
        if (b.Width < 1 || b.Height < 1 || _vm is null) return;
        if (_needsPreset) ApplyPresetNow(b.Width, b.Height);

        foreach (var d in SceneRenderer.Render(_vm.Scene, _camera, b.Width, b.Height))
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
                    var at = new Point(l.Position.X + 6, l.Position.Y - text.Height / 2);
                    ctx.FillRectangle(LabelBack, new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2), 3);
                    ctx.DrawText(text, at);
                    break;
            }
        }
    }

    private void DrawPolygon(DrawingContext ctx, Face2 f)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(f.Points[0].X, f.Points[0].Y), f.Fill is not null);
            for (var i = 1; i < f.Points.Length; i++) g.LineTo(new Point(f.Points[i].X, f.Points[i].Y));
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

    // ── 포인터: 오빗/팬/줌 ──
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        _dragButton = pt.Properties.PointerUpdateKind;
        _lastPointer = _pressPointer = pt.Position;
        _dragging = false;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        var p = e.GetPosition(this);
        var dx = p.X - _lastPointer.X;
        var dy = p.Y - _lastPointer.Y;
        _lastPointer = p;
        if (!_dragging && (Math.Abs(p.X - _pressPointer.X) > DragThresholdPx || Math.Abs(p.Y - _pressPointer.Y) > DragThresholdPx))
            _dragging = true;
        if (!_dragging) return;
        if (_dragButton == PointerUpdateKind.LeftButtonPressed) _camera.Orbit(-dx * 0.4, dy * 0.3);
        else _camera.Pan(dx, dy, Bounds.Height);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging) e.Handled = true;
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
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
}
