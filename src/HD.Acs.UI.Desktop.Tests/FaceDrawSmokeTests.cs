using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD.Acs.UI.Drawing;
using HD.Acs.UI.Desktop.Views;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HD.Acs.UI.Desktop.Tests;

/// <summary>면 드로잉 도구 스모크 — 창 로드·툴바 컨트롤·바인딩 경로 0오류·확정 반영.</summary>
public class FaceDrawSmokeTests
{
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Messages { get; } = new();
        public BindingLog() => Logger.Sink = this;
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string t)
        { if (IsEnabled(level, area)) Messages.Add(t); }
        public void Log(LogEventLevel level, string area, object? source, string t, params object?[] v)
        { if (IsEnabled(level, area)) Messages.Add(t + string.Concat(v.Select(x => " | " + x))); }
        public void Dispose() => Logger.Sink = _previous;
    }

    [AvaloniaFact]
    public void Window_Loads_WithToolbarControls_AndNoBindingPathErrors()
    {
        using var log = new BindingLog();
        var vm = new FaceDrawingViewModel("SL", 4000, 2500);
        var win = new FaceDrawWindow(vm);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("SL", win.Title!);
        Assert.Single(win.GetVisualDescendants().OfType<FaceDrawCanvas>());
        Assert.True(win.GetVisualDescendants().OfType<RadioButton>().Count() >= 4);   // 타입 2 + 모드 2
        Assert.True(win.GetVisualDescendants().OfType<NumericUpDown>().Count() >= 2); // X/Y 간격

        win.Close();
        var pathErrors = log.Messages.Where(m => m.Contains("Could not find", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(pathErrors.Count == 0, "바인딩 경로 오류:\n" + string.Join("\n", pathErrors));
    }

    [AvaloniaFact]
    public void Vm_CommitAndSerialize_ReflectsInWindow()
    {
        var vm = new FaceDrawingViewModel("SL", 4000, 2500) { SelectedMode = DrawMode.Table, PitchX = 500, PitchY = 500 };
        var win = new FaceDrawWindow(vm);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        vm.CommitGrid(new Pt2(0, 0), new Pt2(2000, 1000));
        Assert.Single(vm.Shapes);
        Assert.Equal(4, vm.Shapes[0].CountX);   // 2000/500
        Assert.Contains("\"면\": \"SL\"", vm.SerializeJson());

        win.Close();
    }

    /// <summary>전개도 면 클릭 핸들러가 의존하는 데이터 경로 — 면(ShellWalls) 있으면 그 면 mm 크기로 VM 생성, 없으면 null(조용한 무동작 방지).</summary>
    [AvaloniaFact]
    public void CreateFaceDrawing_FromLoadedWall_ReturnsVmInMillimeters()
    {
        var host = AppHost.Build();
        using (host)
        {
            var tank = host.Services.GetRequiredService<TankViewModel>();
            Assert.Null(tank.CreateFaceDrawing("SL"));   // 면 미로드 → null

            tank.ShellWalls.Add(new WallDto("CT1", "SL", null, null, null, null, 4.0, 2.5, null, true, null));
            var draw = tank.CreateFaceDrawing("SL");
            Assert.NotNull(draw);
            Assert.Equal(4000, draw!.Face.W);   // m → mm
            Assert.Equal(2500, draw.Face.H);
        }
    }

    /// <summary>등록된 CAD가 있으면 전개도 면 클릭 시 그 면 용접선·Corrugation 선분이 시드되고, 수동 보정→저장소 반영 콜백이 연결된다.</summary>
    [AvaloniaFact]
    public async Task CreateFaceDrawing_SeedsFromRegisteredCad_AndWiresPersist()
    {
        var host = AppHost.Build();
        using (host)
        {
            var tank = host.Services.GetRequiredService<TankViewModel>();
            var store = host.Services.GetRequiredService<IFaceCadStore>();
            store.Set(new FaceCadDoc("SL", "WALL SL.dxf", new[]
            {
                new FaceCadSeg(0, 0, 3000, 0, nameof(DrawType.WeldLine)),
                new FaceCadSeg(0, 100, 200, 100, nameof(DrawType.Corrugation)),
            }));

            tank.ShellWalls.Add(new WallDto("CT1", "SL", null, null, null, null, 4.0, 2.5, null, true, null));
            var draw = tank.CreateFaceDrawing("SL");
            Assert.NotNull(draw);
            Assert.Equal(2, draw!.Shapes.Count);
            Assert.Equal(DrawType.WeldLine, draw.Shapes[0].Type);
            Assert.Equal(DrawType.Corrugation, draw.Shapes[1].Type);
            Assert.False(draw.CanUndo);   // 시드는 되돌리기 이력 없음

            // 수동 보정(종류 전환) → CAD 저장 콜백이 캐시에 반영(서버 미연결이라 DB PUT은 실패하나 캐시는 보존)
            draw.Selected = draw.Shapes[1];
            draw.ToggleSelectedTypeCommand.Execute(null);
            await draw.SaveCadCommand.ExecuteAsync(null);
            var saved = store.Get("SL")!;
            Assert.Equal(2, saved.Segments.Length);
            Assert.All(saved.Segments, s => Assert.Equal(nameof(DrawType.WeldLine), s.Kind));   // 둘 다 용접선으로 보정됨
        }
    }

    /// <summary>변별 인접 면 = 3D 공유 모서리에서 산출 — 사각형 면 3개(공유 모서리)로 이웃 코드 검증(팔각도 동일 코드 경로).</summary>
    [AvaloniaFact]
    public void CreateFaceDrawing_ComputesEdgeNeighbors_FromSharedEdges()
    {
        var host = AppHost.Build();
        using (host)
        {
            var tank = host.Services.GetRequiredService<TankViewModel>();
            WallDto W(string code, double[] o, double[] u, double[] v, double ul, double vl) =>
                new("CT1", code, o, u, v, null, ul, vl, null, true, null);
            tank.ShellWalls.Add(W("B", new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, 10, 4));   // 바닥 z=0
            tank.ShellWalls.Add(W("SM", new[] { 0.0, 4, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, 10, 3));  // y=4 벽(B와 모서리 공유)
            tank.ShellWalls.Add(W("A", new[] { 10.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, 4, 3));   // x=10 마구리(B와 모서리 공유)

            var draw = tank.CreateFaceDrawing("B");
            Assert.NotNull(draw);
            Assert.Equal(4, draw!.Outline!.Count);   // 사각형(F/A 아님)

            string CodeNear(double x, double y) =>
                draw.EdgeLabels.First(l => Math.Abs(l.Mid.X - x) < 1 && Math.Abs(l.Mid.Y - y) < 1).Code;
            Assert.Equal("A", CodeNear(10000, 2000));    // x=10 변 너머 = A
            Assert.Equal("SM", CodeNear(5000, 4000));    // y=4 변 너머 = SM
        }
    }
}
