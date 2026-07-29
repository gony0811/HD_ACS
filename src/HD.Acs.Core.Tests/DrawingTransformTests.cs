using HD.Acs.Core.Geometry;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>DrawingTransform (T_W_D) 단위 테스트 [PHASE2 §2.3 수용기준].</summary>
public class DrawingTransformTests
{
    /// <summary>기지 변환으로 합성 대응쌍 생성 → Solve가 원 변환을 복원(yaw 1e-9, t 1e-9).</summary>
    [Fact]
    public void Solve_RecoversKnownTransform()
    {
        var known = new DrawingTransform(Tx: 12.5, Ty: -3.2, YawRad: 0.7853981633974483); // 45°
        var drawingPts = new (double X, double Y)[]
        {
            (0, 0), (4.0, 0), (4.0, 2.5), (1.0, 3.0)
        };
        var pairs = drawingPts
            .Select(d => (d, known.DrawingToMap(d.X, d.Y)))
            .ToList();

        var (t, rms, max) = DrawingTransform.Solve(pairs);

        Assert.Equal(known.YawRad, t.YawRad, 9);
        Assert.Equal(known.Tx, t.Tx, 9);
        Assert.Equal(known.Ty, t.Ty, 9);
        Assert.True(rms < 1e-9, $"noise-free RMS should be ~0 but was {rms}");
        Assert.True(max < 1e-9);
    }

    /// <summary>3점 + 결정론적 노이즈 → RMS가 노이즈 수준과 대체로 일치(과대·과소 아님).</summary>
    [Fact]
    public void Solve_RmsReflectsNoiseLevel()
    {
        var known = new DrawingTransform(2.0, 5.0, 0.3);
        var drawingPts = new (double X, double Y)[] { (0, 0), (5.0, 0), (0, 5.0) };
        // 각 점 맵 좌표에 크기 e의 결정론적 오프셋(방향 회전) 부여
        const double e = 0.02;
        double[] angles = { 0.0, 2.0943951, 4.1887902 }; // 0°, 120°, 240°
        var pairs = drawingPts.Select((d, i) =>
        {
            var (mx, my) = known.DrawingToMap(d.X, d.Y);
            return (d, (mx + e * Math.Cos(angles[i]), my + e * Math.Sin(angles[i])));
        }).ToList();

        var (_, rms, max) = DrawingTransform.Solve(pairs);

        // 균등 방향 오차의 RMS는 오프셋 크기 e 근방 (0.5e ~ 1.5e)
        Assert.InRange(rms, 0.5 * e, 1.5 * e);
        Assert.True(max <= e + 1e-9, $"max residual {max} should not exceed offset {e}");
    }

    [Fact]
    public void Solve_TooFewPairs_Throws()
    {
        var one = new[] { (((double X, double Y))(0, 0), ((double X, double Y))(1, 1)) };
        Assert.Throws<ArgumentException>(() => DrawingTransform.Solve(one));
        Assert.Throws<ArgumentException>(() =>
            DrawingTransform.Solve(Array.Empty<((double X, double Y), (double X, double Y))>()));
    }

    /// <summary>DrawingToMap ↔ MapToDrawing 왕복 항등.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.5707963267948966)]  // 90°
    [InlineData(-2.0)]
    public void DrawingToMap_MapToDrawing_RoundTrips(double yaw)
    {
        var t = new DrawingTransform(7.0, -4.0, yaw);
        var (mx, my) = t.DrawingToMap(3.3, -1.2);
        var (dx, dy) = t.MapToDrawing(mx, my);
        Assert.Equal(3.3, dx, 9);
        Assert.Equal(-1.2, dy, 9);
    }

    /// <summary>DrawingYawToMap = 도면 yaw + 변환 yaw, (−π, π] 정규화.</summary>
    [Fact]
    public void DrawingYawToMap_ComposesAndNormalizes()
    {
        var t = new DrawingTransform(0, 0, Math.PI * 0.75);
        // 0.75π + 0.75π = 1.5π → 정규화 시 −0.5π
        double mapped = t.DrawingYawToMap(Math.PI * 0.75);
        Assert.Equal(-Math.PI * 0.5, mapped, 9);
        // 방향 회전과 좌표 회전 일관성: 도면 단위 X축(yaw=0)이 맵에서 변환 yaw 방향
        Assert.Equal(t.YawRad, t.DrawingYawToMap(0.0), 9);
    }
}
