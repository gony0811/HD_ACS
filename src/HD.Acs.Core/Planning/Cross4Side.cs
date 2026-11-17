using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>
/// CROSS4 좌우 구분 [VDA §8.2 · 1.22] — 면을 정면으로 마주 봤을 때(로봇·카메라 시점) 十자의 수직 용접선이
/// 작업(가로 top–top 한 칸)의 왼쪽에 있으면 <c>CROSS4_L</c>, 오른쪽에 있으면 <c>CROSS4_R</c>.
/// ⚠️ CT1 도면 데이터는 수직선이 모두 작업 정가운데(치우침 0)라 도면만으로는 정해지지 않는다 — 실물 치우침(수 mm)은 도면에 없다.
/// **작업 시작점/끝점 기준이 아니다** — ACS 는 정면 기준 값만 보내고, 용접선 시작→끝 방향이 교시와 반대인 경우의
/// 보정(L/R 바꿔 고르기)은 HD_AMR 이 한다(2026-11-17 협의).
/// 바닥(B)·천장(T)은 "정면"이 없어 정하지 않는다(null — 접미 없는 CROSS4 로 두면 AMR 이 면별 기본값을 쓴다).
/// </summary>
public static class Cross4Side
{
    public const string Left = "CROSS4_L";
    public const string Right = "CROSS4_R";

    /// <summary>CROSS4 계열인가(CROSS4·CROSS4_L·CROSS4_R, legacy CROSS).</summary>
    public static bool IsCross4(string? seamType) =>
        (seamType ?? "").Trim().ToUpperInvariant() is "CROSS4" or Left or Right or "CROSS";

    /// <summary>
    /// 면 정면 기준 "왼쪽" 수평 단위벡터(도면 좌표). 법선 <paramref name="inwardNormal"/>은 면 → 선창 안쪽(로봇 쪽).
    /// 로봇은 −법선을 바라보고, 그 왼쪽 = 바라보는 방향을 반시계 90° 돌린 것. 수평 성분이 없으면(B·T) null.
    /// </summary>
    public static double[]? LeftDir(double[] inwardNormal)
    {
        if (inwardNormal is not { Length: >= 2 }) return null;
        double hx = -inwardNormal[0], hy = -inwardNormal[1];
        double len = Math.Sqrt(hx * hx + hy * hy);
        if (len < 1e-9) return null;
        return new[] { -hy / len, hx / len, 0.0 };
    }

    /// <summary>
    /// 면-로컬 방향 (Δu, Δv) 가 정면에서 왼쪽(+1)·오른쪽(−1)인가. 수평이 아니면(세로 성분이 더 크면) 0, B·T 면 0.
    /// </summary>
    public static int HorizontalSign(double du, double dv, double[] acsU, double[] acsV, double[] inwardNormal)
    {
        if (LeftDir(inwardNormal) is not { } left) return 0;
        var d = Vec3.Add(Vec3.Scale(acsU, du), Vec3.Scale(acsV, dv));
        double len = Math.Sqrt(Vec3.Dot(d, d));
        if (len < 1e-9) return 0;
        double s = Vec3.Dot(d, left) / len;
        return Math.Abs(s) < 0.7 ? 0 : (s > 0 ? 1 : -1);   // 45° 넘게 기운(세로) 방향은 좌우를 말하지 않는다
    }

    /// <summary>
    /// 수직 용접선의 u 위치(<paramref name="verticalU"/>)가 가로 작업의 가운데에서 정면 기준 어느 쪽으로 치우쳤는지로 L/R 을 정한다.
    /// 치우침이 <paramref name="minOffsetM"/> 보다 작으면(도면상 가운데) null — 도면만으로는 정할 수 없다.
    /// 작업이 가로가 아니거나 B·T 면이면 null.
    /// </summary>
    public static string? SideOfVertical(double startU, double startV, double endU, double endV, double verticalU,
        double[] acsU, double[] acsV, double[] inwardNormal, double minOffsetM = 0.01)
    {
        if (HorizontalSign(endU - startU, endV - startV, acsU, acsV, inwardNormal) == 0) return null;
        double off = verticalU - (startU + endU) / 2;
        if (Math.Abs(off) < minOffsetM) return null;
        return HorizontalSign(off, 0, acsU, acsV, inwardNormal) switch
        {
            1 => Left,
            -1 => Right,
            _ => null,
        };
    }

    /// <summary>
    /// 가로 작업을 가로지르는 수직 용접선의 u — 같은 면의 세로 작업 중 u 가 작업 구간 안에 있고, 끝점이 작업 높이에서
    /// <paramref name="reachM"/> 안에 있는 것. 그런 u 가 하나로 모이지 않으면(없음·여럿) null.
    /// </summary>
    public static double? CrossingVerticalU(double startU, double startV, double endU, double endV,
        IEnumerable<(double StartU, double StartV, double EndU, double EndV)> others, double reachM = 0.4, double tolM = 0.005)
    {
        double lo = Math.Min(startU, endU) + tolM, hi = Math.Max(startU, endU) - tolM, v = (startV + endV) / 2;
        var us = others
            .Where(o => Math.Abs(o.StartU - o.EndU) <= tolM && o.StartU > lo && o.StartU < hi
                        && (Math.Abs(o.StartV - v) <= reachM || Math.Abs(o.EndV - v) <= reachM))
            .Select(o => o.StartU).ToList();
        if (us.Count == 0) return null;
        return us.Max() - us.Min() <= tolM ? us.Average() : null;
    }
}
