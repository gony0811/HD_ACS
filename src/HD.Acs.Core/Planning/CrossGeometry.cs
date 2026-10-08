using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>
/// CROSS3/CROSS4 교차 형상의 회전·가지 좌표를 **AMR 면-로컬 프레임**에서 산출한다.
/// [VDA5050_INTERFACE_SPEC §8.5.1, N13 확정안 2026-10-07 — HD_AMR 회신]
///
/// ⚠️ 핵심: ACS가 저장하는 면 축(<see cref="GeneratedWall"/>의 WallPose.U/V = 전개도 프레임)은
/// AMR 카메라-시점 면-로컬 프레임과 **손잡이(handedness)가 다르다** — V축은 전 면에서 반대,
/// U축은 좌현(PL/PM/PU)·마구리(F/A)에서 반대다(ACS는 긴 면 U를 항상 +x로 통일하기 때문).
/// 따라서 회전·점 순서를 ACS 저장 U/V로 계산하면 좌현·마구리에서 부호가 뒤집혀 **조용히 틀린다**.
/// 반드시 면 법선 N에서 AMR 프레임(u=(−sinθ,cosθ), v=n×u)을 재구성한 뒤 그 안에서 계산한다.
/// </summary>
public sealed record AmrFaceFrame(double[] U, double[] V, double[] N)
{
    /// <summary>
    /// 면 외향 법선 N(=AMR n = ACS <see cref="GeneratedWall.Normal"/>, AMR/카메라를 향함)에서
    /// AMR 면-로컬 프레임을 재구성한다. θ=atan2(−Ny,−Nx)(벽 정면), u=(−sinθ,cosθ), v=n×u.
    /// 수평 법선이 없으면(바닥 B·천장 T) AMR u가 정의되지 않으므로 <c>null</c> — B/T 위 CROSS는
    /// 별도 u축 합의 전까지 범위 밖(§8.5.1 잔여 역질의).
    /// </summary>
    public static AmrFaceFrame? FromNormal(double[] n)
    {
        var N = Vec3.Normalize(n);
        if (N is null) return null;
        double nh = Math.Sqrt(N[0] * N[0] + N[1] * N[1]);
        if (nh < 1e-9) return null;   // 바닥/천장 — AMR u 미정의
        double th = Math.Atan2(-N[1], -N[0]);
        var u = new[] { -Math.Sin(th), Math.Cos(th), 0.0 };
        var v = Vec3.Cross(N, u);     // n × u (오른손 프레임 — u×v=n)
        return new AmrFaceFrame(u, v, N);
    }

    /// <summary>
    /// ACS 면-로컬 방향벡터(Δu_acs·Δv_acs, 축 <paramref name="acsU"/>/<paramref name="acsV"/>)를
    /// AMR 면-로컬 성분 (u,v)로 변환한다. 길이 보존(두 프레임 모두 면내 정규직교).
    /// </summary>
    public (double U, double V) FromAcsDir(double duAcs, double dvAcs, double[] acsU, double[] acsV)
    {
        var d = Vec3.Add(Vec3.Scale(acsU, duAcs), Vec3.Scale(acsV, dvAcs));
        return (Vec3.Dot(d, U), Vec3.Dot(d, V));
    }
}

/// <summary>교차 형상(CROSS3/CROSS4) 순수 기하 — 전부 AMR 면-로컬 (u,v) 성분 기준.</summary>
public static class CrossGeometry
{
    /// <summary>
    /// 줄기(교차 중심 → 줄기 끝) AMR 성분 (su,sv) → <c>CROSS3_R0/R90/R180/R270</c>.
    /// R0 = 줄기가 +u(§8.5.1 (1)), 회전은 법선 둘레 CCW(+)(§8.5.1 (2)). 가장 가까운 90°에 스냅.
    /// 길이 ≈0(줄기 불명)이면 null. <paramref name="snapResidualDeg"/>=스냅 전 90° 격자와의 오차(출력).
    /// </summary>
    public static string? Cross3Rotation(double su, double sv, out double snapResidualDeg)
    {
        snapResidualDeg = double.NaN;
        if (Math.Sqrt(su * su + sv * sv) < 1e-9) return null;
        double deg = Math.Atan2(sv, su) * 180.0 / Math.PI;    // +u 기준 CCW
        double norm = ((deg % 360) + 360) % 360;
        int q = (int)Math.Round(norm / 90.0) % 4;             // 0,1,2,3
        snapResidualDeg = Math.Abs(((norm - q * 90 + 540) % 360) - 180);
        return q switch { 0 => "CROSS3_R0", 1 => "CROSS3_R90", 2 => "CROSS3_R180", _ => "CROSS3_R270" };
    }

    /// <summary>
    /// 가지 끝점들(AMR 성분 (u,v))을 +u 기준 CCW 순으로 정렬한다 — params.points 발행 순서.
    /// CROSS4 = +u→+v→−u→−v 순이 되고(§8.5.1 (3)), 동률 각은 입력 순서 유지(안정 정렬).
    /// </summary>
    public static IReadOnlyList<(double U, double V)> OrderCcw(IEnumerable<(double U, double V)> pts) =>
        pts.Select((p, i) => (p, i))
           .OrderBy(x => Ccw(x.p.U, x.p.V)).ThenBy(x => x.i)
           .Select(x => x.p).ToList();

    private static double Ccw(double u, double v)
    {
        double a = Math.Atan2(v, u) * 180.0 / Math.PI;
        return ((a % 360) + 360) % 360;   // [0,360): +u=0, +v=90, −u=180, −v=270
    }

    /// <summary>
    /// 저장된 ACS 면-로컬 가지 끝점(미터, 중심=<paramref name="centerU"/>/<paramref name="centerV"/>)을
    /// **AMR 면-로컬 (u,v) mm·§8.5.1 규약 순서**의 `params.points` 로 변환한다.
    /// CROSS4 = +u→+v→−u→−v (CCW). CROSS3_R* = [줄기 끝 · 통과선 +(줄기+90°) · 통과선 −(줄기−90°)],
    /// 저장 규약상 <paramref name="armsAcs"/>[0]=줄기. LINE/CORNER·프레임 미정의(B/T)·빈 입력 = null(미발행).
    /// </summary>
    public static double[][]? BuildAmrPoints(string? seamType, double centerU, double centerV,
        IReadOnlyList<double[]> armsAcs, double[] acsU, double[] acsV, double[] normal)
    {
        if (armsAcs is null || armsAcs.Count == 0) return null;
        var st = (seamType ?? "").Trim().ToUpperInvariant();
        bool isCross3 = st.StartsWith("CROSS3", StringComparison.Ordinal);
        bool isCross4 = st is "CROSS4" or "CROSS";
        if (!isCross3 && !isCross4) return null;

        var frame = AmrFaceFrame.FromNormal(normal);
        if (frame is null) return null;   // 바닥/천장 — 범위 밖

        var amr = armsAcs
            .Where(p => p is { Length: >= 2 })
            .Select(p =>
            {
                var (au, av) = frame.FromAcsDir(p[0] - centerU, p[1] - centerV, acsU, acsV);
                return (U: au * 1000.0, V: av * 1000.0);
            }).ToList();
        if (amr.Count == 0) return null;

        if (isCross4)
            return OrderCcw(amr).Select(p => new[] { p.U, p.V }).ToArray();

        // CROSS3: 줄기 + 통과선 2가지 → [줄기, +(줄기+90°), −(줄기−90°)]
        var stem = amr[0];
        var through = amr.Skip(1).ToList();
        if (through.Count < 2) return amr.Select(p => new[] { p.U, p.V }).ToArray();   // 방어: 그대로
        double stemAng = Ccw(stem.U, stem.V);
        double Rel(double u, double v) => ((Ccw(u, v) - stemAng + 540) % 360) - 180;   // (−180,180]
        var plus = through.OrderByDescending(p => Rel(p.U, p.V)).First();   // ≈ +90
        var minus = through.OrderBy(p => Rel(p.U, p.V)).First();            // ≈ −90
        return new[] { stem, plus, minus }.Select(p => new[] { p.U, p.V }).ToArray();
    }

    /// <summary>교차 그리기 미리보기 결과 — 서버가 UI에 돌려주는 유도 회전·정렬 점.</summary>
    public readonly record struct CrossPreviewOutcome(string SeamType, double[][]? Points, double SnapResidualDeg, bool FrameOk);

    /// <summary>
    /// 운영자가 그린 중심·가지(ACS 면-로컬)에서 **회전 유도 + AMR 점 정렬**을 한 번에 [VDA §8.5.1, N13].
    /// CROSS3 계열은 줄기(arms[0])로 `CROSS3_R*`를 유도, CROSS4는 seamType 유지. 바닥/천장(프레임 미정의)=FrameOk false.
    /// /cross-preview 엔드포인트와 발행 경로가 공유하는 정본 — 화면 회전 = 실제 발행 회전.
    /// </summary>
    public static CrossPreviewOutcome Preview(string? seamType, double centerU, double centerV,
        IReadOnlyList<double[]> arms, double[] acsU, double[] acsV, double[] normal)
    {
        var frame = AmrFaceFrame.FromNormal(normal);
        if (frame is null) return new CrossPreviewOutcome(seamType ?? "", null, 0, false);

        var st = (seamType ?? "").Trim().ToUpperInvariant();
        string derived = st;
        double residual = 0;
        if (st.StartsWith("CROSS3", StringComparison.Ordinal) && arms is { Count: > 0 } && arms[0] is { Length: >= 2 } stem)
        {
            var (su, sv) = frame.FromAcsDir(stem[0] - centerU, stem[1] - centerV, acsU, acsV);
            derived = Cross3Rotation(su, sv, out residual) ?? st;
        }
        var points = BuildAmrPoints(derived, centerU, centerV, arms, acsU, acsV, normal);
        return new CrossPreviewOutcome(derived, points, residual, true);
    }
}
