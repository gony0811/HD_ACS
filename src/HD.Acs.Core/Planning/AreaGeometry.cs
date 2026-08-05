namespace HD.Acs.Core.Planning;

/// <summary>영역(Area) 순수 기하 [PHASE2 개정]. 디폴트 정차 pose·경계 판정. 도면 좌표(m).</summary>
public static class AreaGeometry
{
    /// <summary>영역 중앙 (디폴트 정차 위치). [정차각 자동화]</summary>
    public static (double X, double Y) AreaCenter(double minX, double minY, double maxX, double maxY) =>
        ((minX + maxX) / 2.0, (minY + maxY) / 2.0);

    /// <summary>정차 방향 yaw 자동 산출 — 정차 위치에서 seam 점들의 중심을 바라보는 각도(도면 기준).
    /// seam은 벽 위에 있으므로 그 방향이 곧 "벽을 바라봄". 정차 위치≈seam 중심(거리²&lt;1e-12)이면 null(불명). [정차각 자동화]</summary>
    public static double? FacingYawToward(double stationX, double stationY, IReadOnlyList<double[]> targets)
    {
        if (targets is null || targets.Count == 0) return null;
        double cx = 0, cy = 0; int n = 0;
        foreach (var p in targets)
        {
            if (p is null || p.Length < 2) continue;
            cx += p[0]; cy += p[1]; n++;
        }
        if (n == 0) return null;
        cx /= n; cy /= n;
        double dx = cx - stationX, dy = cy - stationY;
        if (dx * dx + dy * dy < 1e-12) return null;   // 방향 불명(정차 위치와 seam 중심 일치)
        return Math.Atan2(dy, dx);
    }

    /// <summary>점 (x,y)가 영역 경계 내부(포함)인지.</summary>
    public static bool InBounds(double x, double y, double minX, double minY, double maxX, double maxY) =>
        x >= minX && x <= maxX && y >= minY && y <= maxY;
}
