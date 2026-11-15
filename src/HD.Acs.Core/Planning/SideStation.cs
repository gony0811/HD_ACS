namespace HD.Acs.Core.Planning;

/// <summary>
/// 측면 정차 검사 규칙 [VDA §4.4.3 / N25] — 순수 함수.
/// AMR 이 벽과 나란히 서서 코봇 J1 만 ±90° 돌려 검사한다. 방식은 층+벽면 단위 설정(<see cref="ModeFront"/>·<see cref="ModeSide"/>),
/// 좌/우(<see cref="SideLeft"/>·<see cref="SideRight"/>)는 배차 때 고른 theta 기준으로 벽이 있는 쪽이다.
/// 각도는 모두 같은 프레임(맵 또는 도면)이면 된다 — 호출자가 맞춘다.
/// </summary>
public static class SideStation
{
    // 층+벽면 설정 값
    public const string ModeFront = "FRONT";
    public const string ModeSide = "SIDE";
    /// <summary>기본 방향 — 면 +u 축(수평 성분)을 따라 / 반대로.</summary>
    public const string DirUPlus = "U_PLUS";
    public const string DirUMinus = "U_MINUS";

    // VDA params.stationMode 값
    public const string Front = "FRONT";
    public const string SideLeft = "SIDE_LEFT";
    public const string SideRight = "SIDE_RIGHT";

    /// <summary>방향이 분명하다고 보는 최소 성분(이동 방향 단위벡터 · 벽 접선). 0.5 = 접선에서 60° 이내.</summary>
    public const double MinAlongCos = 0.5;
    /// <summary>이보다 가까우면 이동 방향을 따지지 않고 기본 방향(제자리·바로 옆).</summary>
    public const double MinTravelM = 0.3;

    public static bool IsValidMode(string? m) => m is ModeFront or ModeSide;
    public static bool IsValidDir(string? d) => d is DirUPlus or DirUMinus;

    /// <summary>측면 정차는 수직벽 PM·SM·F·A 에만 쓴다(HD_AMR 통보 2026-11-15) — 바닥·천장은 벽 방향이 없고,
    /// 챔퍼(SL·PL·SU·PU)는 HD_AMR 이 측면 자세를 지원하지 않아 SIDE 가 오면 Order 를 거부한다.</summary>
    public static bool WallAllowsSide(string wallCode) => wallCode is "PM" or "SM" or "F" or "A";

    /// <summary>
    /// 측면 정차 theta — 벽과 나란한 두 방향(<paramref name="tangentYaw"/>, +π) 중 로봇→정차 방향에 가까운 쪽.
    /// 이동 거리가 짧거나 벽과 거의 수직으로 다가오면 <paramref name="defaultDir"/>(U_PLUS = tangentYaw).
    /// 로봇 위치를 모르면(null) 기본 방향.
    /// </summary>
    public static double ChooseHeading(double tangentYaw, double stationX, double stationY,
        double? robotX, double? robotY, string? defaultDir)
    {
        double plus = Normalize(tangentYaw), minus = Normalize(tangentYaw + Math.PI);
        double fallback = defaultDir == DirUMinus ? minus : plus;
        if (robotX is not double rx || robotY is not double ry) return fallback;
        double dx = stationX - rx, dy = stationY - ry, dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < MinTravelM) return fallback;
        double along = (dx * Math.Cos(tangentYaw) + dy * Math.Sin(tangentYaw)) / dist;
        if (Math.Abs(along) < MinAlongCos) return fallback;
        return along > 0 ? plus : minus;
    }

    /// <summary>
    /// theta 기준으로 벽이 어느 쪽인지. <paramref name="inwardNormalYaw"/> = 면 내부향 법선(벽 → 선창 안쪽)의 수평 방향.
    /// 벽은 로봇에서 법선 반대쪽에 있다. 왼쪽(theta+90°)이 벽 쪽이면 SIDE_LEFT.
    /// </summary>
    public static string SideOf(double headingYaw, double inwardNormalYaw)
    {
        double toWall = inwardNormalYaw + Math.PI;
        double leftDot = Math.Cos(headingYaw + Math.PI / 2) * Math.Cos(toWall) + Math.Sin(headingYaw + Math.PI / 2) * Math.Sin(toWall);
        return leftDot >= 0 ? SideLeft : SideRight;
    }

    /// <summary>heading 이 벽 접선과 이루는 각 [deg, 0~90] — 수동 저장 theta 가 벽과 나란한지 확인용.</summary>
    public static double DeviationFromTangentDeg(double headingYaw, double tangentYaw)
    {
        double d = Math.Abs(Normalize(headingYaw - tangentYaw));
        if (d > Math.PI / 2) d = Math.PI - d;
        return d * 180.0 / Math.PI;
    }

    public static double Normalize(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
