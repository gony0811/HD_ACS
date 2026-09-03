using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

public class Plot2DTests
{
    // 캔버스 600px·여백 28·v 뒤집기 — AreaPlanningViewModel.Project()와 같은 형태의 투영
    private static (double x, double y) Proj(double u, double v) => (28 + u * 10, 28 + (50 - v) * 10);

    [Fact]
    public void ProjectPolygon_MapsEachVertex_PreservingOrder()
    {
        var uv = new (double u, double v)[] { (0, 0), (10, 0), (10, 50), (0, 50) };
        var pts = Plot2D.ProjectPolygon(uv, Proj);

        Assert.Equal(4, pts.Length);
        Assert.Equal(new Pt2(28, 528), pts[0]);   // (0,0) → 좌하단
        Assert.Equal(new Pt2(128, 528), pts[1]);
        Assert.Equal(new Pt2(128, 28), pts[2]);   // (10,50) → 우상단
        Assert.Equal(new Pt2(28, 28), pts[3]);
    }

    [Fact]
    public void ProjectCorners_SkipsMalformedEntries()
    {
        var corners = new[] { new[] { 1.0, 2.0 }, new[] { 3.0 }, new[] { 5.0, 6.0, 9.0 } };
        var pts = Plot2D.ProjectCorners(corners, (u, v) => (u, v));

        Assert.Equal(2, pts.Length);
        Assert.Equal(new Pt2(1, 2), pts[0]);
        Assert.Equal(new Pt2(5, 6), pts[1]);
    }

    [Fact]
    public void Centroid_IsVertexAverage_EmptyIsOrigin()
    {
        var c = Plot2D.Centroid(new[] { new Pt2(0, 0), new Pt2(4, 0), new Pt2(4, 2), new Pt2(0, 2) });
        Assert.Equal(new Pt2(2, 1), c);
        Assert.Equal(default, Plot2D.Centroid(Array.Empty<Pt2>()));
    }
}
