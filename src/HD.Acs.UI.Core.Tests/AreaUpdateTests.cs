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

    /// <summary>후진 상한 빈값 = 덮어쓰기 해제(null 전송 → 서버 기본).</summary>
    [Fact]
    public async Task UpdateArea_ClearedMaxBackward_SendsNull()
    {
        var (vm, server) = await BuildAsync();
        Assert.Equal(150, vm.StationMaxBackwardMm);   // 폼 채우기
        vm.StationMaxBackwardMm = null;
        await vm.UpdateAreaCommand.ExecuteAsync(null);
        var (_, _, body) = Assert.Single(server.Writes);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("stationMaxBackwardMm").ValueKind);
    }

    /// <summary>리프트 높이 — 입력값을 그대로 보내고, 빈값이면 null(서버가 기존 값 유지).</summary>
    [Fact]
    public async Task UpdateArea_SendsLiftHeight_AndNullWhenBlank()
    {
        var (vm, server) = await BuildAsync();
        vm.StationLiftMm = 300;
        await vm.UpdateAreaCommand.ExecuteAsync(null);
        Assert.Equal(300, server.Writes[^1].Item3.GetProperty("stationLiftMm").GetDouble());

        vm.StationLiftMm = null;
        await vm.UpdateAreaCommand.ExecuteAsync(null);
        Assert.Equal(JsonValueKind.Null, server.Writes[^1].Item3.GetProperty("stationLiftMm").ValueKind);
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
        Assert.Equal(150, body.GetProperty("stationMaxBackwardMm").GetDouble());   // 영역 후진 상한(N23) 유지 — 전체 교체라 빠지면 해제된다

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

    [Fact]
    public async Task UpdateArea_TooTall_MessageNamesFaceLimit()
    {
        var (vm, server) = await BuildAsync();
        vm.C1V = 0; vm.C2V = 0; vm.C3V = 3.0; vm.C4V = 3.0;   // PM 면, v 폭 3.0m > 2.9m

        await vm.UpdateAreaCommand.ExecuteAsync(null);

        Assert.Empty(server.Writes);
        Assert.Contains("v 2.9m", vm.StatusMessage);
    }

    [Fact]
    public async Task SplitTasks_AddingTasksAutoExpandsCorners_AndSplitsToNewArea()
    {
        var (vm, server) = await BuildAsync();
        var t1 = new AreaTaskDto(Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, null, "LINE_I", 3.2, 3.0 - VOff, 3.6, 3.0 - VOff, "R0", "");
        var t2 = new AreaTaskDto(Guid.Parse("22222222-2222-4222-8222-222222222222"), 2, null, "LINE_I", 3.3, 3.0 - VOff, 3.8, 3.5 - VOff, "R0", "");

        Assert.True(vm.UpdateAreaCommand.CanExecute(null));

        // 1번째 작업 추가 → P1~P4 자동 맞춤 + 새 영역 이름 추천
        vm.AddTaskToSplitCommand.Execute(t1);
        Assert.Single(vm.SplitTasks);
        // 폼이 새 영역용으로 바뀌었으므로 기존 영역 수정은 막힌다(덮어쓰기 방지)
        Assert.False(vm.UpdateAreaCommand.CanExecute(null));
        Assert.Equal("PM-L2-01-S1", vm.AreaName);
        Assert.Equal(3.15, vm.C1U, 6);
        Assert.Equal(3.65, vm.C3U, 6);
        Assert.Equal(3.0 - 0.05 - VOff, vm.C1V, 6);
        Assert.Equal(3.0 + 0.05 - VOff, vm.C3V, 6);

        // 2번째 작업 추가 → P1~P4가 두 작업을 모두 감싸도록 자동 확장
        vm.AddTaskToSplitCommand.Execute(t2);
        Assert.Equal(2, vm.SplitTasks.Count);
        Assert.Equal(3.15, vm.C1U, 6);
        Assert.Equal(3.85, vm.C3U, 6);
        Assert.Equal(3.0 - 0.05 - VOff, vm.C1V, 6);
        Assert.Equal(3.5 + 0.05 - VOff, vm.C3V, 6);

        // 새 영역으로 분리·이동 실행
        await vm.SplitTasksToNewAreaCommand.ExecuteAsync(null);

        var (method, path, body) = Assert.Single(server.Writes);
        Assert.Equal(("POST", "/api/areas/split-tasks"), (method, path));
        Assert.Equal("CT1", body.GetProperty("tankId").GetString());
        Assert.Equal("PM", body.GetProperty("wallCode").GetString());
        Assert.Equal("PM-L2-01-S1", body.GetProperty("name").GetString());

        var ids = body.GetProperty("taskIds");
        Assert.Equal(2, ids.GetArrayLength());
        Assert.Equal(t1.TaskId, ids[0].GetGuid());
        Assert.Equal(t2.TaskId, ids[1].GetGuid());

        var c = body.GetProperty("corners");
        Assert.Equal(4, c.GetArrayLength());
        Assert.Equal(3.15, c[0][0].GetDouble(), 6);
        Assert.Equal(2.95, c[0][1].GetDouble(), 6);   // 층-로컬 → 면-전체 v
        Assert.Equal(3.85, c[2][0].GetDouble(), 6);
        Assert.Equal(3.55, c[2][1].GetDouble(), 6);

        Assert.Empty(vm.SplitTasks);
        Assert.Contains("taskId 유지", vm.StatusMessage);
        Assert.Equal(vm.SelectedArea is not null, vm.UpdateAreaCommand.CanExecute(null));   // 묶음이 비면 다시 수정 가능
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
                if (path == "/api/areas/split-tasks")
                    return Json(new { areaId = Area, level = 2, movedCount = 2, corners = new[] { new[] { 3.15, 2.95 }, new[] { 3.85, 2.95 }, new[] { 3.85, 3.55 }, new[] { 3.15, 3.55 } } });
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
                        stationStandoffM = 1.2, stationMaxBackwardMm = 150.0, sortOrder = 0, taskCount = 0,
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
