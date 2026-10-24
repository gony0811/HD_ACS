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
/// 영역 폼 정차 입력 — 자동 정차점 표시 + 자동 정차점 기준 오프셋(dx,dy) 입력. 서버로는 종전대로 절대 도면 좌표를 보낸다.
/// 현장 사례(2026-10-24): T면 L4-R08C20에 절대 좌표 (0.5,0.5)가 들어가 정차점이 약 12m 떨어진 선창 중앙으로 발행됐다.
/// </summary>
public class StationOffsetTests
{
    private static readonly Guid Area = Guid.Parse("c11e0000-0000-4000-8000-00000000000c");

    // CT1 멤브레인 천장 T: 원점 (−15.48, −5.984, 12.96), u=+x, v=+y, 법선 −z
    private static WallDto CeilingT() => new("CT1", "T", new[] { -15.48, -5.984, 12.96 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 },
        new[] { 0.0, 0, -1 }, 30.96, 11.968, null, true, null);

    // 벽 PM: 원점 (−22.5, 6, 1.9), u=+x, v=+z, 법선 −y(내부향), 층 구간 [2.4, 4.8]
    private static WallDto WallPm() => new("CT1", "PM", new[] { -22.5, 6.0, 1.9 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 },
        new[] { 0.0, -1, 0 }, 45, 5.4, Math.PI / 2, true, null, ReachableVBand: new[] { 2.4, 4.8 });

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildAsync(WallDto wall, object area)
    {
        var server = new FakeAcs(area);
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));
        vm.SelectedWall = wall;
        await WaitAsync(() => vm.Areas.Count == 1);
        vm.SelectedArea = vm.Areas[0];
        return (vm, server);
    }

    private static object TArea(double? sx, double? sy, double? standoff = null) => new
    {
        areaId = Area, tankId = "CT1", wallCode = "T", level = 4, name = "L4-R08C20",
        corners = new[] { new[] { 27.87, 11.384 }, new[] { 27.93, 11.384 }, new[] { 27.93, 9.944 }, new[] { 27.87, 9.944 } },
        uMin = 27.87, vMin = 9.944, uMax = 27.93, vMax = 11.384, stationX = sx, stationY = sy, stationTheta = sx is null ? (double?)null : 0.0,
        stationStandoffM = standoff, sortOrder = 0, taskCount = 4,
    };

    [Fact]
    public async Task Ceiling_AutoStation_IsAreaCenterBelow_AndStandoffNotApplicable()
    {
        var (vm, _) = await BuildAsync(CeilingT(), TArea(null, null, standoff: 2.0));

        Assert.False(vm.StandoffApplicable);                         // 법선 수평 성분 0 → 이격 무시
        var a = vm.AutoStation!.Value;
        Assert.Equal(12.42, a.X, 6);                                  // 27.90 − 15.48
        Assert.Equal(4.68, a.Y, 6);                                   // 10.664 − 5.984 (이격 2.0 무시)
        Assert.Contains("x 12.42, y 4.68", vm.AutoStationText);
        Assert.Contains("영역 중심 바로 아래", vm.AutoStationText);
        Assert.Contains("이격이 적용되지 않습니다", vm.StandoffHint);
        Assert.False(vm.StationOverride);
        Assert.Equal((0.0, 0.0), (vm.StationDx, vm.StationDy));
    }

    [Fact]
    public async Task StoredAbsoluteOverride_ShownAsOffsetFromAutoStation()
    {
        // 현장 사례 값: 절대 (0.5, 0.5) → 자동 정차점 (12.42, 4.68)에서 선미 11.92 · 우현 4.18
        var (vm, _) = await BuildAsync(CeilingT(), TArea(0.5, 0.5));

        Assert.True(vm.StationOverride);
        Assert.Equal(-11.92, vm.StationDx, 6);
        Assert.Equal(-4.18, vm.StationDy, 6);
        Assert.Equal(0.5, vm.StationX, 6);
        Assert.Equal(0.5, vm.StationY, 6);
        Assert.Contains("선미 11.92 · 우현 4.18", vm.StationEffectiveText);
    }

    [Fact]
    public async Task Offset_IsSentAsAbsolute_AndFollowsCornerEdits()
    {
        var (vm, server) = await BuildAsync(CeilingT(), TArea(null, null));

        vm.StationOverride = true;
        vm.StationDx = -0.5;                                          // 선미 쪽 0.5 m
        Assert.Contains("선미 0.5", vm.StationEffectiveText);
        await vm.UpdateAreaCommand.ExecuteAsync(null);

        var (_, _, body) = Assert.Single(server.Writes);
        Assert.Equal(11.92, body.GetProperty("stationX").GetDouble(), 6);
        Assert.Equal(4.68, body.GetProperty("stationY").GetDouble(), 6);

        // 영역을 u로 +1 m 옮기면 정차점도 같은 오프셋으로 따라간다
        vm.C1U += 1; vm.C2U += 1; vm.C3U += 1; vm.C4U += 1;
        Assert.Equal(12.92, vm.StationX, 6);
    }

    [Fact]
    public async Task Wall_AutoStation_IncludesStandoff_AndOffsetRoundTrips()
    {
        var area = new
        {
            areaId = Area, tankId = "CT1", wallCode = "PM", level = 2, name = "PM-L2-01",
            corners = new[] { new[] { 3.0, 3.0 }, new[] { 4.0, 3.0 }, new[] { 4.0, 4.2 }, new[] { 3.0, 4.2 } },
            uMin = 3.0, vMin = 3.0, uMax = 4.0, vMax = 4.2, stationX = -20.0, stationY = 5.0, stationTheta = 1.57,
            stationStandoffM = 1.2, sortOrder = 0, taskCount = 0,
        };
        var (vm, _) = await BuildAsync(WallPm(), area);

        Assert.True(vm.StandoffApplicable);
        var a = vm.AutoStation!.Value;
        Assert.Equal(-19.0, a.X, 6);                                  // u 중심 3.5 − 22.5
        Assert.Equal(4.8, a.Y, 6);                                    // 6 + (−1)·1.2 (이격)
        Assert.Equal(-1.0, vm.StationDx, 6);
        Assert.Equal(0.2, vm.StationDy, 6);

        vm.StationStandoffM = null;                                   // 빈값 = 서버 기본 0.8
        Assert.Equal(6.0 - AreaPlanningViewModel.DefaultStandoffM, vm.AutoStation!.Value.Y, 6);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "VM 비동기 로드가 제한 시간 안에 끝나지 않았습니다.");
    }

    private sealed class FakeAcs(object area) : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method != HttpMethod.Get)
            {
                var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Writes.Add((req.Method.Method, path, body));
                return Json(new { areaId = Area, level = 4 });
            }
            if (path == "/api/internal/areas") return Json(new[] { area });
            if (path == $"/api/internal/areas/{Area}/tasks") return Json(Array.Empty<object>());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
