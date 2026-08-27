using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>영역(Area) 순수 기하 [PHASE2 개정]. 디폴트 정차 pose·경계 판정. 도면 좌표(m).</summary>
public static class AreaGeometry
{
    /// <summary>
    /// standoff 정차점(도면 좌표) — 영역 중심 (uc,vc)의 3D 투영에서 벽 내부향 법선의 **수평 성분** 방향으로
    /// standoffM 만큼 이격. 법선 수평 성분이 없는 면(바닥 B/천장 T)은 이격 없이 중심 투영 폴백(오버라이드 권장).
    /// wallNormal은 ref.wall.normal(내부향 단위 법선, 도면 좌표) — U×V 재계산 대신 DB 값을 신뢰한다.
    /// 반환은 [x,y,z]이며 정차에는 x,y만 의미(z는 참고).
    /// </summary>
    public static double[] StationDrawing(WallPose pose, double uc, double vc, double[] wallNormal, double standoffM)
    {
        var center = pose.LocalToDrawing(uc, vc);
        if (standoffM <= 0 || wallNormal is not { Length: >= 2 }) return center;

        var h = Vec3.Normalize(new[] { wallNormal[0], wallNormal[1], 0.0 });
        if (h is null) return center;   // 수평 성분 0 — 바닥/천장 폴백
        return Vec3.Add(center, Vec3.Scale(h, standoffM));
    }

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

    /// <summary>점 (x,y)가 영역 경계 내부(포함)인지 (축정렬 AABB).</summary>
    public static bool InBounds(double x, double y, double minX, double minY, double maxX, double maxY) =>
        x >= minX && x <= maxX && y >= minY && y <= maxY;

    // ── 임의 다각형(영역 = 4점 사각형) 기하 ──────────────────────────────
    /// <summary>다각형 경계 상자(min/max). 점 배열 각 원소는 [u,v].</summary>
    public static (double MinU, double MinV, double MaxU, double MaxV) Bbox(IReadOnlyList<double[]> poly)
    {
        double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
        foreach (var p in poly)
        {
            if (p is null || p.Length < 2) continue;
            if (p[0] < minU) minU = p[0]; if (p[0] > maxU) maxU = p[0];
            if (p[1] < minV) minV = p[1]; if (p[1] > maxV) maxV = p[1];
        }
        return (minU, minV, maxU, maxV);
    }

    /// <summary>다각형 정점 평균(코너 centroid) — 영역 중심(정차 기준).</summary>
    public static (double U, double V) Centroid(IReadOnlyList<double[]> poly)
    {
        double su = 0, sv = 0; int n = 0;
        foreach (var p in poly)
        {
            if (p is null || p.Length < 2) continue;
            su += p[0]; sv += p[1]; n++;
        }
        return n == 0 ? (0, 0) : (su / n, sv / n);
    }

    /// <summary>점 (x,y)가 다각형 내부(경계 포함)인지 — ray-casting(홀짝) + 변 위 점 포함.</summary>
    public static bool PointInPolygon(double x, double y, IReadOnlyList<double[]> poly, double eps = 1e-9)
    {
        if (poly is null || poly.Count < 3) return false;
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
            // 변 위(경계) 점은 내부로 간주
            if (OnSegment(x, y, xi, yi, xj, yj, eps)) return true;
            bool cross = ((yi > y) != (yj > y)) &&
                         (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
            if (cross) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(double px, double py, double ax, double ay, double bx, double by, double eps)
    {
        // (px,py)가 선분 AB 위에 있는가: 외적≈0 + 경계상자 내
        double cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        if (Math.Abs(cross) > eps * Math.Max(1.0, Math.Abs(bx - ax) + Math.Abs(by - ay))) return false;
        return px >= Math.Min(ax, bx) - eps && px <= Math.Max(ax, bx) + eps
            && py >= Math.Min(ay, by) - eps && py <= Math.Max(ay, by) + eps;
    }
}
