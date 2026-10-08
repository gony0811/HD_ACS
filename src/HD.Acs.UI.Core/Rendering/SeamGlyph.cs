using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Rendering;

/// <summary>용접선 표시 형태 — seamType에서 판정(CORNER*는 직선으로 그린다).</summary>
public enum SeamKind { Line, Cross3, Cross4 }

/// <summary>배지 문구 형식 — 전개도(화면 방향 포함, 넓음/좁음) · 3D(시점이 돌아가므로 화면 방향 없음).</summary>
public enum BadgeStyle { Full, Compact, NoScreen }

/// <summary>교차 도형의 선분 1개(캔버스 px). IsStem=CROSS3 줄기(굵게+화살촉).</summary>
public sealed record GlyphLine(Pt2 A, Pt2 B, bool IsStem);

/// <summary>
/// 작업 1개를 그릴 도형(캔버스 px) — 선분들 + 줄기 화살촉 + 교차점 마커 + 배지 앵커.
/// Dashed=교차 유형인데 가지(points)가 없어 실제 모양을 모르는 작업(시작–끝 직선으로 대신 그림).
/// </summary>
public sealed record SeamGlyphShape(SeamKind Kind, bool HasArms, bool Dashed,
    IReadOnlyList<GlyphLine> Lines, IReadOnlyList<Pt2>? Arrow, IReadOnlyList<Pt2>? Marker, Pt2 LabelAnchor);

/// <summary>
/// 용접선 유형 구분 표시 규칙 [VDA §8.5.1] — 계획 전개도·운영 전개도·3D·작업 목록이 공유하는 단일 정본.
///
/// - LINE: 시작–끝 직선(기존과 동일).
/// - CROSS3: 저장된 가지 좌표 그대로 T자. 줄기(points[0])는 굵게+끝 화살촉, 교차점 ▲(꼭짓점이 줄기 쪽).
/// - CROSS4: 가지 4개 같은 굵기 十자, 교차점 ■.
/// - 교차 유형인데 가지가 없으면: 시작–끝 점선 + 마커 + "가지 미지정".
///
/// ⚠️ 그림은 반드시 **저장된 가지 좌표**로 그린다 — 회전값(CROSS3_R*)으로 T자 템플릿을 돌려 그리면 안 된다.
/// R0~R270은 AMR 카메라 시점 프레임 기준이라 전개도(ACS 면 축)와 면마다 축이 뒤집혀 있다
/// (좌현·격벽은 u·v 모두, 우현은 v만 — Core CrossGeometry). 회전값은 라벨로만 보여 주고,
/// 화면 방향(←↑→↓)은 줄기 좌표에서 따로 계산해 둘을 운영자가 대조할 수 있게 한다.
/// 선 색은 호출 측이 정한다(운영 화면은 진행 상태색 — 유형 구분에 색을 쓰지 않는다).
/// </summary>
public static class SeamGlyph
{
    public static SeamKind KindOf(string? seamType)
    {
        var st = (seamType ?? "").Trim().ToUpperInvariant();
        if (st.StartsWith("CROSS3", StringComparison.Ordinal)) return SeamKind.Cross3;
        if (st is "CROSS4" or "CROSS") return SeamKind.Cross4;   // CROSS = legacy 별칭(서버가 CROSS4로 정규화)
        return SeamKind.Line;
    }

    /// <summary>가지 개수가 유형과 맞는가(CROSS3=3·CROSS4=4). LINE은 항상 false.</summary>
    public static bool HasArms(SeamKind kind, double[][]? points) => kind switch
    {
        SeamKind.Cross3 => points is { Length: 3 } && points.All(p => p is { Length: >= 2 }),
        SeamKind.Cross4 => points is { Length: 4 } && points.All(p => p is { Length: >= 2 }),
        _ => false,
    };

    /// <summary>CROSS3 회전 라벨("R0"~"R270"). 접미 없는 CROSS3는 서버 정규화와 같이 R0. 그 외 null.</summary>
    public static string? RotationLabel(string? seamType)
    {
        if (KindOf(seamType) != SeamKind.Cross3) return null;
        var st = seamType!.Trim().ToUpperInvariant();
        int i = st.IndexOf('_');
        return i >= 0 && i + 1 < st.Length ? st[(i + 1)..] : "R0";
    }

    /// <summary>
    /// 전개도 화면에서의 방향 화살표 — 면-로컬 (du,dv) 기준(전개도는 u=오른쪽·v=위쪽).
    /// 가장 가까운 상하좌우로 스냅, 길이 0이면 "".
    /// </summary>
    public static string ScreenArrow(double du, double dv)
    {
        if (Math.Abs(du) < 1e-12 && Math.Abs(dv) < 1e-12) return "";
        return Math.Abs(du) >= Math.Abs(dv) ? (du >= 0 ? "→" : "←") : (dv >= 0 ? "↑" : "↓");
    }

    /// <summary>
    /// 배지 문구. LINE="3" / CROSS3="3T R0 (화면 ←)"(Full)·"3T R0←"(Compact)·"3T R0"(NoScreen) /
    /// CROSS4="5+" / 가지 없음="3T 가지 미지정"(Full)·"3T ?"(그 외).
    /// </summary>
    public static string Badge(AreaTaskDto t, BadgeStyle style)
    {
        var kind = KindOf(t.SeamType);
        string seq = t.Seq.ToString();
        if (kind == SeamKind.Line) return seq;
        string head = seq + (kind == SeamKind.Cross3 ? "T" : "+");
        if (!HasArms(kind, t.Points)) return head + (style == BadgeStyle.Full ? " 가지 미지정" : " ?");
        if (kind == SeamKind.Cross4) return head;

        string rot = RotationLabel(t.SeamType)!;
        var stem = t.Points![0];
        string arrow = ScreenArrow(stem[0] - t.StartU, stem[1] - t.StartV);
        return style switch
        {
            BadgeStyle.Full => arrow.Length > 0 ? $"{head} {rot} (화면 {arrow})" : $"{head} {rot}",
            BadgeStyle.Compact => $"{head} {rot}{arrow}",
            _ => $"{head} {rot}",
        };
    }

    /// <summary>작업 목록 "유형" 열 문구 — "▲ CROSS3_R90" / "■ CROSS4" / 가지 없음 표시 / LINE 등은 그대로.</summary>
    public static string TypeLabel(string? seamType, double[][]? points)
    {
        var kind = KindOf(seamType);
        if (kind == SeamKind.Line) return seamType ?? "";
        string mark = kind == SeamKind.Cross3 ? "▲" : "■";
        return $"{mark} {seamType}" + (HasArms(kind, points) ? "" : " · 가지 없음");
    }

    /// <summary>
    /// 작업 DTO → 캔버스 선분 레코드. <paramref name="proj"/>=면-로컬 (u,v)→캔버스 px,
    /// <paramref name="vOffset"/>=DTO v에 더할 값(계획 화면의 층-로컬→면-전체 변환, 운영은 0).
    /// 교차는 중심=Start, 가지=Points(투영), 배지 앵커=중심.
    /// </summary>
    public static TaskSeg ToSeg(AreaTaskDto t, Func<double, double, (double X, double Y)> proj, double vOffset,
        string? status, BadgeStyle style)
    {
        var (x1, y1) = proj(t.StartU, t.StartV + vOffset);
        var (x2, y2) = proj(t.EndU, t.EndV + vOffset);
        var kind = KindOf(t.SeamType);
        IReadOnlyList<Pt2>? arms = null;
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        if (HasArms(kind, t.Points))
        {
            arms = t.Points!.Select(p => { var (ax, ay) = proj(p[0], p[1] + vOffset); return new Pt2(ax, ay); }).ToArray();
            mx = x1; my = y1;   // 교차는 중심 기준
        }
        return new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, mx, my, Badge(t, style), status, kind, arms);
    }

    /// <summary>
    /// 캔버스 도형 산출 — <paramref name="markerR"/>=교차점 마커 반경(px), <paramref name="arrowLen"/>=화살촉 길이(px).
    /// 화면 크기(줌)에 무관하게 같은 px 크기로 그리려면 호출 측이 값을 넘긴다.
    /// </summary>
    public static SeamGlyphShape Build(TaskSeg t, double markerR = 6, double arrowLen = 9)
    {
        var start = new Pt2(t.X1, t.Y1);
        var end = new Pt2(t.X2, t.Y2);
        if (t.Kind == SeamKind.Line)
            return new SeamGlyphShape(SeamKind.Line, false, false, new[] { new GlyphLine(start, end, false) }, null, null, t.Mid);

        if (t.Arms is not { Count: > 0 } arms)
        {
            // 가지 없음 — 시작–끝 점선 + 중점 마커(▲는 위를 가리킴)
            var mid = new Pt2((t.X1 + t.X2) / 2, (t.Y1 + t.Y2) / 2);
            var marker = t.Kind == SeamKind.Cross3 ? Triangle(mid, 0, -1, markerR) : Square(mid, markerR);
            return new SeamGlyphShape(t.Kind, false, true, new[] { new GlyphLine(start, end, false) }, null, marker,
                new Pt2(mid.X + markerR + 3, mid.Y + markerR + 1));
        }

        var c = start;   // 교차 중심
        var lines = arms.Select((a, i) => new GlyphLine(c, a, t.Kind == SeamKind.Cross3 && i == 0)).ToArray();

        IReadOnlyList<Pt2>? arrow = null;
        double dx = 0, dy = -1;   // 줄기 방향 단위벡터(px). 기본=위
        if (t.Kind == SeamKind.Cross3)
        {
            var tip = arms[0];
            double len = Math.Sqrt((tip.X - c.X) * (tip.X - c.X) + (tip.Y - c.Y) * (tip.Y - c.Y));
            if (len > 1e-9)
            {
                dx = (tip.X - c.X) / len; dy = (tip.Y - c.Y) / len;
                double al = Math.Min(arrowLen, len * 0.6), w = al * 0.55;
                var b = new Pt2(tip.X - dx * al, tip.Y - dy * al);
                arrow = new[] { tip, new Pt2(b.X - dy * w, b.Y + dx * w), new Pt2(b.X + dy * w, b.Y - dx * w) };
            }
        }
        var mk = t.Kind == SeamKind.Cross3 ? Triangle(c, dx, dy, markerR) : Square(c, markerR);
        return new SeamGlyphShape(t.Kind, true, false, lines, arrow, mk, new Pt2(c.X + markerR + 3, c.Y + markerR + 1));
    }

    /// <summary>정삼각형 — 꼭짓점 하나가 (dx,dy) 방향(px)을 가리킨다.</summary>
    private static Pt2[] Triangle(Pt2 c, double dx, double dy, double r)
    {
        var pts = new Pt2[3];
        double a0 = Math.Atan2(dy, dx);
        for (int i = 0; i < 3; i++)
        {
            double a = a0 + i * 2 * Math.PI / 3;
            pts[i] = new Pt2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        return pts;
    }

    private static Pt2[] Square(Pt2 c, double r)
    {
        double h = r * 0.8;
        return new[] { new Pt2(c.X - h, c.Y - h), new Pt2(c.X + h, c.Y - h), new Pt2(c.X + h, c.Y + h), new Pt2(c.X - h, c.Y + h) };
    }
}
