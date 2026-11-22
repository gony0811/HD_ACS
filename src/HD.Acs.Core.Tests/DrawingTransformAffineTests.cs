using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// T_W_D affine 확장 — SLAM 맵이 실제보다 늘거나 줄어든 층(강체로는 잔차가 남는 층)을 흡수한다.
/// 현장값: 2026-11-22 CT1-L3 기준점 5개(줄자 실측 도면 좌표 ↔ AMR 보고 맵 좌표), 강체 RMS 0.133 m.
/// </summary>
public class DrawingTransformAffineTests
{
    private static readonly ((double X, double Y) Drawing, (double X, double Y) Map)[] Field =
    {
        ((-9.000, 6.840), (7.263, 16.549)),
        ((-0.360, 6.840), (15.919, 16.562)),
        ((11.160, -6.800), (27.598, 2.852)),
        ((6.840, -6.800), (23.261, 2.782)),
        ((-7.380, -6.850), (8.820, 2.596)),
    };

    [Fact]
    public void FieldPoints_AffineCutsResidual_ComparedToRigid()
    {
        var rigid = DrawingTransform.Solve(Field);
        var affine = DrawingTransform.SolveAffine(Field);

        Assert.Equal(0.133, rigid.RmsM, 3);
        Assert.Equal(0.040, affine.RmsM, 3);
        Assert.True(affine.MaxResidualM < 0.06);
        Assert.True(affine.T.IsAffine);
        // 축척은 1~2% — 맵이 도면보다 조금 크다. 회전은 강체 해와 거의 같다.
        var (lo, hi) = affine.T.Scales();
        Assert.InRange(lo, 1.005, 1.02);
        Assert.InRange(hi, 1.005, 1.02);
        Assert.Equal(rigid.T.YawRad, affine.T.YawRad, 2);
    }

    [Fact]
    public void KnownAffine_IsRecoveredExactly_AndRoundTrips()
    {
        var known = new DrawingTransform(16.3, 9.6, 0, new AffineLinear(1.012, -0.004, 0.006, 0.991));
        var pairs = new[] { (-12.0, 7.0), (12.0, 7.0), (12.0, -7.0), (-12.0, -7.0), (1.5, 0.5) }
            .Select(d => (d, known.DrawingToMap(d.Item1, d.Item2))).ToList();

        var (t, rms, max) = DrawingTransform.SolveAffine(pairs);

        Assert.Equal(0, rms, 9);
        Assert.Equal(0, max, 9);
        Assert.Equal(1.012, t.Affine!.A11, 9);
        Assert.Equal(-0.004, t.Affine.A12, 9);
        Assert.Equal(0.006, t.Affine.A21, 9);
        Assert.Equal(0.991, t.Affine.A22, 9);
        Assert.Equal(16.3, t.Tx, 9);
        Assert.Equal(9.6, t.Ty, 9);

        var (mx, my) = t.DrawingToMap(-7.38, -6.85);
        var (dx, dy) = t.MapToDrawing(mx, my);
        Assert.Equal(-7.38, dx, 9);
        Assert.Equal(-6.85, dy, 9);
        foreach (double yaw in new[] { 0.0, 0.7, Math.PI / 2, -2.9, Math.PI })
        {
            double back = t.MapYawToDrawing(t.DrawingYawToMap(yaw));
            Assert.Equal(0, Math.Abs(Math.IEEERemainder(back - yaw, 2 * Math.PI)), 9);
        }
    }

    [Fact]
    public void PureRotationAsAffine_MatchesRigidTransform()
    {
        var rigid = new DrawingTransform(7.0, -4.0, 0.6);
        var pairs = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 6.0), (0.0, 6.0) }
            .Select(d => (d, rigid.DrawingToMap(d.Item1, d.Item2))).ToList();

        var t = DrawingTransform.SolveAffine(pairs).T;

        Assert.Equal(rigid.YawRad, t.YawRad, 9);
        var (lo, hi) = t.Scales();
        Assert.Equal(1, lo, 9);
        Assert.Equal(1, hi, 9);
        Assert.Equal(rigid.DrawingYawToMap(1.1), t.DrawingYawToMap(1.1), 9);
        Assert.Equal(rigid.MapYawToDrawing(-2.0), t.MapYawToDrawing(-2.0), 9);
        Assert.Equal((1.0, 1.0), rigid.Scales());
    }

    [Fact]
    public void Rejects_TooFewPairs_CollinearPoints_AndMirror()
    {
        Assert.Throws<ArgumentException>(() => DrawingTransform.SolveAffine(Field.Take(3).ToList()));

        var line = new[] { (-10.0, 5.0), (-3.0, 5.0), (4.0, 5.1), (11.0, 5.0) }
            .Select(d => (d, (d.Item1 + 16, d.Item2 + 9))).ToList();
        var ex = Assert.Throws<ArgumentException>(() => DrawingTransform.SolveAffine(line));
        Assert.Contains("한 줄", ex.Message);

        var mirror = new[] { (-10.0, 5.0), (10.0, 5.0), (10.0, -5.0), (-10.0, -5.0) }
            .Select(d => (d, (d.Item1 + 16, -d.Item2 + 9))).ToList();
        Assert.Contains("거울", Assert.Throws<ArgumentException>(() => DrawingTransform.SolveAffine(mirror)).Message);
    }

    [Fact]
    public void LeaveOneOut_IsLargerThanFitResidual_AndNullWhenTooFewPoints()
    {
        double loo = DrawingTransform.LeaveOneOutRms(Field, affine: true)!.Value;
        Assert.True(loo > DrawingTransform.SolveAffine(Field).RmsM);
        Assert.Null(DrawingTransform.LeaveOneOutRms(Field.Take(4).ToList(), affine: true));
        Assert.NotNull(DrawingTransform.LeaveOneOutRms(Field.Take(3).ToList(), affine: false));
    }

    [Fact]
    public void ResolveTransform_BuildsAffine_OnlyWhenAllFourValuesPresent()
    {
        var rigid = WeldInspectionPayload.ResolveTransform(1, 1, 16, 9, 0.01);
        Assert.False(rigid.IsAffine);
        Assert.False(WeldInspectionPayload.ResolveTransform(1, 1, 16, 9, 0.01, 1.01, null, 0, 1.02).IsAffine);

        var affine = WeldInspectionPayload.ResolveTransform(1, 1, 16, 9, 0.01, 1.01, 0, 0, 1.02);
        Assert.True(affine.IsAffine);
        var (x, y) = affine.DrawingToMap(10, -5);
        Assert.Equal(26.1, x, 9);
        Assert.Equal(3.9, y, 9);
    }
}
