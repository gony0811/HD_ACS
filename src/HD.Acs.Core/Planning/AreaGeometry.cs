using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>영역(Area) 순수 기하 [PHASE2 개정]. 디폴트 정차 pose·경계 판정. 도면 좌표(m).</summary>
public static class AreaGeometry
{
    /// <summary>AREA 가로(u) 최대 범위 — 모든 면 1440 mm(결정 001: 코봇을 좌우로 멀리 뻗으면 J5·J6 부하·진동).</summary>
    public const double MaxSpanM = 1.44;

    /// <summary>
    /// 경사면·수직벽(SL·PL·SM·PM·SU·PU)의 AREA 세로(v) 최대 범위. 이 면들의 v 는 높이 방향이라 결정 001 의
    /// 가로 제약에 해당하지 않는다(높이는 리프트 + 코봇 도달로 덮음). 2.9 m = 2026-11-03 CT1 면·층별 후보 평가
    /// (리치 조사·정차점 최적화·영역 리프트 고정)에서 채택한 최대값(PM L3·SU L4). 바닥·천장(v 도 수평)·
    /// 격벽 F/A(팔각 모서리에 작업이 걸림)는 1440 mm 그대로.
    /// </summary>
    public const double MaxSpanSideVM = 2.9;

    /// <summary>면의 AREA 세로(v) 최대 범위 [m]. 면 코드를 모르면 1440 mm.</summary>
    public static double MaxSpanV(string? wallCode) =>
        wallCode is "SL" or "PL" or "SM" or "PM" or "SU" or "PU" ? MaxSpanSideVM : MaxSpanM;

    /// <summary>벽면 로컬 좌표의 축별 AREA 최대 범위 — u 1440 mm, v 는 <see cref="MaxSpanV"/>.</summary>
    public static bool WithinMaxSize(double minU, double minV, double maxU, double maxV, string? wallCode = null) =>
        maxU - minU <= MaxSpanM + 1e-9 && maxV - minV <= MaxSpanV(wallCode) + 1e-9;

    /// <summary>최대 범위 안내 문구 — 서버 400 사유·UI 사전 검사 공통.</summary>
    public static string MaxSizeText(string? wallCode) =>
        MaxSpanV(wallCode) > MaxSpanM
            ? $"AREA 최대 크기는 벽면 로컬 u 1.44m · v {MaxSpanV(wallCode):0.0#}m입니다(면 {wallCode})."
            : "AREA 최대 크기는 벽면 로컬 u/v 각 1.44m(1440mm)입니다.";

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

    /// <summary>
    /// 면 종류별 기본 정차 이격 [m] — 수직벽(법선의 수직 성분 ≈ 0: A·F·SM·PM)은 <paramref name="verticalM"/>,
    /// 경사면(SL·PL·SU·PU)은 <paramref name="inclinedM"/>. 바닥·천장은 이격이 적용되지 않으므로 무엇을 돌려도 같다.
    /// 수직벽을 따로 두는 이유: FR5-WML 오프라인 리치 분석(2026-10-31)에서 0.8m 는 수직벽 용접선이 하나도 닿지 않았고
    /// 1.2m 에서 전부 닿았다(플랜지 뻗음 부족·팔 과접힘). 경사면은 필요한 이동이 0~700mm 로 흩어져 기존값을 유지한다.
    /// </summary>
    public static double DefaultStandoffM(double[]? wallNormal, double verticalM, double inclinedM)
        => wallNormal is { Length: >= 3 } && Math.Abs(wallNormal[2]) < 0.1 ? verticalM : inclinedM;

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

    /// <summary>
    /// 용접선 점 목록(시작·끝·교차 가지)을 감싸는 축정렬 사각형 4점 코너 [[minU,minV],[maxU,minV],[maxU,maxV],[minU,maxV]] 생성.
    /// 여백(<paramref name="padM"/>, 기본 50mm)을 양쪽에 두되 AREA 최대 크기(<see cref="MaxSpanM"/>·<see cref="MaxSpanV"/>)와
    /// 허용 범위([uLo,uHi]×[vLo,vHi])를 넘지 않게 자른다. 수평·수직 단일 선분도 면적이 0이 되지 않는다.
    /// </summary>
    public static double[][] BoundingCornersFromPoints(
        IReadOnlyList<double[]> points, string? wallCode,
        double uLo, double vLo, double uHi, double vHi, double padM = 0.05)
    {
        var (rawMinU, rawMinV, rawMaxU, rawMaxV) = Bbox(points);
        var (minU, maxU) = ExpandAxis(rawMinU, rawMaxU, uLo, uHi, MaxSpanM, padM);
        var (minV, maxV) = ExpandAxis(rawMinV, rawMaxV, vLo, vHi, MaxSpanV(wallCode), padM);
        return new[]
        {
            new[] { minU, minV },
            new[] { maxU, minV },
            new[] { maxU, maxV },
            new[] { minU, maxV },
        };

        static (double Min, double Max) ExpandAxis(double rawMin, double rawMax, double lo, double hi, double maxSpan, double pad)
        {
            var span = rawMax - rawMin;
            if (span >= maxSpan) return (rawMin, rawMax);
            var half = Math.Min(pad, (maxSpan - span) / 2.0);
            var min = Math.Max(lo, rawMin - half);
            var max = Math.Min(hi, rawMax + half);
            if (max - min < Math.Min(0.02, Math.Max(0, hi - lo)))
            {
                var want = Math.Min(Math.Max(pad, 0.02), Math.Min(maxSpan, Math.Max(0, hi - lo)));
                if (min - lo < hi - max) max = Math.Min(hi, min + want);
                else min = Math.Max(lo, max - want);
            }
            min = Math.Min(rawMin, Math.Round(min, 4));
            max = Math.Max(rawMax, Math.Round(max, 4));
            return (min, max);
        }
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
