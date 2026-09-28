using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>운영 화면 3D 뷰·전개도 세부 표시 항목(우클릭 메뉴) — 꺼진 항목은 도형 자체를 만들지 않는다.</summary>
public class DisplayLayerTests
{
    private static WallDto Wall() => new("T1", "SM", new[] { -10.0, -4, 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 },
        new[] { 0.0, 1, 0 }, 20, 4, null, true, null);

    private static TankSceneInput Input(SceneLayers? layers, int tasks = 3)
    {
        var area = new AreaDto(Guid.NewGuid(), "T1", "SM", 1, "A1", 1, 0.5, 5, 2.5, null, null, null, 0, tasks,
            Corners: new[] { new[] { 1.0, 0.5 }, new[] { 5.0, 0.5 }, new[] { 5.0, 2.5 }, new[] { 1.0, 2.5 } });
        var list = Enumerable.Range(0, tasks)
            .Select(i => new AreaTaskDto(Guid.NewGuid(), i + 1, null, "LINE", 1.5 + i, 1, 1.8 + i, 1, "dxf", "prof"))
            .ToArray();
        return new TankSceneInput(new[] { Wall() }, Array.Empty<WallDto>(), null,
            new[] { new TankViewModel.AreaOverlay(area, list) }, ShowOverlays: true, SelectedLevel: null,
            _ => null, _ => null, HasRobotPosition: true, RobotPosition: new Pt3(0, 0, 0), RobotHeading: 0.5, Layers: layers);
    }

    [Fact]
    public void Default_ShowsEverything_LikeBefore()
    {
        var scene = TankSceneBuilder.Build(Input(null));
        Assert.Contains(scene.Labels, l => l.Text == "A1");
        Assert.Equal(3, scene.Labels.Count(l => l.Text is "1" or "2" or "3"));        // 작업 순번
        Assert.Equal(6, scene.Markers.Count(m => m.RadiusPx == 5.5));                   // 시작·끝점 3쌍
        Assert.Equal(18, scene.Segments.Count(s => s.Thickness == 0.8));                // 지면 격자 9+9
    }

    [Fact]
    public void DisabledLayers_AreOmittedFromScene()
    {
        var none = new SceneLayers(AreaFills: false, AreaLabels: false, WeldLines: false, WeldEndpoints: false,
            TaskSeq: false, FloorGrid: false, GroundGrid: false);
        var scene = TankSceneBuilder.Build(Input(none));

        Assert.Empty(scene.Labels);
        Assert.DoesNotContain(scene.Markers, m => m.RadiusPx == 5.5);
        Assert.DoesNotContain(scene.Segments, s => s.Thickness is 3.0 or 2.0 or 0.8);  // 용접선·영역 윤곽·지면 격자
        Assert.DoesNotContain(scene.Faces, f => f.Fill is not null && !f.Shade);         // 영역 채움 없음
        Assert.Contains(scene.Markers, m => m.RadiusWorld == 0.4);                     // 로봇은 항상 표시
    }

    [Fact]
    public void EachLayer_ControlsOnlyItsOwnPrimitives()
    {
        var onlyWeld = new SceneLayers(AreaFills: false, AreaLabels: false, WeldLines: true, WeldEndpoints: false,
            TaskSeq: false, FloorGrid: false, GroundGrid: false);
        var scene = TankSceneBuilder.Build(Input(onlyWeld));
        Assert.Equal(3, scene.Segments.Count(s => s.Thickness == 3.0));
        Assert.Empty(scene.Labels);

        var onlyNames = onlyWeld with { WeldLines = false, AreaLabels = true };
        scene = TankSceneBuilder.Build(Input(onlyNames));
        Assert.Equal(new[] { "A1" }, scene.Labels.Select(l => l.Text));
        Assert.DoesNotContain(scene.Segments, s => s.Thickness == 3.0);
    }

    [Fact]
    public void FloorGridOff_KeepsHitPlane_ForManualMove()
    {
        var g = new TankGeometryDto("T1", 20, 6, 45, 2, 4, 45, 2, new[] { 0.0, 3.0 }, 0, 0,
            new TankDerivedDto(WLow: 2, B: 10, WUp: 2, WCeil: 6, H: 8));
        var input = Input(new SceneLayers(FloorGrid: false)) with { Geometry = g, SelectedLevel = 1 };
        var scene = TankSceneBuilder.BuildStatic(input);
        Assert.Contains(scene.Faces, f => f.Fill is { A: 0x16 });                      // 바닥 히트 평면 유지
        Assert.DoesNotContain(scene.Segments, s => s.Thickness == 1.0 && s.A.Z == s.B.Z && Math.Abs(s.A.Z - 0.015) < 1e-9);
        Assert.NotNull(TankSceneBuilder.FloorPlane(input));
    }

    [Fact]
    public void StaticPlusDynamic_EqualsFullScene_AndDynamicHasOnlyRobot()
    {
        var input = Input(null);
        var full = TankSceneBuilder.Build(input);
        var stat = TankSceneBuilder.BuildStatic(input);
        var dyn = TankSceneBuilder.BuildDynamic(input);

        Assert.Equal(full.Faces.Count, stat.Faces.Count + dyn.Faces.Count);
        Assert.Equal(full.Segments.Count, stat.Segments.Count + dyn.Segments.Count);
        Assert.Equal(full.Markers.Count, stat.Markers.Count + dyn.Markers.Count);
        Assert.Equal(full.Labels.Count, stat.Labels.Count);
        Assert.Empty(dyn.Labels);
        Assert.Empty(dyn.Segments);
        Assert.Single(dyn.Markers);                                                     // 로봇 마커
        Assert.Equal(9, dyn.Faces.Count);                                               // heading 화살표

        var cam = new Camera3 { Target = Pt3.Zero, Distance = 40 };
        Assert.Equal(SceneRenderer.Render(full, cam, 800, 600).Count,
            SceneRenderer.Render(new[] { stat, dyn }, cam, 800, 600).Count);
    }

    [Fact]
    public void OverlayLayers_Defaults_Minimal_All()
    {
        var l = new OverlayLayers();
        Assert.Equal(new SceneLayers(WeldEndpoints: false, TaskSeq: false), l.Snapshot());   // 기본: 점·순번 끔

        l.ShowMinimalCommand.Execute(null);
        Assert.Equal(new SceneLayers(AreaLabels: false, WeldEndpoints: false, TaskSeq: false, FloorGrid: false, GroundGrid: false),
            l.Snapshot());

        l.ShowAllCommand.Execute(null);
        Assert.Equal(SceneLayers.All, l.Snapshot());
    }

    [Fact]
    public void TankViewModel_LayerAndStatusChanges_DoNotResetCamera()
    {
        var vm = new TankViewModel(Stub<IAcsApiClient>(), Stub<IMonitoringClient>());
        int view = 0, scene = 0;
        vm.ViewChanged += (_, _) => view++;
        vm.SceneInvalidated += (_, _) => scene++;

        vm.View3DLayers.AreaLabels = false;
        vm.ApplyWorkItemStatuses(new Dictionary<Guid, string> { [Guid.NewGuid()] = "DONE" });
        vm.FlatLayers.WeldLines = false;                     // 전개도만 다시 만든다(3D 씬 무관)

        Assert.Equal(0, view);                               // ViewChanged(=카메라 맞춤) 없음
        Assert.Equal(2, scene);
    }

    /// <summary>호출되면 기본값을 돌려주는 인터페이스 스텁(이벤트 구독 포함).</summary>
    private static T Stub<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null;
    }
}
