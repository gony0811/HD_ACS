namespace HD.Acs.Core.Geometry;

/// <summary>
/// 층별 도면→맵 2D 강체변환 [PHASE2 §T_W_D]. 스케일 없음(도면·맵 모두 m).
/// 맵 좌표 = R(yaw)·도면좌표 + t. yaw는 맵 X축 기준 CCW [rad].
/// </summary>
public sealed record DrawingTransform(double Tx, double Ty, double YawRad)
{
    /// <summary>도면 좌표 → 맵 좌표. R·d + t</summary>
    public (double X, double Y) DrawingToMap(double dx, double dy)
    {
        var (cos, sin) = (Math.Cos(YawRad), Math.Sin(YawRad));
        return (cos * dx - sin * dy + Tx,
                sin * dx + cos * dy + Ty);
    }

    /// <summary>맵 좌표 → 도면 좌표. R⁻¹·(m − t) (R은 직교행렬이므로 R⁻¹ = Rᵀ)</summary>
    public (double X, double Y) MapToDrawing(double mx, double my)
    {
        var (cos, sin) = (Math.Cos(YawRad), Math.Sin(YawRad));
        double ux = mx - Tx, uy = my - Ty;
        return (cos * ux + sin * uy,
                -sin * ux + cos * uy);
    }

    /// <summary>도면 좌표계 방향(yaw) → 맵 좌표계 방향. yaw 합성 후 (−π, π]로 정규화.</summary>
    public double DrawingYawToMap(double drawingYaw) => NormalizeAngle(drawingYaw + YawRad);

    /// <summary>
    /// 대응쌍 최소자승 (2D 강체, 스케일 없음) [§2.3]. 2쌍 미만이면 예외.
    /// 반환: 변환 T + 잔차 RMS + 최대 잔차.
    /// </summary>
    public static (DrawingTransform T, double RmsM, double MaxResidualM) Solve(
        IReadOnlyList<((double X, double Y) Drawing, (double X, double Y) Map)> pairs)
    {
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));
        if (pairs.Count < 2)
            throw new ArgumentException("2D 강체변환 산출에는 최소 2개의 대응쌍이 필요합니다.", nameof(pairs));

        int n = pairs.Count;

        // 1. 중심(centroid)
        double cdx = 0, cdy = 0, cmx = 0, cmy = 0;
        foreach (var (d, m) in pairs) { cdx += d.X; cdy += d.Y; cmx += m.X; cmy += m.Y; }
        cdx /= n; cdy /= n; cmx /= n; cmy /= n;

        // 2~3. 중심화 후 yaw = atan2(Σ(dx·my − dy·mx), Σ(dx·mx + dy·my))
        double sxy = 0, sxx = 0;
        foreach (var (d, m) in pairs)
        {
            double dx = d.X - cdx, dy = d.Y - cdy;
            double mx = m.X - cmx, my = m.Y - cmy;
            sxy += dx * my - dy * mx;
            sxx += dx * mx + dy * my;
        }
        double yaw = Math.Atan2(sxy, sxx);

        // 4. t = c_m − R(yaw)·c_d
        double cos = Math.Cos(yaw), sin = Math.Sin(yaw);
        double tx = cmx - (cos * cdx - sin * cdy);
        double ty = cmy - (sin * cdx + cos * cdy);

        var transform = new DrawingTransform(tx, ty, yaw);

        // 5. 잔차: ‖R·p_d + t − p_m‖
        double sumSq = 0, max = 0;
        foreach (var (d, m) in pairs)
        {
            var (px, py) = transform.DrawingToMap(d.X, d.Y);
            double ex = px - m.X, ey = py - m.Y;
            double err = Math.Sqrt(ex * ex + ey * ey);
            sumSq += err * err;
            if (err > max) max = err;
        }
        double rms = Math.Sqrt(sumSq / n);

        return (transform, rms, max);
    }

    private static double NormalizeAngle(double a)
    {
        // (−π, π] 로 정규화
        double twoPi = 2 * Math.PI;
        a %= twoPi;
        if (a <= -Math.PI) a += twoPi;
        else if (a > Math.PI) a -= twoPi;
        return a;
    }
}
