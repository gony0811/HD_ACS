using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

/// <summary>
/// 검사 가능 지도 3D 씬 — 선창 윤곽(선) 위에 영역은 옅은 음영으로, 용접선은 검사 가능 = 초록 · 불가 = 빨강으로 그린다.
/// 임시 제외한 용접선은 회색(판정하지 않음).
/// </summary>
public static class ReachAreaMapSceneBuilder
{
    public static readonly Rgba Red = Rgba.FromRgb(0xE7, 0x4C, 0x3C);
    public static readonly Rgba Green = Rgba.FromRgb(0x2E, 0xCC, 0x71);
    public static readonly Rgba NoTarget = Rgba.FromRgb(0x7F, 0x8C, 0x8D);
    /// <summary>영역 음영(가능·불가와 무관한 중립색).</summary>
    public static readonly Rgba AreaShade = Rgba.FromArgb(0x38, 0x9F, 0xB4, 0xC8);
    private static readonly Rgba AreaEdge = Rgba.FromArgb(0xFF, 0x6C, 0x7F, 0x90);
    private static readonly Rgba Shell = Rgba.FromArgb(0xFF, 0x5D, 0x6D, 0x7E);
    private static readonly Rgba Deck = Rgba.FromArgb(0xFF, 0x34, 0x49, 0x5E);
    private static readonly Rgba LabelColor = Rgba.FromRgb(0xEC, 0xF0, 0xF1);
    private const double AreaInsetM = 0.02;   // 면 안쪽으로 살짝 띄워 윤곽선과 겹치지 않게
    private const double SeamInsetM = 0.04;   // 용접선은 영역 음영보다 앞에
    public const double SeamThickness = 2.5;

    /// <summary>용접선 색 — 가능 초록 / 불가 빨강 / 임시 제외 회색.</summary>
    public static Rgba SeamColor(bool? ok) => ok switch { true => Green, false => Red, _ => NoTarget };

    public static Scene3 Build(IReadOnlyList<WallDto> walls, TankGeometryDto? geometry, IReadOnlyList<ReachAreaVerdictDto> areas,
        IReadOnlyList<double>? deckZs = null)
    {
        var scene = new Scene3();
        var byCode = walls.GroupBy(w => w.WallCode).ToDictionary(g => g.Key, g => g.First());

        // 선창 윤곽 — 격벽은 팔각, 나머지는 사각형
        foreach (var w in walls)
        {
            IReadOnlyList<Pt3>? outline = TankShape.BulkheadPolygon(w, geometry, 0, geometry?.Derived.H ?? 0);
            outline ??= TankShape.Corners(w, 0, 0, w.ULen, w.VLen);
            if (outline is null) continue;
            AddLoop(scene, outline, Shell, 1.0);
            scene.ExtentPoints.AddRange(outline);
        }

        // 층 발판 윤곽(그 높이의 선창 단면) — 지도에 실린 층마다
        if (deckZs is { Count: > 0 } && geometry is not null
            && byCode.TryGetValue("F", out var f) && byCode.TryGetValue("A", out var a)
            && f.Origin is { Length: 3 } fo && a.Origin is { Length: 3 } ao)
        {
            var oy = geometry.OriginOy;
            foreach (var z in deckZs)
            {
                var hw = TankShape.HalfWidth(geometry, z);
                AddLoop(scene, new[]
                {
                    new Pt3(ao[0], oy - hw, z), new Pt3(fo[0], oy - hw, z), new Pt3(fo[0], oy + hw, z), new Pt3(ao[0], oy + hw, z),
                }, Deck, 1.0);
            }
        }

        foreach (var area in areas)
        {
            if (!byCode.TryGetValue(area.WallCode, out var w)) continue;

            // 영역 — 옅은 음영 + 가는 테두리(색으로 판정을 나타내지 않는다)
            if (area.Corners is { Length: >= 3 } corners)
            {
                var inset = TankShape.NormalOffset(w, AreaInsetM);
                var pts = new List<Pt3>(corners.Length);
                foreach (var c in corners)
                    if (c is { Length: >= 2 } && TankShape.TryPoint(w, c[0], c[1], out var p)) pts.Add(p + inset);
                if (pts.Count >= 3) scene.Faces.Add(new Face3(pts, AreaShade, AreaEdge, 0.8, Shade: false));
            }

            // 용접선 — 가능 초록 / 불가 빨강 / 임시 제외 회색
            var seamInset = TankShape.NormalOffset(w, SeamInsetM);
            foreach (var s in area.Seams ?? new List<ReachWeldVerdictDto>())
            {
                if (!TankShape.TryPoint(w, s.StartU, s.StartV, out var p0) || !TankShape.TryPoint(w, s.EndU, s.EndV, out var p1)) continue;
                var color = SeamColor(s.Ok);
                if ((p1 - p0) is var d && d.X * d.X + d.Y * d.Y + d.Z * d.Z < 1e-8)
                    scene.Markers.Add(new Marker3(p0 + seamInset, color, 3.5));   // 시작 = 끝(점으로 등록된 교차)
                else
                    scene.Segments.Add(new Segment3(p0 + seamInset, p1 + seamInset, color, SeamThickness));
            }
        }

        // 면 이름 + 가능 용접선 수 / 검사 대상
        foreach (var g in areas.GroupBy(x => x.WallCode))
        {
            if (!byCode.TryGetValue(g.Key, out var w) || !TankShape.TryPoint(w, w.ULen / 2, w.VLen / 2, out var c)) continue;
            scene.Labels.Add(new Label3(c + TankShape.NormalOffset(w, 0.3),
                $"{g.Key}  가능 {g.Sum(x => x.Ok)} / {g.Sum(x => x.Welds)}", LabelColor, 13));
        }
        return scene;
    }

    private static void AddLoop(Scene3 scene, IReadOnlyList<Pt3> pts, Rgba color, double thickness)
    {
        for (var i = 0; i < pts.Count; i++)
            scene.Segments.Add(new Segment3(pts[i], pts[(i + 1) % pts.Count], color, thickness));
    }
}
