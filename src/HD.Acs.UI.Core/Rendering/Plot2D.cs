using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

/// <summary>
/// 전개도(2D 캔버스) 투영 공용 함수 — 프레임워크 중립.
/// (u,v) 면-로컬 좌표 → 캔버스 px 변환은 호출자가 proj로 넘기고, 여기서는 폴리곤 단위 변환·라벨 앵커만 담당.
/// AreaPlanningViewModel(계획 캔버스)·TankViewModel(운영 전개도 셀)이 공유한다.
/// </summary>
public static class Plot2D
{
    /// <summary>(u,v) 튜플 폴리곤을 캔버스 px 폴리곤으로 투영.</summary>
    public static Pt2[] ProjectPolygon(IReadOnlyList<(double u, double v)> uv, Func<double, double, (double x, double y)> proj)
    {
        var pts = new Pt2[uv.Count];
        for (int i = 0; i < uv.Count; i++)
        {
            var (x, y) = proj(uv[i].u, uv[i].v);
            pts[i] = new Pt2(x, y);
        }
        return pts;
    }

    /// <summary>[u,v] 배열 코너(영역 corners 형식)를 캔버스 px 폴리곤으로 투영. 길이 2 미만 원소는 건너뛴다.</summary>
    public static Pt2[] ProjectCorners(IEnumerable<double[]> corners, Func<double, double, (double x, double y)> proj)
    {
        var list = new List<Pt2>();
        foreach (var p in corners)
        {
            if (p is not { Length: >= 2 }) continue;
            var (x, y) = proj(p[0], p[1]);
            list.Add(new Pt2(x, y));
        }
        return list.ToArray();
    }

    /// <summary>정점 평균(라벨 앵커). 빈 폴리곤이면 (0,0).</summary>
    public static Pt2 Centroid(IReadOnlyList<Pt2> pts)
    {
        if (pts.Count == 0) return default;
        double cx = 0, cy = 0;
        foreach (var p in pts) { cx += p.X; cy += p.Y; }
        return new Pt2(cx / pts.Count, cy / pts.Count);
    }
}
