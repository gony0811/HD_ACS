using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>계획 ▸ 영역·작업 — 선택 용접선 임시 제외(DISABLE)·복귀(ENABLE). 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버에 붙인다.</summary>
public class TaskEnableTests
{
    private static readonly Guid Area = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private static readonly Guid Task2 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48");

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildAsync(bool supported = true)
    {
        var server = new FakeAcs { Supported = supported };
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));
        vm.SelectedWall = new WallDto("CT1", "PM", new[] { -22.5, 6.0, 1.9 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 },
            45, 5.4, Math.PI / 2, true, null, ReachableVBand: new[] { 2.4, 4.8 });
        await WaitAsync(() => vm.Areas.Count == 1);
        vm.SelectedArea = vm.Areas[0];
        await WaitAsync(() => vm.AreaTasks.Count == 2);
        return (vm, server);
    }

    [Fact]
    public async Task Buttons_FollowSelectedWeldState()
    {
        var (vm, _) = await BuildAsync();
        Assert.False(vm.DisableTaskCommand.CanExecute(null));       // 선택 없음
        Assert.False(vm.EnableTaskCommand.CanExecute(null));

        vm.SelectedTask = vm.AreaTasks[0];                          // 검사 대상 → DISABLE 만
        Assert.True(vm.DisableTaskCommand.CanExecute(null));
        Assert.False(vm.EnableTaskCommand.CanExecute(null));
        Assert.Equal("", vm.AreaTasks[0].EnabledLabel);
    }

    [Fact]
    public async Task Disable_ThenEnable_SendsTaskId_AndListShowsState()
    {
        var (vm, server) = await BuildAsync();
        vm.SelectedTask = vm.AreaTasks.Single(t => t.TaskId == Task2);

        await vm.DisableTaskCommand.ExecuteAsync(null);

        var (method, path, body) = Assert.Single(server.Writes);
        Assert.Equal(("PUT", "/api/area-tasks/enabled"), (method, path));
        Assert.Equal(Task2, body.GetProperty("taskIds")[0].GetGuid());
        Assert.False(body.GetProperty("enabled").GetBoolean());
        var row = vm.AreaTasks.Single(t => t.TaskId == Task2);
        Assert.False(row.Enabled);
        Assert.Equal("제외", row.EnabledLabel);
        Assert.Equal(Task2, vm.SelectedTask?.TaskId);              // 선택 유지 — 이어서 ENABLE 가능
        Assert.True(vm.EnableTaskCommand.CanExecute(null));
        Assert.False(vm.DisableTaskCommand.CanExecute(null));
        Assert.Contains("임시 제외", vm.StatusMessage);
        Assert.Contains("제외 1개 / 2개", vm.StatusMessage);

        await vm.EnableTaskCommand.ExecuteAsync(null);

        Assert.True(server.Writes[1].Body.GetProperty("enabled").GetBoolean());
        Assert.True(vm.AreaTasks.Single(t => t.TaskId == Task2).Enabled);
        Assert.Contains("복귀", vm.StatusMessage);
    }

    [Fact]
    public async Task OldServer_ShowsRebuildNotice()
    {
        var (vm, _) = await BuildAsync(supported: false);
        vm.SelectedTask = vm.AreaTasks[0];

        await vm.DisableTaskCommand.ExecuteAsync(null);

        Assert.Contains("구빌드", vm.StatusMessage);
        Assert.True(vm.AreaTasks[0].Enabled);
    }

    [Fact]
    public void OldServerResponse_WithoutField_MeansEnabled()
    {
        var t = JsonSerializer.Deserialize<AreaTaskDto>(
            """{"taskId":"3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47","seq":1,"seamType":"LINE","startU":0,"startV":0,"endU":1,"endV":0,"sectionDxfId":"D","profileId":"P"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(t!.Enabled);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "VM 비동기 로드가 제한 시간 안에 끝나지 않았습니다.");
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public bool Supported { get; set; } = true;
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        private readonly Dictionary<Guid, bool> _enabled = new() { [Task1] = true, [Task2] = true };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path == "/api/area-tasks/enabled")
            {
                if (!Supported) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Writes.Add((req.Method.Method, path, body));
                foreach (var id in body.GetProperty("taskIds").EnumerateArray()) _enabled[id.GetGuid()] = body.GetProperty("enabled").GetBoolean();
                return Json(new { enabled = body.GetProperty("enabled").GetBoolean(), changed = 1 });
            }
            if (path == "/api/internal/areas")
                return Json(new[]
                {
                    new
                    {
                        areaId = Area, tankId = "CT1", wallCode = "PM", level = 2, name = "PM-L2-01",
                        corners = new[] { new[] { 3.0, 3.0 }, new[] { 6.0, 3.0 }, new[] { 6.0, 4.6 }, new[] { 3.0, 4.6 } },
                        uMin = 3.0, vMin = 3.0, uMax = 6.0, vMax = 4.6, sortOrder = 0, taskCount = 2,
                    },
                });
            if (path == $"/api/internal/areas/{Area}/tasks")
                return Json(new[]
                {
                    new { taskId = Task1, seq = 1, name = "H01", seamType = "LINE", startU = 3.2, startV = 3.3, endU = 5.8, endV = 3.3, sectionDxfId = "DXF-1", profileId = "PROF-1", enabled = _enabled[Task1] },
                    new { taskId = Task2, seq = 2, name = "H02", seamType = "LINE", startU = 3.2, startV = 3.8, endU = 5.8, endV = 3.8, sectionDxfId = "DXF-1", profileId = "PROF-1", enabled = _enabled[Task2] },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
