using HD.Acs.UI.Drawing;
using HD.Acs.UI.Primitives;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>면 CAD(DXF) 가져오기 — 순수 분류·평행이동(길이 임계) 골든.</summary>
public class DxfFaceImportTests
{
    [Fact]
    public void Classify_KeepsOnlyWeldLines_ExcludesCorrugation()
    {
        var raw = new List<(Pt2 A, Pt2 B)>
        {
            (new Pt2(1000, 2000), new Pt2(1000, 4000)),   // 길이 2000 ≥ 500 → 용접라인(유지)
            (new Pt2(1100, 2000), new Pt2(1200, 2000)),   // 길이 100  < 500 → Corrugation(제외)
        };
        var r = DxfFaceImport.Classify("SL", "x.dxf", raw);

        Assert.Equal(1, r.WeldCount);
        Assert.Equal(1, r.CorrCount);        // 제외된 Corrugation 수(참고)
        Assert.Single(r.Segments);           // 용접라인만 남음
        Assert.Equal(nameof(DrawType.WeldLine), r.Segments[0].Kind);
        Assert.Equal(200, r.WidthMm);        // bbox는 전체 raw 기준
        Assert.Equal(2000, r.HeightMm);

        // bbox 좌하단(1000,2000)이 (0,0)으로 평행이동
        Assert.Equal(0, r.Segments[0].Ax);
        Assert.Equal(0, r.Segments[0].Ay);
        Assert.Equal(2000, r.Segments[0].By);
    }

    [Fact]
    public void Classify_RemovesDuplicateOverlappingSegments()
    {
        var raw = new List<(Pt2 A, Pt2 B)>
        {
            (new Pt2(0, 0), new Pt2(1000, 0)),      // 용접라인
            (new Pt2(1000, 0), new Pt2(0, 0)),      // 같은 선(방향만 반대) → 중복 제거
            (new Pt2(0, 500), new Pt2(1000, 500)),  // 다른 용접라인
        };
        var r = DxfFaceImport.Classify("A", "a.dxf", raw);
        Assert.Equal(2, r.WeldCount);   // 3개 중 중복 1개 제거 → 2개
        Assert.Equal(2, r.Segments.Count);
    }

    [Fact]
    public void Classify_Empty_ReturnsNoSegments()
    {
        var r = DxfFaceImport.Classify("A", "a.dxf", System.Array.Empty<(Pt2, Pt2)>());
        Assert.Empty(r.Segments);
        Assert.Equal(0, r.WeldCount);
        Assert.Equal(0, r.CorrCount);
    }
}
