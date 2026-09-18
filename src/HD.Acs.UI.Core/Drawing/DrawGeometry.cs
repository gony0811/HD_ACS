using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Drawing;

/// <summary>
/// 면 드로잉의 순수 기하 — 격자 계산·엣지 스냅(ALT)·직교 구속(SHIFT)·최근접 꼭짓점(HUD).
/// UI/프레임워크 의존 없음(테스트 대상). 좌표는 실좌표(mm), y는 위로 증가.
/// </summary>
public static class DrawGeometry
{
    /// <summary>표 모드 안전 상한 — 미리보기/확정 시 이보다 많은 선은 성능 보호로 거부(경고).</summary>
    public const int MaxGridLines = 5000;

    /// <summary>점 평행이동.</summary>
    public static Pt2 Translate(Pt2 p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    /// <summary>선 모드 — 시작→끝 단일 선분.</summary>
    public static DrawSeg BuildLine(Pt2 a, Pt2 b) => new(a, b);

    /// <summary>
    /// 표 모드 등간격 격자 — **온전한 칸 단위**로만 생성. 기준점(anchor)과 커서로 만든 사각 영역에서:
    /// 열(칸) 수 = ⌊Xrange ÷ Xpitch⌋, 행(칸) 수 = ⌊Yrange ÷ Ypitch⌋ (내림, 잔여 자투리 버림).
    /// **두 방향 모두 최소 1칸(가로 Xpitch × 세로 Ypitch) 성립할 때만** 격자를 만든다(둘 중 0이면 빈 격자).
    /// 선은 칸 경계 = anchor에서 커서 방향으로 i=0..CellsX(세로선)·j=0..CellsY(가로선), 온전한 칸 영역만 잇는다.
    /// 반환 CellsX/CellsY는 열×행 칸 수(둘 중 &lt;1이면 격자 없음).
    /// </summary>
    public static (int CellsX, int CellsY, IReadOnlyList<DrawSeg> Segs) BuildGrid(
        Pt2 anchor, Pt2 cursor, double pitchX, double pitchY)
    {
        double rangeX = Math.Abs(cursor.X - anchor.X);
        double rangeY = Math.Abs(cursor.Y - anchor.Y);
        int cellsX = pitchX > 0 ? (int)Math.Floor(rangeX / pitchX + 1e-9) : 0;
        int cellsY = pitchY > 0 ? (int)Math.Floor(rangeY / pitchY + 1e-9) : 0;

        // 온전한 칸이 하나도 없으면(어느 한 방향이라도 0) 격자 생성 안 함
        if (cellsX < 1 || cellsY < 1)
            return (cellsX, cellsY, Array.Empty<DrawSeg>());

        double sx = cursor.X >= anchor.X ? 1 : -1;
        double sy = cursor.Y >= anchor.Y ? 1 : -1;
        double xEnd = anchor.X + sx * cellsX * pitchX;   // 온전한 칸 경계(자투리 제외)
        double yEnd = anchor.Y + sy * cellsY * pitchY;

        var segs = new List<DrawSeg>(cellsX + cellsY + 2);
        for (int i = 0; i <= cellsX; i++)   // 세로선(칸 경계)
        {
            double x = anchor.X + sx * i * pitchX;
            segs.Add(new DrawSeg(new Pt2(x, anchor.Y), new Pt2(x, yEnd)));
        }
        for (int j = 0; j <= cellsY; j++)   // 가로선(칸 경계)
        {
            double y = anchor.Y + sy * j * pitchY;
            segs.Add(new DrawSeg(new Pt2(anchor.X, y), new Pt2(xEnd, y)));
        }
        return (cellsX, cellsY, segs);
    }

    /// <summary>커서 이동 단위(Step) 스냅 — 원점(기본 면 좌하단 0,0) 기준 격자에 반올림. step≤0인 축은 그대로.</summary>
    public static Pt2 StepSnap(Pt2 p, double stepX, double stepY, Pt2 origin = default)
    {
        double x = stepX > 0 ? origin.X + Math.Round((p.X - origin.X) / stepX) * stepX : p.X;
        double y = stepY > 0 ? origin.Y + Math.Round((p.Y - origin.Y) / stepY) * stepY : p.Y;
        return new Pt2(x, y);
    }

    /// <summary>점 집합에서 tol(mm) 이내 최근접 점(끝점/꼭짓점 스냅). 없으면 null.</summary>
    public static Pt2? NearestWithin(Pt2 p, IReadOnlyList<Pt2> pts, double tol)
    {
        Pt2? best = null; double bestD = tol * tol;
        foreach (var c in pts)
        {
            double d = Dist2(p, c);
            if (d <= bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>점 집합에서 거리 제한 없이 최근접 점 + 거리(mm). 비어 있으면 null. (확정 선 끝점→커서 거리 표시용)</summary>
    public static (Pt2 Point, double Distance)? Nearest(Pt2 p, IReadOnlyList<Pt2> pts)
    {
        Pt2? best = null; double bestD = double.MaxValue;
        foreach (var c in pts)
        {
            double d = Dist2(p, c);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best is { } b ? (b, Math.Sqrt(bestD)) : null;
    }

    /// <summary>ALT 엣지 스냅 — 점을 가장 가까운 면 모서리(좌/우/하/상) 위로 투영(모서리 방향 클램프 포함).</summary>
    public static Pt2 SnapToNearestEdge(Pt2 p, FaceRect r)
    {
        // 각 모서리에 투영한 후보 + 거리
        var left = new Pt2(0, Math.Clamp(p.Y, 0, r.H));
        var right = new Pt2(r.W, Math.Clamp(p.Y, 0, r.H));
        var bottom = new Pt2(Math.Clamp(p.X, 0, r.W), 0);
        var top = new Pt2(Math.Clamp(p.X, 0, r.W), r.H);

        Pt2 best = left; double bestD = Dist2(p, left);
        foreach (var c in new[] { right, bottom, top })
        {
            double d = Dist2(p, c);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>SHIFT 직교 구속 — 직전 확정점(from) 기준 수평/수직 중 델타가 큰 축을 유지.</summary>
    public static Pt2 OrthoConstrain(Pt2 from, Pt2 p)
    {
        double dx = Math.Abs(p.X - from.X), dy = Math.Abs(p.Y - from.Y);
        return dx >= dy ? new Pt2(p.X, from.Y) : new Pt2(from.X, p.Y);
    }

    /// <summary>ALT·SHIFT 동시 — SHIFT(직교) 적용 후, 그 축 위에서 ALT(엣지 스냅).</summary>
    public static Pt2 OrthoThenSnap(Pt2 from, Pt2 p, FaceRect r)
    {
        var o = OrthoConstrain(from, p);
        bool horizontal = Math.Abs(o.Y - from.Y) < 1e-9;   // y 고정 → 수평(자유축 X)
        return horizontal
            ? new Pt2(o.X <= r.W - o.X ? 0 : r.W, o.Y)      // 자유축 X를 좌/우 모서리로 스냅
            : new Pt2(o.X, o.Y <= r.H - o.Y ? 0 : r.H);     // 자유축 Y를 하/상 모서리로 스냅
    }

    /// <summary>커서에서 가장 가까운 면 꼭짓점 + ΔX·ΔY·직선거리(HUD).</summary>
    public static NearestCorner FindNearestCorner(Pt2 p, FaceRect r)
    {
        var corners = new (Corner c, Pt2 pt)[]
        {
            (Corner.BottomLeft, r.BottomLeft),
            (Corner.BottomRight, r.BottomRight),
            (Corner.TopLeft, r.TopLeft),
            (Corner.TopRight, r.TopRight),
        };
        var best = corners[0];
        double bestD = Dist2(p, best.pt);
        foreach (var c in corners.Skip(1))
        {
            double d = Dist2(p, c.pt);
            if (d < bestD) { bestD = d; best = c; }
        }
        double dx = p.X - best.pt.X, dy = p.Y - best.pt.Y;
        return new NearestCorner(best.c, best.pt, dx, dy, Math.Sqrt(bestD));
    }

    /// <summary>
    /// 임의 정점 집합(면 실제 윤곽 — 팔각 8정점 등)에서 최근접 꼭짓점 + ΔX·ΔY·직선거리(HUD·스냅).
    /// 사각형 4꼭짓점만 아는 <see cref="FindNearestCorner"/>가 팔각 모서리를 인식하지 못하는 문제 해소용. 비면 null.
    /// </summary>
    public static (Pt2 Point, double Dx, double Dy, double Distance)? NearestVertex(Pt2 p, IReadOnlyList<Pt2> verts)
    {
        Pt2? best = null; double bestD = double.MaxValue;
        foreach (var v in verts)
        {
            double d = Dist2(p, v);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best is { } b ? (b, p.X - b.X, p.Y - b.Y, Math.Sqrt(bestD)) : null;
    }

    /// <summary>
    /// 두 선분의 교차점(교착점) — 내부·끝점 포함. 평행/공선/미교차면 null(공선 겹침도 대표점 없이 null).
    /// 용접선×용접선·용접선×Corrugation 교착점 계산에 사용.
    /// </summary>
    public static Pt2? SegmentIntersection(DrawSeg s1, DrawSeg s2)
    {
        double x1 = s1.A.X, y1 = s1.A.Y, x2 = s1.B.X, y2 = s1.B.Y;
        double x3 = s2.A.X, y3 = s2.A.Y, x4 = s2.B.X, y4 = s2.B.Y;
        double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(den) < 1e-9) return null;   // 평행 또는 공선
        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den;
        double u = ((x1 - x3) * (y1 - y2) - (y1 - y3) * (x1 - x2)) / den;
        const double e = 1e-9;
        if (t < -e || t > 1 + e || u < -e || u > 1 + e) return null;   // 선분 밖
        return new Pt2(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
    }

    /// <summary>선분이 가로(|Δx|≥|Δy|)인지 — Corrugation 가로/세로 구분용.</summary>
    public static bool IsHorizontal(DrawSeg s) => Math.Abs(s.B.X - s.A.X) >= Math.Abs(s.B.Y - s.A.Y);

    /// <summary>점→선분 최단거리(편집 모드 히트 테스트).</summary>
    public static double DistanceToSegment(Pt2 p, DrawSeg s)
    {
        double vx = s.B.X - s.A.X, vy = s.B.Y - s.A.Y;
        double wx = p.X - s.A.X, wy = p.Y - s.A.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 < 1e-12 ? 0 : Math.Clamp((wx * vx + wy * vy) / len2, 0, 1);
        double cx = s.A.X + t * vx, cy = s.A.Y + t * vy;
        double dx = p.X - cx, dy = p.Y - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Dist2(Pt2 a, Pt2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
