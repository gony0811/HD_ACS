using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HD.Acs.UI.Models;
using HD.Acs.UI.ViewModels;
using HelixToolkit.Wpf;

namespace HD.Acs.UI.Views;

/// <summary>
/// 화물창 좌측 뷰. 3D 셸(팔각 프리즘)은 지오메트리 API의 실제 10면(WallDto)으로 코드비하인드에서
/// 메시를 빌드한다(반투명 면 + 모서리 와이어). 선택 층 도달 z-밴드를 강조 오버레이로 그린다.
/// 로봇 마커 위치도 VM 변화에 반응해 코드비하인드에서 갱신한다.
/// 3D 씬과 전개도는 동일한 벽면 코드/좌표계(도면 프레임, z-up, m)를 공유한다.
/// </summary>
public partial class TankView : UserControl
{
    private TankViewModel? _vm;

    // 면 두께 강조·z-fighting 회피용 법선 방향 오프셋(m)
    private const double HighlightOffsetM = 0.02;

    // 수동 이동: 바닥 그리드 클릭 판정용 히트 평면(층 격리 모드에서만 생성)
    private GeometryModel3D? _gridHitModel;
    private double _gridZ;

    public TankView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Viewport.MouseLeftButtonDown += OnViewportMouseDown;
    }

    /// <summary>수동 이동 모드에서 바닥 그리드 클릭 → 도면 (x,y)로 goto 명령.</summary>
    private void OnViewportMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm is null || !_vm.ManualMoveMode || _gridHitModel is null) return;
        var hits = Viewport.Viewport.FindHits(e.GetPosition(Viewport));
        var hit = hits.FirstOrDefault(h => ReferenceEquals(h.Model, _gridHitModel));
        if (hit is null) return;

        var p = hit.Position;
        PlaceMoveMarker(p);
        _ = _vm.RequestMoveAsync(p.X, p.Y);
    }

    private void PlaceMoveMarker(Point3D p)
    {
        MoveMarker.Children.Clear();
        MoveMarker.Children.Add(new SphereVisual3D
        { Center = new Point3D(p.X, p.Y, _gridZ + 0.25), Radius = 0.25, Fill = new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xB6)) });
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ViewChanged -= OnViewChanged;
        }
        _vm = e.NewValue as TankViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ViewChanged += OnViewChanged;
        }
        UpdateRobotMarker();
        Rebuild();
    }

    private void OnViewChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild() { BuildShell(); BuildLevelHighlight(); BuildOverlays(); BuildFloorGrid(); }

    // ── 층 바닥 그리드 (층 격리 모드) — 수동 이동 클릭 대상 평면 + 1m 격자 ─────────
    private void BuildFloorGrid()
    {
        FloorGrid.Children.Clear();
        MoveMarker.Children.Clear();
        _gridHitModel = null;
        if (_vm?.Geometry is not { } g || _vm.SelectedLevel is not int level) return;

        // 층 주행 평면 z = level_z[level-1] (미정의 시 0)
        _gridZ = g.LevelZ is { } lz && level - 1 < lz.Length ? lz[level - 1] : 0.0;
        double hw = HalfWidth(g, _gridZ);
        double x0 = g.OriginOx - g.LengthL / 2, x1 = g.OriginOx + g.LengthL / 2;
        double y0 = g.OriginOy - hw, y1 = g.OriginOy + hw;
        double z = _gridZ + 0.015;   // 셸과의 z-fighting 회피

        // 클릭 히트 평면(옅은 파랑 반투명) — 재질이 투명해도 히트 테스트는 지오메트리 기준
        var mesh = PolygonMesh(new List<Point3D>
        {
            new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z),
        });
        var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x16, 0x2E, 0x86, 0xC1)));
        _gridHitModel = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
        FloorGrid.Children.Add(new ModelVisual3D { Content = _gridHitModel });

        // 1m 격자선
        var lines = new LinesVisual3D { Color = Color.FromRgb(0x5D, 0x8A, 0xA8), Thickness = 1.0 };
        for (double x = Math.Ceiling(x0); x <= x1 + 1e-9; x += 1.0)
        { lines.Points.Add(new Point3D(x, y0, z)); lines.Points.Add(new Point3D(x, y1, z)); }
        for (double y = Math.Ceiling(y0); y <= y1 + 1e-9; y += 1.0)
        { lines.Points.Add(new Point3D(x0, y, z)); lines.Points.Add(new Point3D(x1, y, z)); }
        FloorGrid.Children.Add(lines);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TankViewModel.RobotDrawingX)
            or nameof(TankViewModel.RobotDrawingY)
            or nameof(TankViewModel.HasRobotPosition))
            UpdateRobotMarker();
    }

    private void UpdateRobotMarker()
    {
        if (_vm is null) return;
        // 맵 좌표 → 도면 좌표(T_W_D 역변환, VM에서 산출) + 로봇 층 주행 평면 z에 표시.
        RobotMarker.Transform = new TranslateTransform3D(_vm.RobotDrawingX, _vm.RobotDrawingY, _vm.RobotDrawingZ);
    }

    // ── 3D 셸 (반투명 면 + 팔각 모서리 와이어) ──────────────────────────────
    private void BuildShell()
    {
        ShellModel.Children.Clear();
        if (_vm is null || _vm.ShellWalls.Count == 0) return;

        bool fill = !_vm.IsolateLevel;   // 전체 모드만 반투명 채움(격리 모드는 가림 방지 위해 와이어만)
        var group = new Model3DGroup();
        var edges = new LinesVisual3D { Color = Color.FromRgb(0xEA, 0xF2, 0xF8), Thickness = 1.1 };

        foreach (var w in _vm.ShellWalls)
        {
            // 마구리(F/A)는 팔각 단면 윤곽(모따기)으로 그린다. 그 외 면은 직사각형.
            if (BulkheadPolygon(w, 0, _vm.Geometry?.Derived.H ?? 0) is { Count: >= 3 } poly)
            {
                if (fill)
                {
                    var mat = new DiffuseMaterial(FaceBrush(w.WallCode));
                    group.Children.Add(new GeometryModel3D(PolygonMesh(poly), mat) { BackMaterial = mat });
                }
                AddClosedOutline(edges, poly);
                continue;
            }

            if (!TryCorners(w, 0, 0, w.ULen, w.VLen, out var c0, out var c1, out var c2, out var c3)) continue;

            if (fill)
            {
                var mat = new DiffuseMaterial(FaceBrush(w.WallCode));
                group.Children.Add(new GeometryModel3D(Quad(c0, c1, c2, c3), mat) { BackMaterial = mat });
            }

            // 4변 모서리(닫힌 사각형) — 모든 모드에서 형상 컨텍스트 제공
            edges.Points.Add(c0); edges.Points.Add(c1);
            edges.Points.Add(c1); edges.Points.Add(c2);
            edges.Points.Add(c2); edges.Points.Add(c3);
            edges.Points.Add(c3); edges.Points.Add(c0);
        }

        if (group.Children.Count > 0) ShellModel.Children.Add(new ModelVisual3D { Content = group });
        ShellModel.Children.Add(edges);
        Viewport.ZoomExtents();
    }

    // ── 선택 층 도달 z-밴드 강조 (reachableVBand 서브사각형) ─────────────────
    private void BuildLevelHighlight()
    {
        LevelHighlight.Children.Clear();
        if (_vm is null || _vm.LevelWalls.Count == 0) return;

        // 밝은 골드: 불투명 근접 확산 + 발광(EmissiveMaterial)으로 조명·블렌드 순서와 무관하게 또렷.
        var fill = new MaterialGroup();
        fill.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0xE0, 0xF5, 0xB0, 0x41))));
        fill.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x66, 0xF1, 0xC4, 0x0F))));

        var group = new Model3DGroup();
        var outline = new LinesVisual3D { Color = Color.FromRgb(0xF3, 0x9C, 0x12), Thickness = 3.0 };

        foreach (var w in _vm.LevelWalls)
        {
            if (w.ReachableVBand is not { Length: 2 } band) continue;
            double vLo = band[0], vHi = band[1];
            if (vHi <= vLo) continue;
            var off = -NormalOffset(w);   // 외부 방향으로 소량 띄워 와이어와의 z-fighting 방지

            // 마구리(F/A)는 팔각 윤곽을 z-밴드로 클리핑한 다각형으로 강조.
            if (BulkheadPolygon(w, vLo, vHi) is { Count: >= 3 } poly)
            {
                for (int i = 0; i < poly.Count; i++) poly[i] += off;
                group.Children.Add(new GeometryModel3D(PolygonMesh(poly), fill) { BackMaterial = fill });
                AddClosedOutline(outline, poly);
                continue;
            }

            if (!TryCorners(w, 0, vLo, w.ULen, vHi, out var c0, out var c1, out var c2, out var c3)) continue;
            c0 += off; c1 += off; c2 += off; c3 += off;

            group.Children.Add(new GeometryModel3D(Quad(c0, c1, c2, c3), fill) { BackMaterial = fill });

            outline.Points.Add(c0); outline.Points.Add(c1);
            outline.Points.Add(c1); outline.Points.Add(c2);
            outline.Points.Add(c2); outline.Points.Add(c3);
            outline.Points.Add(c3); outline.Points.Add(c0);
        }

        LevelHighlight.Children.Add(new ModelVisual3D { Content = group });
        LevelHighlight.Children.Add(outline);
    }

    // ── 영역·작업(용접선) 오버레이 ──────────────────────────────────────────
    private void BuildOverlays()
    {
        OverlayModel.Children.Clear();
        if (_vm is null || !_vm.ShowOverlays || _vm.Overlays.Count == 0 || _vm.ShellWalls.Count == 0) return;

        int? lvl = _vm.SelectedLevel;   // 전체=null → 모든 영역, L{n}=그 층만
        var wallByCode = _vm.ShellWalls.GroupBy(w => w.WallCode).ToDictionary(g => g.Key, g => g.First());

        var areaGroup = new Model3DGroup();
        // 외곽선·용접선은 상태색별 LinesVisual3D를 지연 생성해 공유 (상태 종류 수만큼만 생성)
        var linesByColor = new Dictionary<Color, LinesVisual3D>();
        var weldByColor = new Dictionary<Color, LinesVisual3D>();
        var startPts = new PointsVisual3D { Color = Color.FromRgb(0x27, 0xAE, 0x60), Size = 11 };          // 시작=녹
        var endPts = new PointsVisual3D { Color = Color.FromRgb(0xC0, 0x39, 0x2B), Size = 11 };            // 끝=빨

        foreach (var ov in _vm.Overlays)
        {
            var a = ov.Area;
            if (lvl is int L && a.Level != L) continue;
            if (!wallByCode.TryGetValue(a.WallCode, out var wall)) continue;

            var off = -NormalOffset(wall) * 1.5;   // 외부향으로 셸보다 약간 더 띄움(가림 방지)

            // 영역 폴리곤(임의 4점) + 채움 + 이름 라벨. corners 없으면 bbox 사각형 폴백.
            var corners = a.Corners ?? new[]
            {
                new[] { a.UMin, a.VMin }, new[] { a.UMax, a.VMin }, new[] { a.UMax, a.VMax }, new[] { a.UMin, a.VMax },
            };
            var pts3d = new List<Point3D>(corners.Length);
            foreach (var p in corners)
                if (p is { Length: >= 2 } && TryPoint(wall, p[0], p[1], out var cp)) pts3d.Add(cp + off);
            if (pts3d.Count >= 3)
            {
                // work_item 상태색 (계획=녹, 대기=회, 배차=파랑, 완료=녹(진), 스킵·실패=빨강)
                var (fillC, lineC) = ViewModels.TankViewModel.StatusColors(_vm.WorkItemStatusOf(a.AreaId));
                var areaFill = new DiffuseMaterial(new SolidColorBrush(fillC));
                if (!linesByColor.TryGetValue(lineC, out var areaLines))
                    linesByColor[lineC] = areaLines = new LinesVisual3D { Color = lineC, Thickness = 2.0 };
                areaGroup.Children.Add(new GeometryModel3D(PolygonMesh(pts3d), areaFill) { BackMaterial = areaFill });
                AddClosedOutline(areaLines, pts3d);
                double cx = pts3d.Average(q => q.X), cy = pts3d.Average(q => q.Y), cz = pts3d.Average(q => q.Z);
                OverlayModel.Children.Add(new BillboardTextVisual3D
                { Text = a.Name, Position = new Point3D(cx, cy, cz), Foreground = Brushes.White, FontSize = 12 });
            }

            // 작업 용접선(시작/끝 마커 + seq 라벨) — 액션 상태색 (계획=주황)
            foreach (var t in ov.Tasks)
            {
                if (!TryPoint(wall, t.StartU, t.StartV, out var s) || !TryPoint(wall, t.EndU, t.EndV, out var e)) continue;
                s += off; e += off;
                var weldC = ViewModels.TankViewModel.WeldLineColor(_vm.TaskStatusOf(t.TaskId));
                if (!weldByColor.TryGetValue(weldC, out var weldLines))
                    weldByColor[weldC] = weldLines = new LinesVisual3D { Color = weldC, Thickness = 3.0 };
                weldLines.Points.Add(s); weldLines.Points.Add(e);
                startPts.Points.Add(s); endPts.Points.Add(e);
                var mid = new Point3D((s.X + e.X) / 2, (s.Y + e.Y) / 2, (s.Z + e.Z) / 2);
                OverlayModel.Children.Add(new BillboardTextVisual3D
                { Text = t.Seq.ToString(), Position = mid, Foreground = Brushes.Wheat, FontSize = 11 });
            }
        }

        if (areaGroup.Children.Count > 0) OverlayModel.Children.Add(new ModelVisual3D { Content = areaGroup });
        foreach (var lines in linesByColor.Values) OverlayModel.Children.Add(lines);
        foreach (var lines in weldByColor.Values) OverlayModel.Children.Add(lines);
        OverlayModel.Children.Add(startPts);
        OverlayModel.Children.Add(endPts);
    }

    /// <summary>면 로컬 (u,v) → 도면 3D 단일 점. 프레임 배열이 없으면 false.</summary>
    private static bool TryPoint(WallDto w, double u, double v, out Point3D p)
    {
        p = default;
        if (w.Origin is not { Length: 3 } o || w.UAxis is not { Length: 3 } ua || w.VAxis is not { Length: 3 } va)
            return false;
        p = new Point3D(o[0], o[1], o[2]) + new Vector3D(ua[0], ua[1], ua[2]) * u + new Vector3D(va[0], va[1], va[2]) * v;
        return true;
    }

    /// <summary>면 로컬 (u,v) 사각형의 4코너를 도면 3D로 계산. Origin/UAxis/VAxis 배열이 없으면 false.</summary>
    private static bool TryCorners(WallDto w, double uMin, double vMin, double uMax, double vMax,
        out Point3D c0, out Point3D c1, out Point3D c2, out Point3D c3)
    {
        c0 = c1 = c2 = c3 = default;
        if (w.Origin is not { Length: 3 } o || w.UAxis is not { Length: 3 } ua || w.VAxis is not { Length: 3 } va)
            return false;
        var origin = new Point3D(o[0], o[1], o[2]);
        var u = new Vector3D(ua[0], ua[1], ua[2]);
        var v = new Vector3D(va[0], va[1], va[2]);
        Point3D P(double uu, double vv) => origin + u * uu + v * vv;
        c0 = P(uMin, vMin); c1 = P(uMax, vMin); c2 = P(uMax, vMax); c3 = P(uMin, vMax);
        return true;
    }

    /// <summary>4코너 사각형 → MeshGeometry3D(삼각형 2개 + 면 법선). 조명용 법선은 코너에서 산출.</summary>
    private static MeshGeometry3D Quad(Point3D a, Point3D b, Point3D c, Point3D d)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
        var n = Vector3D.CrossProduct(b - a, d - a);
        if (n.LengthSquared > 1e-18) n.Normalize();
        for (int i = 0; i < 4; i++) m.Normals.Add(n);
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(1); m.TriangleIndices.Add(2);
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(2); m.TriangleIndices.Add(3);
        return m;
    }

    private static Vector3D NormalOffset(WallDto w) =>
        w.Normal is { Length: 3 } n ? new Vector3D(n[0], n[1], n[2]) * HighlightOffsetM : new Vector3D();

    // ── 마구리(F/A) 팔각 단면 윤곽 ──────────────────────────────────────────
    /// <summary>
    /// 마구리 면(F/A)을 z∈[zLo,zHi]로 클리핑한 팔각 단면 다각형(도면 3D). F/A가 아니거나
    /// 지오메트리 미로드면 null → 호출부가 직사각형으로 폴백. 면은 x=const 평면(마구리)이며,
    /// 반폭 halfWidth(z)로 좌/우 윤곽을 만들어 챔퍼 모서리가 모따기된 형상을 그린다.
    /// </summary>
    private List<Point3D>? BulkheadPolygon(WallDto w, double zLo, double zHi)
    {
        if (w.WallCode is not ("F" or "A")) return null;
        if (_vm?.Geometry is not { } g || w.Origin is not { Length: 3 } o) return null;
        double x = o[0], oy = g.OriginOy, h = g.Derived.H;
        zLo = Math.Max(0, zLo); zHi = Math.Min(h, zHi);
        if (zHi <= zLo) return null;

        double zWall = g.HLow + g.HWall;   // 수직벽 상단 z (챔퍼 무릎)
        var zs = new List<double> { zLo };
        foreach (var knee in new[] { g.HLow, zWall })
            if (knee > zLo + 1e-9 && knee < zHi - 1e-9) zs.Add(knee);
        zs.Add(zHi);
        zs.Sort();

        var pts = new List<Point3D>(zs.Count * 2);
        foreach (var z in zs) pts.Add(new Point3D(x, oy + HalfWidth(g, z), z));          // 우현(+y) 아래→위
        for (int i = zs.Count - 1; i >= 0; i--) pts.Add(new Point3D(x, oy - HalfWidth(g, zs[i]), zs[i])); // 좌현(−y) 위→아래
        return pts;
    }

    /// <summary>팔각 단면 반폭 y(z) — 하부챔퍼/수직벽/상부챔퍼 구간별 선형.</summary>
    private static double HalfWidth(TankGeometryDto g, double z)
    {
        double b2 = g.Derived.B / 2, wf2 = g.WFloor / 2, wc2 = g.Derived.WCeil / 2;
        double hLow = g.HLow, zWall = g.HLow + g.HWall, h = g.Derived.H;
        if (z <= hLow) return hLow > 1e-9 ? wf2 + (z / hLow) * (b2 - wf2) : b2;           // 하부 챔퍼
        if (z <= zWall) return b2;                                                         // 수직벽
        double hUp = h - zWall;
        return hUp > 1e-9 ? b2 - ((z - zWall) / hUp) * (b2 - wc2) : wc2;                   // 상부 챔퍼
    }

    /// <summary>볼록 다각형 → MeshGeometry3D(삼각형 팬 + 면 법선).</summary>
    private static MeshGeometry3D PolygonMesh(IList<Point3D> pts)
    {
        var m = new MeshGeometry3D();
        foreach (var p in pts) m.Positions.Add(p);
        var n = Vector3D.CrossProduct(pts[1] - pts[0], pts[2] - pts[0]);
        if (n.LengthSquared > 1e-18) n.Normalize();
        for (int i = 0; i < pts.Count; i++) m.Normals.Add(n);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            m.TriangleIndices.Add(0); m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1);
        }
        return m;
    }

    /// <summary>닫힌 다각형의 변을 LinesVisual3D에 누적(점쌍).</summary>
    private static void AddClosedOutline(LinesVisual3D lines, IReadOnlyList<Point3D> pts)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            lines.Points.Add(pts[i]);
            lines.Points.Add(pts[(i + 1) % pts.Count]);
        }
    }

    /// <summary>면 타입별 연한 반투명 색조(바닥/천장/수직벽/챔퍼/마구리).</summary>
    private static Brush FaceBrush(string code)
    {
        var c = code switch
        {
            "B" => Color.FromArgb(0x66, 0x2E, 0x86, 0xC1),                        // 바닥 — 진한 파랑
            "T" => Color.FromArgb(0x55, 0xAE, 0xD6, 0xF1),                        // 천장 — 연한 파랑
            "SM" or "PM" => Color.FromArgb(0x55, 0x48, 0xC9, 0xB0),               // 수직벽 — 청록
            "SL" or "PL" or "SU" or "PU" => Color.FromArgb(0x55, 0x5D, 0x6D, 0x7E), // 챔퍼 — 슬레이트
            "F" or "A" => Color.FromArgb(0x55, 0xD5, 0xA6, 0x7E),                 // 마구리 — 웜그레이
            _ => Color.FromArgb(0x55, 0x85, 0x92, 0x9E),
        };
        return new SolidColorBrush(c);
    }
}
