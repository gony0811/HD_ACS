namespace HD.Acs.Core.Geometry;

/// <summary>선형부 2×2 행렬 — 맵 = A·도면 + t 의 A. affine 캘리브레이션에서만 쓴다.</summary>
public sealed record AffineLinear(double A11, double A12, double A21, double A22)
{
    public double Det => A11 * A22 - A12 * A21;
}

/// <summary>
/// 층별 도면→맵 2D 변환 [PHASE2 §T_W_D]. 맵 좌표 = A·도면좌표 + t.
/// 기본은 강체(A = R(yaw), 스케일 없음). <see cref="Affine"/> 가 있으면 그 행렬이 A 이고(축별 축척·전단 포함 —
/// SLAM 맵이 실제보다 늘거나 줄어든 층을 흡수), <see cref="YawRad"/> 는 A 에 가장 가까운 회전각(표시·요약용)이다.
/// yaw 는 맵 X축 기준 CCW [rad].
/// </summary>
public sealed record DrawingTransform(double Tx, double Ty, double YawRad, AffineLinear? Affine = null)
{
    public bool IsAffine => Affine is not null;

    private (double A11, double A12, double A21, double A22) Linear()
    {
        if (Affine is { } a) return (a.A11, a.A12, a.A21, a.A22);
        var (cos, sin) = (Math.Cos(YawRad), Math.Sin(YawRad));
        return (cos, -sin, sin, cos);
    }

    /// <summary>도면 좌표 → 맵 좌표. A·d + t</summary>
    public (double X, double Y) DrawingToMap(double dx, double dy)
    {
        var (a11, a12, a21, a22) = Linear();
        return (a11 * dx + a12 * dy + Tx,
                a21 * dx + a22 * dy + Ty);
    }

    /// <summary>맵 좌표 → 도면 좌표. A⁻¹·(m − t) (강체면 A⁻¹ = Rᵀ)</summary>
    public (double X, double Y) MapToDrawing(double mx, double my)
    {
        double ux = mx - Tx, uy = my - Ty;
        if (Affine is not { } a)
        {
            var (cos, sin) = (Math.Cos(YawRad), Math.Sin(YawRad));
            return (cos * ux + sin * uy,
                    -sin * ux + cos * uy);
        }
        double det = a.Det;
        return ((a.A22 * ux - a.A12 * uy) / det,
                (-a.A21 * ux + a.A11 * uy) / det);
    }

    /// <summary>
    /// 도면 좌표계 방향(yaw) → 맵 좌표계 방향, (−π, π]. 강체는 yaw 합성. affine 은 방향 벡터에 A 를 적용한
    /// 각도 — 축척이 축마다 다르면 방향에 따라 회전량이 조금 달라지기 때문이다.
    /// </summary>
    public double DrawingYawToMap(double drawingYaw)
    {
        if (Affine is not { } a) return NormalizeAngle(drawingYaw + YawRad);
        double c = Math.Cos(drawingYaw), s = Math.Sin(drawingYaw);
        return Math.Atan2(a.A21 * c + a.A22 * s, a.A11 * c + a.A12 * s);
    }

    /// <summary>맵 좌표계 방향(yaw) → 도면 좌표계 방향. DrawingYawToMap의 역 — (−π, π]로 정규화 [SAIGE §9.2].</summary>
    public double MapYawToDrawing(double mapYaw)
    {
        if (Affine is not { } a) return NormalizeAngle(mapYaw - YawRad);
        double c = Math.Cos(mapYaw), s = Math.Sin(mapYaw);
        return Math.Atan2(-a.A21 * c + a.A11 * s, a.A22 * c - a.A12 * s);   // det > 0 이라 부호 불변
    }

    /// <summary>방향(법선) 벡터 회전 — 평행이동 없이 R(yaw)만 적용. R·v [PHASE2 §4.2 wallNormalW].</summary>
    public (double X, double Y) RotateDirection(double x, double y)
    {
        var (cos, sin) = (Math.Cos(YawRad), Math.Sin(YawRad));
        return (cos * x - sin * y, sin * x + cos * y);
    }

    /// <summary>축척의 최소·최대(선형부 특이값). 강체는 (1, 1). 1 에서 멀수록 맵이 도면과 크기가 다르다.</summary>
    public (double Min, double Max) Scales()
    {
        if (Affine is not { } a) return (1, 1);
        double e = (a.A11 + a.A22) / 2, f = (a.A11 - a.A22) / 2, g = (a.A21 + a.A12) / 2, h = (a.A21 - a.A12) / 2;
        double q = Math.Sqrt(e * e + h * h), r = Math.Sqrt(f * f + g * g);
        return (Math.Abs(q - r), q + r);
    }

    /// <summary>
    /// 도면 좌표 프레임 회전 성분. 기준점의 도면 좌표를 원점 기준으로 deg 만큼 돌릴 때 쓴다 —
    /// 도면 좌표를 돌아간 프레임으로 입력해 저장된 캘리브레이션(흔한 사례: 180° 반전)을
    /// 현장 재측정 없이 교정하는 경로. 90° 배수는 부동소수 먼지가 남지 않도록 정확한 정수로 고정한다.
    /// </summary>
    public static (double Cos, double Sin) RotationCosSin(double deg)
    {
        double m = ((deg % 360) + 360) % 360;
        if (m == 0) return (1, 0);
        if (m == 90) return (0, 1);
        if (m == 180) return (-1, 0);
        if (m == 270) return (0, -1);
        double r = deg * Math.PI / 180;
        return (Math.Cos(r), Math.Sin(r));
    }

    /// <summary>도면 좌표를 원점 기준으로 deg 회전. 맵 좌표는 측정값이므로 건드리지 않는다.</summary>
    public static (double X, double Y) RotateDrawing(double x, double y, double deg)
    {
        var (c, s) = RotationCosSin(deg);
        return (c * x - s * y, s * x + c * y);
    }

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

    /// <summary>affine 산출에 필요한 최소 대응쌍 수. 3쌍이면 항상 잔차 0 으로 맞아 틀린 점을 가려내지 못한다.</summary>
    public const int AffineMinPairs = 4;

    /// <summary>
    /// 대응쌍 최소자승 (2D affine — 축별 축척·전단 허용). 맵 = A·도면 + t.
    /// <see cref="AffineMinPairs"/> 쌍 미만, 기준점이 한 줄로 늘어선 경우(폭 0.5 m 미만), 거울상(det ≤ 0)이면 예외.
    /// </summary>
    public static (DrawingTransform T, double RmsM, double MaxResidualM) SolveAffine(
        IReadOnlyList<((double X, double Y) Drawing, (double X, double Y) Map)> pairs)
    {
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));
        if (pairs.Count < AffineMinPairs)
            throw new ArgumentException($"affine 변환 산출에는 최소 {AffineMinPairs}개의 대응쌍이 필요합니다(현재 {pairs.Count}개).", nameof(pairs));

        int n = pairs.Count;
        double cdx = 0, cdy = 0, cmx = 0, cmy = 0;
        foreach (var (d, m) in pairs) { cdx += d.X; cdy += d.Y; cmx += m.X; cmy += m.Y; }
        cdx /= n; cdy /= n; cmx /= n; cmy /= n;

        // 정규방정식: A = (Σ m·dᵀ)(Σ d·dᵀ)⁻¹ (중심화 좌표)
        double sxx = 0, sxy = 0, syy = 0, mxx = 0, mxy = 0, myx = 0, myy = 0;
        foreach (var (d, m) in pairs)
        {
            double dx = d.X - cdx, dy = d.Y - cdy, mx = m.X - cmx, my = m.Y - cmy;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            mxx += mx * dx; mxy += mx * dy; myx += my * dx; myy += my * dy;
        }
        // 기준점 퍼짐: 공분산의 작은 고유값 → 좁은 쪽 표준편차
        double tr = sxx + syy, dt = sxx * syy - sxy * sxy;
        double minEig = tr / 2 - Math.Sqrt(Math.Max(0, tr * tr / 4 - dt));
        double minSpread = Math.Sqrt(Math.Max(0, minEig) / n);
        if (minSpread < 0.5)
            throw new ArgumentException(
                $"기준점이 한 줄에 가깝게 놓여 있어 affine 변환을 정할 수 없습니다(좁은 쪽 퍼짐 {minSpread:F2} m). 길이·폭 양쪽으로 넓게 배치하세요.", nameof(pairs));

        double a11 = (mxx * syy - mxy * sxy) / dt, a12 = (mxy * sxx - mxx * sxy) / dt;
        double a21 = (myx * syy - myy * sxy) / dt, a22 = (myy * sxx - myx * sxy) / dt;
        var lin = new AffineLinear(a11, a12, a21, a22);
        if (lin.Det <= 0)
            throw new ArgumentException("기준점 대응이 거울상입니다(좌우가 뒤집힘) — 도면 좌표의 좌현/우현 부호를 확인하세요.", nameof(pairs));

        double tx = cmx - (a11 * cdx + a12 * cdy), ty = cmy - (a21 * cdx + a22 * cdy);
        var transform = new DrawingTransform(tx, ty, Math.Atan2(a21 - a12, a11 + a22), lin);
        var (rms, max) = Residuals(transform, pairs);
        return (transform, rms, max);
    }

    /// <summary>
    /// 한 점씩 빼고 푼 변환으로 그 점을 예측했을 때의 오차 RMS. 변환이 기준점에 과하게 맞춰졌는지 보는 값 —
    /// 등록 잔차보다 훨씬 크면 기준점이 모자라다. 풀 수 없는 점 수(강체 3 미만·affine 5 미만)거나
    /// 한 점을 뺀 배치가 풀리지 않으면 null.
    /// </summary>
    public static double? LeaveOneOutRms(
        IReadOnlyList<((double X, double Y) Drawing, (double X, double Y) Map)> pairs, bool affine)
    {
        if (pairs is null || pairs.Count < (affine ? AffineMinPairs + 1 : 3)) return null;
        double sumSq = 0;
        for (int k = 0; k < pairs.Count; k++)
        {
            var rest = pairs.Where((_, i) => i != k).ToList();
            DrawingTransform t;
            try { t = affine ? SolveAffine(rest).T : Solve(rest).T; }
            catch (ArgumentException) { return null; }
            var (px, py) = t.DrawingToMap(pairs[k].Drawing.X, pairs[k].Drawing.Y);
            double ex = px - pairs[k].Map.X, ey = py - pairs[k].Map.Y;
            sumSq += ex * ex + ey * ey;
        }
        return Math.Sqrt(sumSq / pairs.Count);
    }

    private static (double Rms, double Max) Residuals(DrawingTransform t,
        IReadOnlyList<((double X, double Y) Drawing, (double X, double Y) Map)> pairs)
    {
        double sumSq = 0, max = 0;
        foreach (var (d, m) in pairs)
        {
            var (px, py) = t.DrawingToMap(d.X, d.Y);
            double err = Math.Sqrt((px - m.X) * (px - m.X) + (py - m.Y) * (py - m.Y));
            sumSq += err * err;
            if (err > max) max = err;
        }
        return (Math.Sqrt(sumSq / pairs.Count), max);
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
