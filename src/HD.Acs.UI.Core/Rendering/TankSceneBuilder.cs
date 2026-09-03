using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Rendering;

/// <summary>씬 빌드 입력 — TankViewModel 상태의 스냅샷(VM 의존 없이 테스트 가능).</summary>
public sealed record TankSceneInput(
    IReadOnlyList<WallDto> ShellWalls,
    IReadOnlyList<WallDto> LevelWalls,
    TankGeometryDto? Geometry,
    IReadOnlyList<TankViewModel.AreaOverlay> Overlays,
    bool ShowOverlays,
    int? SelectedLevel,
    Func<Guid, string?> WorkItemStatusOf,
    Func<Guid, string?> TaskStatusOf,
    bool HasRobotPosition,
    Pt3 RobotPosition,
    Pt3? MoveMarker = null,
    double? RobotHeading = null)   // 도면 프레임 heading(rad). null=화살표 생략
{
    public bool IsolateLevel => SelectedLevel is not null;
}

/// <summary>
/// 화물창 3D 씬 빌더 — WPF TankView.xaml.cs의 BuildShell/BuildLevelHighlight/BuildOverlays/BuildFloorGrid를 프리미티브 생성으로 옮긴 것.
/// 레이어 오프셋 규칙(TANK_RENDERING.md): 셸 0 / 층 밴드 0.02m 외부향 / 오버레이 0.03m 외부향(법선이 내부향이라 부호 반전).
/// </summary>
public static class TankSceneBuilder
{
    public const double HighlightOffsetM = 0.02;
    /// <summary>로봇 heading 화살표(바닥면) — 마커 원(0.4m) 밖으로 나오는 축 길이·화살촉 치수[m].</summary>
    public const double HeadingShaftM = 0.85;
    public const double HeadingTipM = 1.25;
    public const double HeadingHeadBaseM = 0.8;
    public const double HeadingHeadHalfWidthM = 0.28;
    public const double HeadingLiftM = 0.05;   // 바닥 격자 위로 살짝 띄움(z-fighting 회피)

    private static readonly Rgba EdgeColor = Rgba.FromRgb(0xEA, 0xF2, 0xF8);
    private static readonly Rgba BandFill = Rgba.FromArgb(0xE0, 0xF5, 0xB0, 0x41);
    private static readonly Rgba BandStroke = Rgba.FromRgb(0xF3, 0x9C, 0x12);
    private static readonly Rgba GroundGrid = Rgba.FromArgb(0x70, 0x5D, 0x6D, 0x7E);
    private static readonly Rgba FloorGridLine = Rgba.FromRgb(0x5D, 0x8A, 0xA8);
    private static readonly Rgba FloorHitFill = Rgba.FromArgb(0x16, 0x2E, 0x86, 0xC1);
    private static readonly Rgba RobotColor = Rgba.FromRgb(0xE7, 0x4C, 0x3C);
    private static readonly Rgba MoveMarkerColor = Rgba.FromRgb(0x9B, 0x59, 0xB6);
    private static readonly Rgba WeldStart = Rgba.FromRgb(0x27, 0xAE, 0x60);
    private static readonly Rgba WeldEnd = Rgba.FromRgb(0xC0, 0x39, 0x2B);
    private static readonly Rgba LabelWhite = Rgba.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Rgba LabelWheat = Rgba.FromRgb(0xF5, 0xDE, 0xB3);

    public static Scene3 Build(TankSceneInput input)
    {
        var scene = new Scene3();
        AddGroundGrid(scene);
        AddShell(scene, input);
        AddLevelHighlight(scene, input);
        AddOverlays(scene, input);
        AddFloorGrid(scene, input);
        if (input.HasRobotPosition)
        {
            if (input.RobotHeading is double heading) AddRobotHeading(scene, input.RobotPosition, heading);
            scene.Markers.Add(new Marker3(input.RobotPosition + new Pt3(0, 0, 0.4), RobotColor, RadiusWorld: 0.4, Stroke: Rgba.FromRgb(0xFF, 0xFF, 0xFF)));
        }
        if (input.MoveMarker is { } mm)
            scene.Markers.Add(new Marker3(mm, MoveMarkerColor, RadiusWorld: 0.25, Stroke: Rgba.FromRgb(0xFF, 0xFF, 0xFF)));
        return scene;
    }

    /// <summary>
    /// 로봇 heading 화살표 — 주행 평면(로봇 z) 위에 축(Segment3)+삼각 화살촉(Face3, 셰이딩 없음)을 그린다.
    /// 마커 원은 오버레이(항상 위)라 축이 원 중심에서 나오는 것처럼 보인다. heading은 도면 프레임 rad(x축 기준 CCW).
    /// </summary>
    public static void AddRobotHeading(Scene3 scene, Pt3 robot, double headingRad)
    {
        var dir = new Pt3(Math.Cos(headingRad), Math.Sin(headingRad), 0);
        var perp = new Pt3(-dir.Y, dir.X, 0);
        var o = robot + new Pt3(0, 0, HeadingLiftM);
        scene.Segments.Add(new Segment3(o, o + dir * HeadingShaftM, RobotColor, Thickness: 2.5));
        scene.Faces.Add(new Face3(
            new[] { o + dir * HeadingTipM, o + dir * HeadingHeadBaseM + perp * HeadingHeadHalfWidthM, o + dir * HeadingHeadBaseM - perp * HeadingHeadHalfWidthM },
            RobotColor, LabelWhite, StrokeThickness: 1.0, Shade: false));
    }

    /// <summary>층 격리 모드의 바닥 클릭 평면 — z와 (x0,y0,x1,y1) 사각 범위. 격리 모드가 아니거나 지오메트리 없으면 null.</summary>
    public static (double Z, double X0, double Y0, double X1, double Y1)? FloorPlane(TankSceneInput input)
    {
        if (input.Geometry is not { } g || input.SelectedLevel is not int level) return null;
        double gridZ = g.LevelZ is { } lz && level - 1 < lz.Length ? lz[level - 1] : 0.0;
        double hw = TankShape.HalfWidth(g, gridZ);
        return (gridZ, g.OriginOx - g.LengthL / 2, g.OriginOy - hw, g.OriginOx + g.LengthL / 2, g.OriginOy + hw);
    }

    // ── 지면 참조 격자(원점, 40×40m, 5m 간격) — Helix GridLinesVisual3D 대체(형상 컨텍스트만, 프레이밍 제외) ──
    private static void AddGroundGrid(Scene3 scene)
    {
        const double half = 20, step = 5;
        for (double t = -half; t <= half + 1e-9; t += step)
        {
            scene.Segments.Add(new Segment3(new Pt3(t, -half, 0), new Pt3(t, half, 0), GroundGrid, 0.8));
            scene.Segments.Add(new Segment3(new Pt3(-half, t, 0), new Pt3(half, t, 0), GroundGrid, 0.8));
        }
    }

    // ── 3D 셸 (반투명 면 + 팔각 모서리 와이어) ──
    private static void AddShell(Scene3 scene, TankSceneInput input)
    {
        if (input.ShellWalls.Count == 0) return;
        bool fill = !input.IsolateLevel;   // 전체 모드만 반투명 채움(격리 모드는 가림 방지 위해 와이어만)

        foreach (var w in input.ShellWalls)
        {
            var h = input.Geometry?.Derived.H ?? 0;
            IReadOnlyList<Pt3>? poly = (IReadOnlyList<Pt3>?)TankShape.BulkheadPolygon(w, input.Geometry, 0, h)
                                       ?? TankShape.Corners(w, 0, 0, w.ULen, w.VLen);
            if (poly is null || poly.Count < 3) continue;

            scene.Faces.Add(new Face3(poly, fill ? TankShape.FaceColor(w.WallCode) : null, null));
            AddClosedOutline(scene, poly, EdgeColor, 1.1);
            scene.ExtentPoints.AddRange(poly);
        }
    }

    // ── 선택 층 도달 z-밴드 강조 (reachableVBand 서브사각형) ──
    private static void AddLevelHighlight(Scene3 scene, TankSceneInput input)
    {
        foreach (var w in input.LevelWalls)
        {
            if (w.ReachableVBand is not { Length: 2 } band) continue;
            double vLo = band[0], vHi = band[1];
            if (vHi <= vLo) continue;
            var off = -TankShape.NormalOffset(w, HighlightOffsetM);   // 외부 방향으로 소량 띄워 와이어와의 겹침 방지

            IReadOnlyList<Pt3>? poly = (IReadOnlyList<Pt3>?)TankShape.BulkheadPolygon(w, input.Geometry, vLo, vHi)
                                       ?? TankShape.Corners(w, 0, vLo, w.ULen, vHi);
            if (poly is null || poly.Count < 3) continue;
            var shifted = poly.Select(p => p + off).ToArray();

            scene.Faces.Add(new Face3(shifted, BandFill, null, Shade: false));
            AddClosedOutline(scene, shifted, BandStroke, 3.0);
        }
    }

    // ── 영역·작업(용접선) 오버레이 ──
    private static void AddOverlays(Scene3 scene, TankSceneInput input)
    {
        if (!input.ShowOverlays || input.Overlays.Count == 0 || input.ShellWalls.Count == 0) return;

        int? lvl = input.SelectedLevel;   // 전체=null → 모든 영역, L{n}=그 층만
        var wallByCode = input.ShellWalls.GroupBy(w => w.WallCode).ToDictionary(g => g.Key, g => g.First());

        foreach (var ov in input.Overlays)
        {
            var a = ov.Area;
            if (lvl is int L && a.Level != L) continue;
            if (!wallByCode.TryGetValue(a.WallCode, out var wall)) continue;

            var off = -TankShape.NormalOffset(wall, HighlightOffsetM * 1.5);   // 외부향으로 셸보다 약간 더 띄움

            var corners = a.Corners ?? new[]
            {
                new[] { a.UMin, a.VMin }, new[] { a.UMax, a.VMin }, new[] { a.UMax, a.VMax }, new[] { a.UMin, a.VMax },
            };
            var pts3d = new List<Pt3>(corners.Length);
            foreach (var p in corners)
                if (p is { Length: >= 2 } && TankShape.TryPoint(wall, p[0], p[1], out var cp)) pts3d.Add(cp + off);
            if (pts3d.Count >= 3)
            {
                var (fillC, lineC) = TankViewModel.StatusColors(input.WorkItemStatusOf(a.AreaId));
                scene.Faces.Add(new Face3(pts3d, fillC, null, Shade: false));
                AddClosedOutline(scene, pts3d, lineC, 2.0);
                var c = Centroid(pts3d);
                scene.Labels.Add(new Label3(c, a.Name, LabelWhite, 12));
            }

            foreach (var t in ov.Tasks)
            {
                if (!TankShape.TryPoint(wall, t.StartU, t.StartV, out var s) || !TankShape.TryPoint(wall, t.EndU, t.EndV, out var e)) continue;
                s += off; e += off;
                var weldC = TankViewModel.WeldLineColor(input.TaskStatusOf(t.TaskId));
                scene.Segments.Add(new Segment3(s, e, weldC, 3.0));
                scene.Markers.Add(new Marker3(s, WeldStart, RadiusPx: 5.5));
                scene.Markers.Add(new Marker3(e, WeldEnd, RadiusPx: 5.5));
                scene.Labels.Add(new Label3((s + e) * 0.5, t.Seq.ToString(), LabelWheat, 11));
            }
        }
    }

    // ── 층 바닥 그리드 (층 격리 모드) — 수동 이동 클릭 대상 평면 + 1m 격자 ──
    private static void AddFloorGrid(Scene3 scene, TankSceneInput input)
    {
        if (FloorPlane(input) is not var (gridZ, x0, y0, x1, y1)) return;
        double z = gridZ + 0.015;   // 셸과의 겹침 회피

        scene.Faces.Add(new Face3(new[] { new Pt3(x0, y0, z), new Pt3(x1, y0, z), new Pt3(x1, y1, z), new Pt3(x0, y1, z) },
            FloorHitFill, null, Shade: false));
        for (double x = Math.Ceiling(x0); x <= x1 + 1e-9; x += 1.0)
            scene.Segments.Add(new Segment3(new Pt3(x, y0, z), new Pt3(x, y1, z), FloorGridLine, 1.0));
        for (double y = Math.Ceiling(y0); y <= y1 + 1e-9; y += 1.0)
            scene.Segments.Add(new Segment3(new Pt3(x0, y, z), new Pt3(x1, y, z), FloorGridLine, 1.0));
    }

    private static void AddClosedOutline(Scene3 scene, IReadOnlyList<Pt3> pts, Rgba color, double thickness)
    {
        for (int i = 0; i < pts.Count; i++)
            scene.Segments.Add(new Segment3(pts[i], pts[(i + 1) % pts.Count], color, thickness));
    }

    private static Pt3 Centroid(IReadOnlyList<Pt3> pts)
    {
        var sum = Pt3.Zero;
        foreach (var p in pts) sum += p;
        return sum * (1.0 / Math.Max(1, pts.Count));
    }
}
