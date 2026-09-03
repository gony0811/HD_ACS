using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

// ── 3D 씬 프리미티브(도면 프레임) ─────────────────────────────────────────────
/// <summary>볼록 다각형 면. Fill=null이면 외곽선만. Shade=true면 렌더 시 법선·광원 내적으로 플랫 셰이딩.</summary>
public sealed record Face3(IReadOnlyList<Pt3> Points, Rgba? Fill, Rgba? Stroke, double StrokeThickness = 1.0, bool Shade = true);
/// <summary>선분.</summary>
public sealed record Segment3(Pt3 A, Pt3 B, Rgba Color, double Thickness = 1.0);
/// <summary>원형 마커 — RadiusWorld>0이면 도면 m 반지름(원근 적용), 아니면 RadiusPx 고정 화면 반지름.</summary>
public sealed record Marker3(Pt3 Center, Rgba Fill, double RadiusPx = 5, double RadiusWorld = 0, Rgba? Stroke = null);
/// <summary>빌보드 텍스트(항상 화면을 향함).</summary>
public sealed record Label3(Pt3 Position, string Text, Rgba Color, double FontSize = 12);

/// <summary>3D 씬 — 프리미티브 목록 + 경계(ZoomExtents용).</summary>
public sealed class Scene3
{
    public List<Face3> Faces { get; } = new();
    public List<Segment3> Segments { get; } = new();
    public List<Marker3> Markers { get; } = new();
    public List<Label3> Labels { get; } = new();

    /// <summary>ZoomExtents 대상 점(셸·강조 등 "형상"만 — 격자·라벨은 제외해 프레이밍이 흔들리지 않게).</summary>
    public List<Pt3> ExtentPoints { get; } = new();

    public bool IsEmpty => Faces.Count == 0 && Segments.Count == 0 && Markers.Count == 0 && Labels.Count == 0;
}

// ── 2D 그리기 목록(화면 px) — 헤드가 DrawingContext 등으로 그린다 ────────────────
public abstract record Draw2(double Depth);
public sealed record Face2(Pt2[] Points, Rgba? Fill, Rgba? Stroke, double StrokeThickness, double Depth) : Draw2(Depth);
public sealed record Segment2(Pt2 A, Pt2 B, Rgba Color, double Thickness, double Depth) : Draw2(Depth);
public sealed record Marker2(Pt2 Center, double Radius, Rgba Fill, Rgba? Stroke, double Depth) : Draw2(Depth);
public sealed record Label2(Pt2 Position, string Text, Rgba Color, double FontSize, double Depth) : Draw2(Depth);

/// <summary>
/// 소프트웨어 투영 렌더러 — 씬을 카메라로 투영해 화면 px 그리기 목록을 만든다.
/// 깊이 정렬은 페인터 알고리즘(면·선은 시선 깊이 내림차순 = 먼 것 먼저), 마커·라벨은 그 위에 오버레이.
/// 반투명 면은 2D 알파 블렌딩으로 자연히 겹쳐 보인다(WPF 3D의 반투명 깊이 컬링 문제 없음).
/// </summary>
public static class SceneRenderer
{
    private static readonly Pt3 LightDir = new Pt3(-0.4, -0.3, 1.0).Normalized();

    public static List<Draw2> Render(Scene3 scene, Camera3 camera, double viewWidth, double viewHeight)
    {
        var body = new List<Draw2>(scene.Faces.Count + scene.Segments.Count);
        var overlay = new List<Draw2>(scene.Markers.Count + scene.Labels.Count);

        foreach (var f in scene.Faces)
        {
            if (f.Points.Count < 3) continue;
            var pts = new Pt2[f.Points.Count];
            double depth = 0;
            bool ok = true;
            for (int i = 0; i < pts.Length; i++)
            {
                var (s, d, vis) = camera.Project(f.Points[i], viewWidth, viewHeight);
                if (!vis) { ok = false; break; }
                pts[i] = s; depth += d;
            }
            if (!ok) continue;
            var fill = f.Fill;
            if (fill is { } c && f.Shade) fill = Shade(c, FaceNormal(f.Points));
            body.Add(new Face2(pts, fill, f.Stroke, f.StrokeThickness, depth / pts.Length));
        }

        foreach (var s in scene.Segments)
        {
            var (a, da, va) = camera.Project(s.A, viewWidth, viewHeight);
            var (b, db, vb) = camera.Project(s.B, viewWidth, viewHeight);
            if (!va || !vb) continue;
            body.Add(new Segment2(a, b, s.Color, s.Thickness, (da + db) / 2));
        }

        double focal = camera.FocalPx(viewHeight);
        foreach (var m in scene.Markers)
        {
            var (c, d, vis) = camera.Project(m.Center, viewWidth, viewHeight);
            if (!vis) continue;
            double r = m.RadiusWorld > 0 ? Math.Max(1.5, m.RadiusWorld * focal / d) : m.RadiusPx;
            overlay.Add(new Marker2(c, r, m.Fill, m.Stroke, d));
        }

        foreach (var l in scene.Labels)
        {
            var (p, d, vis) = camera.Project(l.Position, viewWidth, viewHeight);
            if (!vis) continue;
            overlay.Add(new Label2(p, l.Text, l.Color, l.FontSize, d));
        }

        // 먼 것 먼저(깊이 내림차순). 안정 정렬로 같은 깊이는 삽입 순서 유지(셸 → 강조 → 오버레이 순서 보존).
        var ordered = body.OrderByDescending(x => x.Depth).ToList();
        ordered.AddRange(overlay.OrderByDescending(x => x.Depth));
        return ordered;
    }

    /// <summary>다각형 법선(처음 세 점, 정규화). 퇴화면 z-up.</summary>
    public static Pt3 FaceNormal(IReadOnlyList<Pt3> pts)
    {
        var n = (pts[1] - pts[0]).Cross(pts[2] - pts[0]);
        return n.LengthSquared > 1e-18 ? n.Normalized() : Pt3.UnitZ;
    }

    /// <summary>플랫 셰이딩 — 0.65~1.0 밝기(양면 동일하도록 |n·L|). 알파는 유지.</summary>
    public static Rgba Shade(Rgba c, Pt3 normal)
    {
        double k = 0.65 + 0.35 * Math.Abs(normal.Dot(LightDir));
        return new Rgba(c.A, (byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));
    }
}
