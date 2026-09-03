using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>3D 소프트웨어 투영 계층 — 카메라 투영/역투영, 마구리 팔각, 씬 빌더 구성, 깊이 정렬.</summary>
public class Rendering3DTests
{
    private const double W = 800, H = 600;

    // 단순 선창: 길이 20, 바닥폭 6, 하부챔퍼 높이 2(폭 6→10), 수직벽 4, 상부챔퍼 2(10→6) → H=8, B=10
    private static TankGeometryDto Geometry() => new(
        "T1", LengthL: 20, WFloor: 6, ThetaLowDeg: 45, HLow: 2, HWall: 4, ThetaUpDeg: 45, HUp: 2,
        LevelZ: new[] { 0.0, 3.0, 6.0 }, OriginOx: 0, OriginOy: 0,
        Derived: new TankDerivedDto(WLow: 2, B: 10, WUp: 2, WCeil: 6, H: 8));

    private static WallDto Wall(string code, double[] origin, double[] u, double[] v, double[] n, double uLen, double vLen, double[]? band = null) =>
        new("T1", code, origin, u, v, n, uLen, vLen, null, true, null, band);

    private static List<WallDto> Walls() => new()
    {
        Wall("B", new[] { -10.0, -3, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, 20, 6),
        Wall("SM", new[] { -10.0, -5, 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, 20, 4),
        Wall("F", new[] { 10.0, -5, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }, 10, 8),
    };

    [Fact]
    public void Camera_ProjectsTargetToViewportCenter_AndNearerPointsHaveSmallerDepth()
    {
        var cam = new Camera3 { Target = new Pt3(1, 2, 3), Distance = 20 };
        var (s, depth, vis) = cam.Project(cam.Target, W, H);
        Assert.True(vis);
        Assert.Equal(W / 2, s.X, 6);
        Assert.Equal(H / 2, s.Y, 6);
        Assert.Equal(20, depth, 6);

        var (f, _, _) = cam.Basis();
        var nearer = cam.Target - f * 5;   // 카메라 쪽으로 5m
        Assert.Equal(15, cam.Project(nearer, W, H).Depth, 6);
        Assert.False(cam.Project(cam.Eye - f, W, H).Visible);   // 카메라 뒤
    }

    [Fact]
    public void Camera_UnprojectRoundTrips_ThroughPlaneHit()
    {
        var cam = new Camera3 { Target = new Pt3(0, 0, 0), Distance = 25, PitchDeg = 35 };
        var p = new Pt3(3.5, -2.25, 0);
        var (s, _, _) = cam.Project(p, W, H);
        var hit = cam.HitPlaneZ(s, 0, W, H);
        Assert.NotNull(hit);
        Assert.Equal(p.X, hit!.Value.X, 6);
        Assert.Equal(p.Y, hit.Value.Y, 6);
    }

    [Fact]
    public void Camera_ZoomExtents_BringsAllCornersIntoView()
    {
        var cam = new Camera3();
        var box = new[] { new Pt3(-10, -5, 0), new Pt3(10, 5, 8), new Pt3(-10, 5, 8), new Pt3(10, -5, 0) };
        cam.ZoomExtents(box, W, H);
        Assert.Equal(new Pt3(0, 0, 4), cam.Target);
        foreach (var c in box)
        {
            var (s, _, vis) = cam.Project(c, W, H);
            Assert.True(vis);
            Assert.InRange(s.X, 0, W);
            Assert.InRange(s.Y, 0, H);
        }
    }

    [Fact]
    public void TankShape_BulkheadOctagon_HasEightVertices_AndClipsToBand()
    {
        var g = Geometry();
        var f = Walls().First(w => w.WallCode == "F");
        var full = TankShape.BulkheadPolygon(f, g, 0, g.Derived.H)!;
        Assert.Equal(8, full.Count);
        Assert.All(full, p => Assert.Equal(10, p.X, 9));           // 마구리 x=const
        Assert.Equal(3, full[0].Y, 9);                             // 바닥 우현 반폭 = wf/2
        Assert.Equal(5, full[1].Y, 9);                             // 무릎(z=2) 반폭 = B/2

        var band = TankShape.BulkheadPolygon(f, g, 3, 5)!;         // 수직벽 구간만 → 사각형
        Assert.Equal(4, band.Count);
        Assert.Null(TankShape.BulkheadPolygon(Walls()[0], g, 0, 8));   // 바닥은 팔각 아님
        Assert.Equal(4, TankShape.HalfWidth(g, 1), 9);             // 하부 챔퍼 중간: 3→5 선형
    }

    [Fact]
    public void SceneBuilder_ShellAndHighlightAndOverlays_AreEmitted()
    {
        var g = Geometry();
        var walls = Walls();
        var areaId = Guid.NewGuid(); var taskId = Guid.NewGuid();
        var area = new AreaDto(areaId, "T1", "SM", 1, "A1", 2, 0.5, 6, 2.5, null, null, null, 0, 1,
            Corners: new[] { new[] { 2.0, 0.5 }, new[] { 6.0, 0.5 }, new[] { 6.0, 2.5 }, new[] { 2.0, 2.5 } });
        var task = new AreaTaskDto(taskId, 1, "W1", "LINE", 2.5, 1, 5.5, 1, "dxf", "prof");
        var overlays = new[] { new TankViewModel.AreaOverlay(area, new[] { task }) };
        var levelWalls = new[] { Wall("SM", walls[1].Origin!, walls[1].UAxis!, walls[1].VAxis!, walls[1].Normal!, 20, 4, band: new[] { 0.0, 3.0 }) };

        var input = new TankSceneInput(walls, levelWalls, g, overlays, ShowOverlays: true, SelectedLevel: 1,
            _ => "DISPATCHED", _ => "RUNNING", HasRobotPosition: true, RobotPosition: new Pt3(1, 1, 0));
        var scene = TankSceneBuilder.Build(input);

        // 격리 모드: 셸 채움 없음(3면 모두 Fill=null) + 층 밴드 1면(채움) + 영역 1면 + 바닥 히트 평면 1면
        Assert.Equal(3, scene.Faces.Count(f => f.Fill is null));
        Assert.Contains(scene.Faces, f => f.Fill is { } c
            && c.A == TankSceneBuilder.LevelHighlightAlpha
            && c.R == 0xF5 && c.G == 0xB0 && c.B == 0x41);   // 선택 층: 옅은 황금색 채움
        Assert.Contains(scene.Faces, f => f.Fill == TankViewModel.StatusColors("DISPATCHED").Fill);
        Assert.Contains(scene.Faces, f => f.Points.Count == 4 && f.Fill is { } c && c.A == 0x16);   // 바닥 히트 평면
        Assert.Contains(scene.Segments, s => s.Color == TankViewModel.WeldLineColor("RUNNING") && s.Thickness == 3.0);
        Assert.Contains(scene.Labels, l => l.Text == "A1");
        Assert.Contains(scene.Labels, l => l.Text == "1");
        Assert.Contains(scene.Markers, m => m.RadiusWorld == 0.4);   // 로봇
        Assert.Equal(8 + 4 + 4, scene.ExtentPoints.Count);           // 마구리 8 + 바닥 4 + 수직벽 4

        var plane = TankSceneBuilder.FloorPlane(input);
        Assert.NotNull(plane);
        Assert.Equal(0, plane!.Value.Z);
        Assert.Equal(-10, plane.Value.X0); Assert.Equal(10, plane.Value.X1);
        Assert.Equal(-3, plane.Value.Y0); Assert.Equal(3, plane.Value.Y1);   // z=0 반폭 = wf/2

        // 전체 모드: 셸 채움, 밴드·바닥 없음
        var all = TankSceneBuilder.Build(input with { SelectedLevel = null, LevelWalls = Array.Empty<WallDto>() });
        Assert.Equal(3, all.Faces.Count(f => f.Fill is not null && f.Shade));
        Assert.Null(TankSceneBuilder.FloorPlane(input with { SelectedLevel = null }));
    }

    [Fact]
    public void SceneBuilder_RobotHeading_EmitsArrowOnlyWhenKnown()
    {
        var g = Geometry();
        var walls = Walls();
        var baseInput = new TankSceneInput(walls, Array.Empty<WallDto>(), g, Array.Empty<TankViewModel.AreaOverlay>(),
            ShowOverlays: false, SelectedLevel: null, _ => null, _ => null,
            HasRobotPosition: true, RobotPosition: new Pt3(2, 1, 0));

        var without = TankSceneBuilder.Build(baseInput);
        var with = TankSceneBuilder.Build(baseInput with { RobotHeading = Math.PI / 2 });   // 도면 +y 방향

        // theta 없음 → 원만. 있음 → 3D 화살표 = 축 프리즘 옆면 4 + 화살촉 사각뿔(밑면 1 + 옆면 4) = 9면, 선분은 그대로
        Assert.Equal(without.Segments.Count, with.Segments.Count);
        Assert.Equal(without.Faces.Count + 9, with.Faces.Count);
        var arrow = with.Faces.Skip(without.Faces.Count).ToList();
        Assert.Equal(4, arrow.Count(f => f.Points.Count == 3));   // 화살촉 옆면
        Assert.Equal(5, arrow.Count(f => f.Points.Count == 4));   // 축 4 + 화살촉 밑면 1
        Assert.All(arrow, f => Assert.True(f.Shade));             // 입체감 = 플랫 셰이딩

        // +y heading: 마커 원 중심 높이(z+0.4)에서 +y로 뻗고, 끝점 x는 로봇 x와 같다
        var pts = arrow.SelectMany(f => f.Points).ToList();
        var tip = pts.MaxBy(p => p.Y);
        Assert.Equal(1 + TankSceneBuilder.HeadingTipM, tip.Y, 9);
        Assert.Equal(2, tip.X, 9);
        Assert.Equal(TankSceneBuilder.RobotMarkerLiftM, tip.Z, 9);
        Assert.Equal(1, pts.Min(p => p.Y), 9);                                   // 축 시작 = 원 중심
        Assert.Equal(TankSceneBuilder.HeadingHeadHalfM, pts.Max(p => p.X) - 2, 9); // 화살촉 반폭

        // 위치 없음 → heading이 있어도 아무것도 그리지 않음
        var none = TankSceneBuilder.Build(baseInput with { HasRobotPosition = false, RobotHeading = 0.3 });
        Assert.Equal(without.Segments.Count, none.Segments.Count);
        Assert.DoesNotContain(none.Markers, m => m.RadiusWorld == 0.4);
    }

    [Fact]
    public void SceneBuilder_MoveHeading_EmitsPurpleDirectionArrow()
    {
        var input = new TankSceneInput(Walls(), Array.Empty<WallDto>(), Geometry(),
            Array.Empty<TankViewModel.AreaOverlay>(), false, 1, _ => null, _ => null,
            HasRobotPosition: false, RobotPosition: Pt3.Zero,
            MoveMarker: new Pt3(2, 3, 0.25), MoveHeading: 0);

        var scene = TankSceneBuilder.Build(input);

        Assert.Contains(scene.Markers, marker => marker.Center == new Pt3(2, 3, 0.25) && marker.RadiusWorld == 0.25);
        Assert.Equal(9, scene.Faces.Count(face => face.Fill is { } c && c.R == 0x9B && c.G == 0x59 && c.B == 0xB6));
        Assert.Contains(scene.Faces.SelectMany(face => face.Points), point => Math.Abs(point.X - 3.35) < 1e-9);
    }

    [Theory]
    [InlineData(Math.PI / 2, Math.PI / 2, 0)]            // 맵 +y를 보는 로봇, 맵이 도면 대비 +90° 회전 → 도면 +x
    [InlineData(0, Math.PI / 2, -Math.PI / 2)]           // 맵 +x → 도면 −y
    [InlineData(3.0, -3.0, 6.0 - 2 * Math.PI)]           // 차가 π를 넘으면 (−π, π]로 감음
    [InlineData(-3.0, 3.0, -6.0 + 2 * Math.PI)]
    public void MapThetaToDrawing_SubtractsCalibrationYaw_AndWraps(double thetaMap, double yaw, double expected)
    {
        Assert.Equal(expected, TankViewModel.MapThetaToDrawing(thetaMap, yaw), 9);
    }

    [Fact]
    public void Renderer_SortsFarFacesFirst_AndOverlaysLast()
    {
        var cam = new Camera3 { Target = Pt3.Zero, Distance = 30, PitchDeg = 20, YawDeg = 180 };   // -x 쪽에서 봄
        var (f, _, _) = cam.Basis();
        var near = cam.Target - f * 5;
        var far = cam.Target + f * 5;
        Pt3[] Quad(Pt3 c) => new[] { c + new Pt3(0, -1, -1), c + new Pt3(0, 1, -1), c + new Pt3(0, 1, 1), c + new Pt3(0, -1, 1) };

        var scene = new Scene3();
        scene.Faces.Add(new Face3(Quad(near), Rgba.FromRgb(1, 1, 1), null));
        scene.Faces.Add(new Face3(Quad(far), Rgba.FromRgb(2, 2, 2), null));
        scene.Markers.Add(new Marker3(far, Rgba.FromRgb(3, 3, 3), RadiusPx: 4));
        scene.Labels.Add(new Label3(near, "L", Rgba.FromRgb(4, 4, 4)));

        var draws = SceneRenderer.Render(scene, cam, W, H);
        Assert.Equal(4, draws.Count);
        Assert.IsType<Face2>(draws[0]);
        Assert.True(draws[0].Depth > draws[1].Depth);                       // 먼 면 먼저
        Assert.IsType<Marker2>(draws[2]);                                    // 마커·라벨은 면 뒤(오버레이)
        Assert.IsType<Label2>(draws[3]);
        Assert.NotEqual(((Face2)draws[0]).Fill, Rgba.FromRgb(2, 2, 2));      // 셰이딩 적용됨
    }
}
