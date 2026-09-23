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

    // 마구리 챔퍼 각 45° 견고화: OutlineFit은 corrugation 포함 외곽으로 역산 → 바닥 코너가 채워져
    // 용접선만 기반 fit보다 바닥폭이 정확(넓게) 나온다. 저장/표시 Segments는 여전히 용접선만.
    [Fact]
    public void Classify_OutlineFit_IncludesCorrugationCorners()
    {
        var raw = new List<(Pt2 A, Pt2 B)>
        {
            // 용접선 팔각 외곽(바닥 변 4000..8000 = 폭 4000)
            (new Pt2(4000, 0),     new Pt2(8000, 0)),        // 바닥
            (new Pt2(8000, 0),     new Pt2(12000, 3000)),    // 하부챔퍼 우
            (new Pt2(12000, 3000), new Pt2(12000, 9000)),    // 우벽
            (new Pt2(12000, 9000), new Pt2(8000, 12000)),    // 상부챔퍼 우
            (new Pt2(8000, 12000), new Pt2(4000, 12000)),    // 천장
            (new Pt2(4000, 12000), new Pt2(0, 9000)),        // 상부챔퍼 좌
            (new Pt2(0, 9000),     new Pt2(0, 3000)),        // 좌벽
            (new Pt2(0, 3000),     new Pt2(4000, 0)),        // 하부챔퍼 좌
            // corrugation(단선<500): 바닥 코너를 바깥으로 200mm씩 넓힘(용접선엔 없는 코너 채움)
            (new Pt2(3800, 0), new Pt2(4000, 0)),
            (new Pt2(8000, 0), new Pt2(8200, 0)),
        };
        var r = DxfFaceImport.Classify("A", "a.dxf", raw);

        Assert.NotNull(r.OutlineFit);
        Assert.Equal(4400, r.OutlineFit!.WFloor, 1);   // corrugation 포함 = 3800..8200

        var weldFit = TankReconstruct.FitOctagon(
            r.Segments.SelectMany(s => new[] { new Pt2(s.Ax, s.Ay), new Pt2(s.Bx, s.By) }).ToList());
        Assert.Equal(4000, weldFit!.WFloor, 1);        // 용접선만 = 4000 (코너 미채움)
    }
}
