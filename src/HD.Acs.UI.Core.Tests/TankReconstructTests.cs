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
}
