using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// CROSS3/4 교차 그리기 UI [VDA §8.5.1, N13] — 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버에 붙여
/// 중심+가지 클릭 상태머신, /cross-preview 요청(면-전체 v), 등록 시 points 전달을 검증한다.
/// 회전 수치 정본은 Core CrossGeometryTests — 여기선 VM 캡처·+VOff 환산·배선을 본다.
/// </summary>
public class CrossDrawTests
{
    private static readonly Guid Area = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private const double VOff = 1.0, ULen = 30, VLen = 8, Margin = 28, Canvas = 600;
    private static readonly double Scale = Math.Min((Canvas - 2 * Margin) / ULen, (Canvas - 2 * Margin) / VLen);

    // 레벨-로컬 (u, vLocal) → 캔버스 픽셀 (Project 역산과 동일식)
    private static double Px(double u) => Margin + u * Scale;
    private static double Py(double vLocal) => Margin + (VLen - (vLocal + VOff)) * Scale;

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildAsync()
    {
        var server = new FakeAcs();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));
        // SM(우현 수직벽): U=+x, V=+z, Normal=+y. ReachableVBand=[VOff, VLen] → SliceH=VLen−VOff
        vm.SelectedWall = new WallDto("CT1", "SM", new[] { -15.0, -8.0, 3.2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 },
            ULen, VLen, -Math.PI / 2, true, null, ReachableVBand: new[] { VOff, VLen });
        await WaitAsync(() => vm.Areas.Count == 1);
        vm.SelectedArea = vm.Areas[0];
        await WaitAsync(() => vm.StartU == 0 || true);   // 작업 로드(0건) 완료 대기
        return (vm, server);
    }

    [Fact]
    public async Task Cross3_CenterPlusStemPlusThrough_DerivesRotation_And_PreviewsArms()
    {
        var (vm, server) = await BuildAsync();

        vm.StartCrossDrawCommand.Execute("CROSS3");
        Assert.Equal("CROSS3", vm.CrossDrawShape);
        Assert.False(vm.SeamTypeEditable);           // 그리기 중 콤보 잠금
        Assert.True(vm.CanvasPickActive);            // 크로스헤어 커서

        vm.CanvasClick(Px(5), Py(3));                // 중심 (u5, vLocal3)
        Assert.Equal((5.0, 3.0), (vm.StartU, vm.StartV));
        vm.CanvasClick(Px(6), Py(3));                // 줄기 +u
        vm.CanvasClick(Px(5), Py(4));                // 통과1
        vm.CanvasClick(Px(5), Py(2));                // 통과2 → 완료 → /cross-preview

        await WaitAsync(() => vm.SelectedSeamType == "CROSS3_R90");

        // 서버로 간 /cross-preview 요청: 경로·중심·가지(면-전체 v = +VOff)
        var pre = Assert.Single(server.Writes, w => w.Path.EndsWith("/cross-preview"));
        Assert.Equal($"/api/areas/{Area}/cross-preview", pre.Path);
        Assert.Equal("CROSS3", pre.Body.GetProperty("seamType").GetString());
        Assert.Equal(5.0, pre.Body.GetProperty("centerU").GetDouble(), 6);
        Assert.Equal(3.0 + VOff, pre.Body.GetProperty("centerV").GetDouble(), 6);
        var arms = pre.Body.GetProperty("arms").EnumerateArray().Select(a => (a[0].GetDouble(), a[1].GetDouble())).ToList();
        Assert.Equal(3, arms.Count);
        AssertUv(6.0, 3.0 + VOff, arms[0]);    // 줄기 +VOff
        AssertUv(5.0, 4.0 + VOff, arms[1]);
        AssertUv(5.0, 2.0 + VOff, arms[2]);

        // 유도 결과 반영
        Assert.Contains("CROSS3_R90", vm.SeamRotationText);
        Assert.Equal(3, vm.CrossPreview.Count);      // 중심→가지 3선분 미리보기
    }

    [Fact]
    public async Task Register_SendsPoints_AndDerivedSeamType_ThenClearsBuffer()
    {
        var (vm, server) = await BuildAsync();
        vm.StartCrossDrawCommand.Execute("CROSS3");
        vm.CanvasClick(Px(5), Py(3)); vm.CanvasClick(Px(6), Py(3)); vm.CanvasClick(Px(5), Py(4)); vm.CanvasClick(Px(5), Py(2));
        await WaitAsync(() => vm.SelectedSeamType == "CROSS3_R90");

        await vm.RegisterTaskCommand.ExecuteAsync(null);

        var post = Assert.Single(server.Writes, w => w.Path == $"/api/areas/{Area}/tasks");
        Assert.Equal("CROSS3_R90", post.Body.GetProperty("seamType").GetString());
        Assert.Equal(3.0 + VOff, post.Body.GetProperty("startV").GetDouble(), 6);   // 중심 v + VOff
        var pts = post.Body.GetProperty("points").EnumerateArray().Select(a => (a[0].GetDouble(), a[1].GetDouble())).ToList();
        Assert.Equal(3, pts.Count);
        AssertUv(6.0, 4.0, pts[0]); AssertUv(5.0, 5.0, pts[1]); AssertUv(5.0, 3.0, pts[2]);   // 가지(면-전체 v)

        Assert.Null(vm.CrossDrawShape);              // 등록 후 버퍼 비움
        Assert.Empty(vm.CrossPreview);
    }

    [Fact]
    public async Task FloorCeiling_FrameNotOk_WarnsAndNoPoints()
    {
        var (vm, server) = await BuildAsync();
        server.FrameOk = false;
        vm.StartCrossDrawCommand.Execute("CROSS3");
        vm.CanvasClick(Px(5), Py(3)); vm.CanvasClick(Px(6), Py(3)); vm.CanvasClick(Px(5), Py(4)); vm.CanvasClick(Px(5), Py(2));
        await WaitAsync(() => vm.SeamRotationText.Length > 0);

        Assert.Contains("바닥/천장", vm.SeamRotationText);
        await vm.RegisterTaskCommand.ExecuteAsync(null);
        var post = Assert.Single(server.Writes, w => w.Path == $"/api/areas/{Area}/tasks");
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("points").ValueKind);   // 점 미전송
    }

    [Fact]
    public async Task Cancel_ClearsDraw()
    {
        var (vm, _) = await BuildAsync();
        vm.StartCrossDrawCommand.Execute("CROSS4");
        vm.CanvasClick(Px(5), Py(3)); vm.CanvasClick(Px(6), Py(3));
        Assert.NotEmpty(vm.CrossPreview);

        vm.CancelPickCommand.Execute(null);   // ESC/우클릭
        Assert.Null(vm.CrossDrawShape);
        Assert.Empty(vm.CrossPreview);
        Assert.True(vm.SeamTypeEditable);
    }

    private static void AssertUv(double u, double v, (double U, double V) actual)
    {
        Assert.Equal(u, actual.U, 6);
        Assert.Equal(v, actual.V, 6);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "VM 비동기가 제한 시간 안에 끝나지 않았습니다.");
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        public bool FrameOk { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method != HttpMethod.Get)
            {
                var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Writes.Add((req.Method.Method, path, body));
                if (path.EndsWith("/cross-preview"))
                    return Json(new { frameOk = FrameOk, seamType = FrameOk ? "CROSS3_R90" : "CROSS3", points = FrameOk ? new[] { new[] { 100.0, 0 }, new[] { 0.0, 100 }, new[] { 0.0, -100 } } : null, snapResidualDeg = 2.0 });
                return Json(new { taskId = Guid.NewGuid(), seq = 1 });
            }
            if (path == "/api/internal/areas")
                return Json(new[]
                {
                    new
                    {
                        areaId = Area, tankId = "CT1", wallCode = "SM", level = 2, name = "SM-L2-01",
                        corners = new[] { new[] { 1.0, 1.0 }, new[] { 10.0, 1.0 }, new[] { 10.0, 6.0 }, new[] { 1.0, 6.0 } },
                        uMin = 1.0, vMin = 1.0, uMax = 10.0, vMax = 6.0, sortOrder = 0, taskCount = 0,
                    },
                });
            if (path == $"/api/internal/areas/{Area}/tasks")
                return Json(Array.Empty<object>());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
