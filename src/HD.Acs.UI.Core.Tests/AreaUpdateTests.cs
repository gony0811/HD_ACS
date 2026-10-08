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
/// 영역 수정(PUT) — areaId·작업 유지 수정. 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버에 붙여
/// VM이 보내는 **요청 경로·본문**(층-로컬 v → 면-전체 v 환산 포함)과 폼 채우기·선택 유지를 검증한다.
/// </summary>
public class AreaUpdateTests
{
    private static readonly Guid Area = Guid.Parse("c11e0000-0000-4000-8000-00000000000b");
    private const double VOff = 2.4;   // L2 도달 구간 하한

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildAsync(bool select = true)
    {
        var server = new FakeAcs();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));

        vm.SelectedWall = new WallDto("CT1", "PM", new[] { -22.5, 6.0, 1.9 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 },
            45, 5.4, Math.PI / 2, true, null, ReachableVBand: new[] { VOff, 4.8 });
        await WaitAsync(() => vm.Areas.Count == 1);
        if (select) vm.SelectedArea = vm.Areas[0];
        return (vm, server);
    }

    [Fact]
    public async Task SelectingArea_FillsForm_InLevelLocalV_AndEnablesUpdate()
    {
        var (vm, _) = await BuildAsync();

        Assert.Equal("PM-L2-01", vm.AreaName);
        Assert.Equal((3.0, 4.0), (vm.C1U, vm.C3U));
        Assert.Equal(0.6, vm.C1V, 9);                        // 저장값 3.0(면-전체) − VOff 2.4
        Assert.Equal(1.8, vm.C3V, 9);                        // 4.2 − 2.4
        Assert.Equal(1.2, vm.StationStandoffM);
        Assert.True(vm.StationOverride);
        Assert.Equal((-20.0, 5.0), (vm.StationX, vm.StationY));
        Assert.True(vm.UpdateAreaCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoSelection_UpdateDisabled()
    {
        var (vm, _) = await BuildAsync(select: false);
        Assert.False(vm.UpdateAreaCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateArea_PutsSameAreaId_WithFaceGlobalV_AndKeepsSelection()
    {
        var (vm, server) = await BuildAsync();

        vm.AreaName = "PM-L2-01b";
        vm.C3U = 4.2; vm.C2U = 4.2;
        vm.StationOverride = false;
        await vm.UpdateAreaCommand.ExecuteAsync(null);

        var (method, path, body) = Assert.Single(server.Writes);
        Assert.Equal(("PUT", $"/api/areas/{Area}"), (method, path));          // 삭제·POST가 아니라 같은 areaId로 PUT
        Assert.Equal("PM-L2-01b", body.GetProperty("name").GetString());
        var c = body.GetProperty("corners");
        Assert.Equal(4, c.GetArrayLength());
        Assert.Equal(0.6 + VOff, c[0][1].GetDouble(), 9);                       // 층-로컬 → 면-전체 v
        Assert.Equal(4.2, c[2][0].GetDouble(), 9);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("stationX").ValueKind); // 수동 정차 해제 = null 전송
        Assert.Equal(1.2, body.GetProperty("stationStandoffM").GetDouble());

        Assert.Equal(Area, vm.SelectedArea?.AreaId);                            // 새로고침 후에도 선택 유지
        Assert.Contains("areaId·작업 유지", vm.StatusMessage);
    }

    [Fact]
    public async Task UpdateArea_ServerRejects_ShowsReason()
    {
        var (vm, server) = await BuildAsync();
        server.PutStatus = HttpStatusCode.BadRequest;

        await vm.UpdateAreaCommand.ExecuteAsync(null);

        Assert.Contains("기존 작업이 새 영역 밖으로", vm.StatusMessage);
    }

    [Fact]
    public async Task UpdateArea_TooLarge_RejectedBeforeRoundTrip()
    {
        var (vm, server) = await BuildAsync();
        vm.C2U = 5.0; vm.C3U = 5.0;   // u 폭 2.0m > 1.44m

        await vm.UpdateAreaCommand.ExecuteAsync(null);

        Assert.Empty(server.Writes);
        Assert.Contains("1.44m", vm.StatusMessage);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "VM 비동기 로드가 제한 시간 안에 끝나지 않았습니다.");
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method != HttpMethod.Get)
            {
                var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Writes.Add((req.Method.Method, path, body));
                return PutStatus == HttpStatusCode.OK
                    ? Json(new { areaId = Area, level = 2 })
                    : Json(new { error = "기존 작업이 새 영역 밖으로 나갑니다 (seq 3)" }, PutStatus);
            }
            if (path == "/api/internal/areas")
                return Json(new[]
                {
                    new
                    {
                        areaId = Area, tankId = "CT1", wallCode = "PM", level = 2, name = "PM-L2-01",
                        corners = new[] { new[] { 3.0, 3.0 }, new[] { 4.0, 3.0 }, new[] { 4.0, 4.2 }, new[] { 3.0, 4.2 } },
                        uMin = 3.0, vMin = 3.0, uMax = 4.0, vMax = 4.2, stationX = -20.0, stationY = 5.0, stationTheta = 1.57,
                        stationStandoffM = 1.2, sortOrder = 0, taskCount = 0,
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
