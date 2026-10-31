namespace HD.Acs.Core.Reach;

/// <summary>
/// FAIRINO FR5-WML 표준 DH 기구학(UR 형 6축) — 컨트롤러 없이 순·역기구학을 계산한다(리치·간섭 시뮬레이션용).
/// HD_AMR <c>Fr5Kinematics</c> 와 같은 식·같은 치수다(장비 STEP 축선 실측, 2026-10-31). 길이 mm, 각도 도.
///
/// 관절 규약은 UR 과 같다고 <b>가정</b>한다(전 관절 0 = 팔 수평으로 펴짐). 링크 치수는 STEP 으로 검증했지만
/// 관절 0점·부호는 실물 <c>GetForwardKin</c> 대조 전이다.
/// </summary>
public sealed record Fr5Kinematics(double D1, double A2, double A3, double D4, double D5, double D6)
{
    private const double Deg2Rad = Math.PI / 180.0;
    private static readonly double[] Alpha = { Math.PI / 2, 0, 0, Math.PI / 2, -Math.PI / 2, 0 };

    /// <summary>FR5-WML — d4 = 어깨 가로 185 − 팔꿈치 가로 140 + 손목 114.</summary>
    public static Fr5Kinematics Fr5Wml { get; } = new(180, -970, -816, 159, 114, 106);

    private double[] A => new[] { 0, A2, A3, 0, 0, 0 };
    private double[] D => new[] { D1, 0, 0, D4, D5, D6 };

    private double[,] Link(int i, double theta)
    {
        double ca = Math.Cos(Alpha[i]), sa = Math.Sin(Alpha[i]), ct = Math.Cos(theta), st = Math.Sin(theta);
        double a = A[i], d = D[i];
        return new double[,]
        {
            { ct, -st * ca, st * sa, a * ct },
            { st, ct * ca, -ct * sa, a * st },
            { 0, sa, ca, d },
            { 0, 0, 0, 1 },
        };
    }

    /// <summary>순기구학 — BASE 기준 링크 끝 프레임 T0..T6(T0 = 단위, T6 = 플랜지).</summary>
    public IReadOnlyList<double[,]> Frames(double[] jointsDeg)
    {
        var frames = new List<double[,]>(7) { Mat4.Identity() };
        var m = Mat4.Identity();
        for (var i = 0; i < 6; i++)
        {
            m = Mat4.Multiply(m, Link(i, jointsDeg[i] * Deg2Rad));
            frames.Add(m);
        }
        return frames;
    }

    /// <summary>
    /// 역기구학 — BASE 기준 플랜지 동차변환 → 해 전부(최대 8개, 어깨·팔꿈치·손목 가지)[도, −180~180].
    /// 도달 불가면 빈 목록. 손목 특이점(J5 = 0)에서는 J6 을 0 으로 둔다.
    /// </summary>
    public IReadOnlyList<double[]> InverseFlange(double[,] t)
    {
        var sols = new List<double[]>(8);
        var p5x = t[0, 3] - D6 * t[0, 2];
        var p5y = t[1, 3] - D6 * t[1, 2];
        var r = Math.Sqrt(p5x * p5x + p5y * p5y);
        if (r < Math.Abs(D4)) return sols;
        var inv = Mat4.Inverse(t);

        foreach (var s1 in new[] { 1.0, -1.0 })
        {
            var q1 = Math.Atan2(p5y, p5x) + s1 * Math.Acos(D4 / r) + Math.PI / 2;
            double s1v = Math.Sin(q1), c1v = Math.Cos(q1);
            var c5 = (t[0, 3] * s1v - t[1, 3] * c1v - D4) / D6;
            if (Math.Abs(c5) > 1 + 1e-12) continue;
            c5 = Math.Clamp(c5, -1, 1);

            foreach (var s5 in new[] { 1.0, -1.0 })
            {
                var q5 = s5 * Math.Acos(c5);
                var sin5 = Math.Sin(q5);
                var q6 = Math.Abs(sin5) < 1e-9
                    ? 0.0
                    : Math.Atan2((-inv[1, 0] * s1v + inv[1, 1] * c1v) / sin5, (inv[0, 0] * s1v - inv[0, 1] * c1v) / sin5);

                var t14 = Mat4.Multiply(Mat4.Multiply(Mat4.Inverse(Link(0, q1)), t),
                    Mat4.Inverse(Mat4.Multiply(Link(4, q5), Link(5, q6))));
                double px = t14[0, 3], py = t14[1, 3];
                var c3 = (px * px + py * py - A2 * A2 - A3 * A3) / (2 * A2 * A3);
                if (Math.Abs(c3) > 1 + 1e-12) continue;
                c3 = Math.Clamp(c3, -1, 1);

                foreach (var s3 in new[] { 1.0, -1.0 })
                {
                    var q3 = s3 * Math.Acos(c3);
                    var q2 = -Math.Atan2(py, -px)
                             + Math.Asin(Math.Clamp(A3 * Math.Sin(q3) / Math.Sqrt(px * px + py * py), -1, 1));
                    var t34 = Mat4.Multiply(Mat4.Inverse(Mat4.Multiply(Link(1, q2), Link(2, q3))), t14);
                    var q4 = Math.Atan2(t34[1, 0], t34[0, 0]);
                    sols.Add(new[] { q1, q2, q3, q4, q5, q6 }.Select(q => Wrap(q / Deg2Rad)).ToArray());
                }
            }
        }
        return sols;
    }

    private static double Wrap(double deg)
    {
        deg %= 360.0;
        if (deg > 180) deg -= 360;
        else if (deg <= -180) deg += 360;
        return deg;
    }
}

/// <summary>4×4 동차변환 최소 연산(강체 변환 전용).</summary>
public static class Mat4
{
    public static double[,] Identity() => new double[,] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                double s = 0;
                for (var k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                m[i, j] = s;
            }
        return m;
    }

    /// <summary>강체 역변환: R' = Rᵀ, t' = −Rᵀ·t.</summary>
    public static double[,] Inverse(double[,] t)
    {
        var m = new double[4, 4];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = t[j, i];
        for (var i = 0; i < 3; i++)
            m[i, 3] = -(m[i, 0] * t[0, 3] + m[i, 1] * t[1, 3] + m[i, 2] * t[2, 3]);
        m[3, 3] = 1;
        return m;
    }

    /// <summary>회전 열(x·y·z 축)과 원점으로 변환을 만든다.</summary>
    public static double[,] FromAxes(double[] x, double[] y, double[] z, double[] origin) => new double[,]
    {
        { x[0], y[0], z[0], origin[0] },
        { x[1], y[1], z[1], origin[1] },
        { x[2], y[2], z[2], origin[2] },
        { 0, 0, 0, 1 },
    };

    public static double[] Origin(double[,] t) => new[] { t[0, 3], t[1, 3], t[2, 3] };
    public static double[] AxisZ(double[,] t) => new[] { t[0, 2], t[1, 2], t[2, 2] };
}
