using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Desktop.Infrastructure;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 전개도 면 1개(<see cref="TankViewModel.FacePlot"/>)를 DrawingContext로 한 번에 그리는 컨트롤.
/// 종전엔 영역·용접선·끝점마다 Polygon/Line/Ellipse/TextBlock 컨트롤을 만들어(ItemsControl 중첩) 작업 수천 개면
/// 비주얼 트리가 수만 개로 커졌다 — 여기서는 면당 비주얼 1개. 표시 항목은 FacePlot.Layers(우클릭 메뉴)를 따른다.
/// DataContext=FacePlot.
/// </summary>
public sealed class FacePlotCanvas : Control
{
    private static readonly IBrush OutlineFill = new ImmutableSolidColorBrush(Color.Parse("#0F2E86C1"));
    private static readonly IPen OutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#5D6D7E")), 1.2);
    private static readonly Rgba WeldStart = Rgba.FromRgb(0x27, 0xAE, 0x60);
    private static readonly Rgba WeldEnd = Rgba.FromRgb(0xC0, 0x39, 0x2B);
    private static readonly Rgba SeqColor = Rgba.FromRgb(0xF5, 0xDE, 0xB3);
    private static readonly Typeface LabelFace = new("Segoe UI, Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, sans-serif", FontStyle.Normal, FontWeight.Bold);

    private static readonly Dictionary<Rgba, IImmutableBrush> Brushes = new();
    private static readonly Dictionary<(Rgba, double), IPen> Pens = new();
    private static readonly Dictionary<(string, Rgba, double), FormattedText> Texts = new();

    public FacePlotCanvas()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (this.TryFindResource("AppCanvasBrush", ActualThemeVariant, out var bg) && bg is IBrush canvas)
            ctx.FillRectangle(canvas, new Rect(Bounds.Size));
        if (DataContext is not TankViewModel.FacePlot plot) return;
        var layers = plot.Layers;

        if (plot.Outline.Count >= 3) DrawPolygon(ctx, plot.Outline, OutlineFill, OutlinePen);

        if (layers.AreaFills)
            foreach (var a in plot.Areas)
            {
                var (fill, line) = TankViewModel.StatusColors(a.Status);
                if (a.Points.Count >= 3) DrawPolygon(ctx, a.Points, Brush(fill), Pen(line, 1));
            }

        foreach (var t in plot.Tasks)
        {
            if (layers.WeldLines)
                ctx.DrawLine(Pen(TankViewModel.WeldLineColor(t.Status), 2), new Point(t.X1, t.Y1), new Point(t.X2, t.Y2));
            if (layers.WeldEndpoints)
            {
                ctx.DrawEllipse(Brush(WeldStart), null, new Point(t.X1, t.Y1), 2.5, 2.5);
                ctx.DrawEllipse(Brush(WeldEnd), null, new Point(t.X2, t.Y2), 2.5, 2.5);
            }
        }

        // 글자는 도형 위에
        if (layers.AreaLabels)
            foreach (var a in plot.Areas)
                DrawText(ctx, a.Label, TankViewModel.StatusColors(a.Status).Line, 9, a.LabelX, a.LabelY, center: false);
        if (layers.TaskSeq)
            foreach (var t in plot.Tasks)
                DrawText(ctx, t.Badge, SeqColor, 8, t.MidX, t.MidY, center: true);
    }

    private static void DrawPolygon(DrawingContext ctx, IReadOnlyList<Pt2> pts, IBrush? fill, IPen? pen)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(pts[0].X, pts[0].Y), fill is not null);
            for (int i = 1; i < pts.Count; i++) g.LineTo(new Point(pts[i].X, pts[i].Y));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, pen, geo);
    }

    private static void DrawText(DrawingContext ctx, string text, Rgba color, double size, double x, double y, bool center)
    {
        var key = (text, color, size);
        if (!Texts.TryGetValue(key, out var ft))
        {
            if (Texts.Count > 20_000) Texts.Clear();   // 상한 — 라벨 종류가 비정상적으로 많아도 메모리 제한
            Texts[key] = ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, size, Brush(color));
        }
        ctx.DrawText(ft, center ? new Point(x - ft.Width / 2, y - ft.Height / 2) : new Point(x, y));
    }

    private static IImmutableBrush Brush(Rgba c)
    {
        if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = new ImmutableSolidColorBrush(c.ToColor());
        return b;
    }

    private static IPen Pen(Rgba c, double thickness)
    {
        var key = (c, thickness);
        if (!Pens.TryGetValue(key, out var p)) Pens[key] = p = new ImmutablePen(Brush(c), thickness);
        return p;
    }
}
