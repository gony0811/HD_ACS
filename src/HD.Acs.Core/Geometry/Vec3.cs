namespace HD.Acs.Core.Geometry;

/// <summary>3D 벡터 순수 함수 (double[3], 도면 좌표 m). [벽면-로컬 좌표 모델 Phase 1]</summary>
public static class Vec3
{
    public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    public static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));

    /// <summary>단위벡터. 길이 &lt;1e-12면 null(방향 불명).</summary>
    public static double[]? Normalize(double[] a)
    {
        double len = Length(a);
        return len < 1e-12 ? null : new[] { a[0] / len, a[1] / len, a[2] / len };
    }
}

/// <summary>축 degenerate(‖U×V‖≈0 또는 축 길이 0) — 벽면 pose 산출 실패. [Phase 1]</summary>
public sealed class WallPoseInvalidException(string message) : Exception(message);

/// <summary>
/// 벽면 pose — 벽면-로컬 2D (u,v) → 도면(CAD) 3D [x,y,z] 강체 매핑. [벽면-로컬 좌표 모델 Phase 1]
/// Origin = 벽면-로컬 (0,0)의 도면 좌표, U/V = 도면 좌표계 기준 벽면-로컬 축(단위벡터, 직교).
/// </summary>
public sealed record WallPose(double[] Origin, double[] U, double[] V)
{
    /// <summary>벽면-로컬 (u,v) → 도면 3D [x,y,z] = Origin + u·U + v·V.</summary>
    public double[] LocalToDrawing(double u, double v) =>
        Vec3.Add(Origin, Vec3.Add(Vec3.Scale(U, u), Vec3.Scale(V, v)));

    /// <summary>벽면 법선(단위) = U×V. 방향 불명이면 예외.</summary>
    public double[] Normal() =>
        Vec3.Normalize(Vec3.Cross(U, V)) ?? throw new WallPoseInvalidException("벽면 법선 산출 불가(U×V≈0).");

    /// <summary>법선의 수평 성분(단위, z=0) — AMR 바닥 정차 방향용(Phase 2). 수평 성분 0이면 예외.</summary>
    public double[] HorizontalNormal()
    {
        var n = Normal();
        return Vec3.Normalize(new[] { n[0], n[1], 0.0 })
            ?? throw new WallPoseInvalidException("벽면 법선의 수평 성분이 0(천장/바닥면?) — 바닥 정차 방향 불명.");
    }

    /// <summary>
    /// 3점에서 정규직교 pose 산출: origin=로컬 원점, pU=+u 방향의 한 점, pV=+v 쪽 한 점.
    /// U = normalize(pU−origin); V = Gram-Schmidt로 (pV−origin)에서 U 성분 제거 후 정규화.
    /// 축이 degenerate면 WallPoseInvalidException.
    /// </summary>
    public static WallPose FromThreePoints(double[] origin, double[] pU, double[] pV)
    {
        var u = Vec3.Normalize(Vec3.Sub(pU, origin))
            ?? throw new WallPoseInvalidException("U축 산출 불가(origin==pU).");
        var vRaw = Vec3.Sub(pV, origin);
        var vOrtho = Vec3.Sub(vRaw, Vec3.Scale(u, Vec3.Dot(vRaw, u)));   // pV의 U 성분 제거
        var v = Vec3.Normalize(vOrtho)
            ?? throw new WallPoseInvalidException("V축 산출 불가(pV가 U축과 평행).");
        return new WallPose(origin, u, v);
    }
}
