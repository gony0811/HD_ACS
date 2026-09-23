using HD.Acs.UI.Drawing;
using HD.Acs.UI.Primitives;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>도면(DXF)에서 팔각 단면 역산 — 실 마구리(WALL A) 외곽 정점 골든.</summary>
public class TankReconstructTests
{
    // WALL A.dxf 멤브레인 외곽(팔각) 정점(면-로컬 근사): 바닥폭 8590·천장폭 11780·전폭 16950·높이 12630.
    // 좌하단 원점으로 평행이동한 좌표.
    private static readonly Pt2[] OctaA =
    {
        new(0, 3720), new(3665, 0), new(12255, 0), new(16950, 3720),
        new(16950, 9991), new(14365, 12630), new(2585, 12630), new(0, 9991),
    };

    [Fact]
    public void FitOctagon_RecoversSectionParams_FromBulkheadOutline()
    {
        var fit = TankReconstruct.FitOctagon(OctaA);
        Assert.NotNull(fit);
        Assert.Equal(16950, fit!.BeamB, 1);
        Assert.Equal(12630, fit.HTotal, 1);
        Assert.Equal(8590, fit.WFloor, 1);    // 바닥 변 12255−3665
        Assert.Equal(11780, fit.WCeil, 1);    // 천장 변 14365−2585
        Assert.Equal(3720, fit.HLow, 1);
        Assert.Equal(2639, fit.HUp, 1);       // 12630−9991
        Assert.Equal(6271, fit.HWall, 1);     // 9991−3720
        // θ_low = atan2(3720, (16950−8590)/2=4180) ≈ 41.66°
        Assert.Equal(41.66, fit.ThetaLowDeg, 1);
        Assert.Equal(45.6, fit.ThetaUpDeg, 1);
    }

    [Fact]
    public void FitOctagon_Degenerate_ReturnsNull()
    {
        Assert.Null(TankReconstruct.FitOctagon(new[] { new Pt2(0, 0), new Pt2(1, 0) }));   // 점 부족
    }

    [Fact]
    public void Extent_ReturnsBboxSize()
    {
        var e = TankReconstruct.Extent(new[] { new Pt2(0, 0), new Pt2(30630, 0), new Pt2(30630, 5095) });
        Assert.NotNull(e);
        Assert.Equal(30630, e!.Value.W, 1);
        Assert.Equal(5095, e.Value.H, 1);
    }

    // 바닥 면(WALL B) 자체 bbox = 길이 30630 × 폭 9618 → 폭(짧은 변)을 정확히 취한다.
    // 마구리 챔퍼 외곽 역산(9620/8590)이 아닌 이 값이 바닥폭 정본(CAD 측정 9618과 일치).
    [Fact]
    public void FaceWidth_PicksTransverse_NotLength()
    {
        var floor = new[] { new Pt2(0, 0), new Pt2(30630, 0), new Pt2(30630, 9618), new Pt2(0, 9618) };
        Assert.Equal(9618, TankReconstruct.FaceWidth(floor, 30630)!.Value, 1);   // L에 가까운 변=길이, 나머지=폭
    }

    // 도면 방향이 뒤집혀(길이 축이 Y) 있어도 L에 가까운 변을 길이로 판정 → 폭은 여전히 짧은 변.
    [Fact]
    public void FaceWidth_OrientationIndependent()
    {
        var rotated = new[] { new Pt2(0, 0), new Pt2(11778, 0), new Pt2(11778, 30630), new Pt2(0, 30630) };
        Assert.Equal(11778, TankReconstruct.FaceWidth(rotated, 30630)!.Value, 1);
    }

    [Fact]
    public void FaceWidth_Empty_ReturnsNull() => Assert.Null(TankReconstruct.FaceWidth(Array.Empty<Pt2>(), 30630));
}
