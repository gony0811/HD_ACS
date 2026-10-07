using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Desktop.Infrastructure;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// <see cref="SeamGlyphShape"/>을 DrawingContext에 그린다 — 계획 전개도(<see cref="TaskGlyphLayer"/>)와
/// 운영 전개도(<see cref="FacePlotCanvas"/>)가 공유. 색은 호출 측이 정한다(운영=진행 상태색).
/// </summary>
internal static class SeamGlyphPainter
{
    private static readonly Dictionary<Rgba, IImmutableBrush> Brushes = new();
    private static readonly Dictionary<(Rgba, double, bool), IPen> Pens = new();
    private static readonly IImmutableBrush MarkerEdge = new ImmutableSolidColorBrush(Colors.White);

    /// <summary>선분(줄기는 <paramref name="stemThickness"/>) + 화살촉 + 교차점 마커(흰 테두리).</summary>
    public static void Draw(DrawingContext ctx, SeamGlyphShape g, Rgba color, double thickness, double stemThickness)
    {
        foreach (var l in g.Lines)
            ctx.DrawLine(Pen(color, l.IsStem ? stemThickness : thickness, g.Dashed), P(l.A), P(l.B));
        if (g.Arrow is { Count: 3 } a) Fill(ctx, a, Brush(color), null);
        if (g.Marker is { Count: >= 3 } m) Fill(ctx, m, Brush(color), new ImmutablePen(MarkerEdge, 1.2));
    }

    public static void Fill(DrawingContext ctx, IReadOnlyList<Pt2> pts, IBrush? fill, IPen? pen)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(P(pts[0]), fill is not null);
            for (int i = 1; i < pts.Count; i++) c.LineTo(P(pts[i]));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(fill, pen, geo);
    }

    public static IImmutableBrush Brush(Rgba c)
    {
        if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = new ImmutableSolidColorBrush(c.ToColor());
        return b;
    }

    public static IPen Pen(Rgba c, double thickness, bool dashed = false)
    {
        var key = (c, thickness, dashed);
        if (!Pens.TryGetValue(key, out var p))
            Pens[key] = p = new ImmutablePen(Brush(c), thickness, dashed ? new ImmutableDashStyle(new double[] { 3, 2 }, 0) : null);
        return p;
    }

    private static Point P(Pt2 p) => new(p.X, p.Y);
}
