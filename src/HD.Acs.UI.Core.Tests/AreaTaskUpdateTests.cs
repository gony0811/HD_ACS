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
/// 검사 작업 수정(PUT) — taskId 유지 수정 [SAIGE v2.6 §2.5/§10.2]. 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버에 붙여
/// VM이 보내는 **요청 경로·본문**(층-로컬 v → 면-전체 v 환산 포함)을 검증한다.
/// </summary>
public class AreaTaskUpdateTests
{
    private static readonly Guid Area = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private const double VOff = 2.4;   // L2 도달 구간 하한 — VM은 층-로컬 v로 동작하고 API 경계에서만 ±VOff

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildWithSelectedTaskAsync()
    {
        var server = new FakeAcs();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));

        vm.SelectedWall = new WallDto("CT1", "PM", new[] { -22.5, 6.0, 1.9 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 },
            45, 5.4, Math.PI / 2, true, null, ReachableVBand: new[] { VOff, 4.8 });
        await WaitAsync(() => vm.Areas.Count == 1);
        vm.SelectedArea = vm.Areas[0];
        await WaitAsync(() => vm.AreaTasks.Count == 1);
        vm.SelectedTask = vm.AreaTasks[0];
        return (vm, server);
    }

    [Fact]
    public async Task SelectingTask_FillsForm_InLevelLocalV_AndEnablesUpdate()
    {
        var (vm, _) = await BuildWithSelectedTaskAsync();

        Assert.Equal((3.2, 5.8), (vm.StartU, vm.EndU));
        Assert.Equal(0.9, vm.StartV, 9);                     // 저장값 3.3(면-전체) − VOff 2.4 = 층-로컬 0.9
        Assert.Equal("CROSS4", vm.SelectedSeamType);
        Assert.True(vm.UpdateTaskCommand.CanExecute(null));

        vm.SelectedTask = null;
        Assert.False(vm.UpdateTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateTask_PutsSameTaskId_WithFaceGlobalV_AndKeepsSelection()
    {
        var (vm, server) = await BuildWithSelectedTaskAsync();

        vm.StartU = 3.5; vm.StartV = 1.0; vm.EndU = 5.5; vm.EndV = 1.1; vm.SelectedSeamType = "LINE";
        await vm.UpdateTaskCommand.ExecuteAsync(null);

        var (method, path, body) = Assert.Single(server.Writes);
        Assert.Equal(("PUT", $"/api/area-tasks/{Task1}"), (method, path));      // 삭제·POST가 아니라 같은 taskId로 PUT
        Assert.Equal(3.5, body.GetProperty("startU").GetDouble());
        Assert.Equal(1.0 + VOff, body.GetProperty("startV").GetDouble(), 9);    // 층-로컬 → 면-전체 v
        Assert.Equal(1.1 + VOff, body.GetProperty("endV").GetDouble(), 9);
        Assert.Equal("LINE", body.GetProperty("seamType").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("seq").ValueKind);    // seq·name은 기존값 유지(null 전송)
        Assert.Equal(JsonValueKind.Null, body.GetProperty("name").ValueKind);

        Assert.Equal(Task1, vm.SelectedTask?.TaskId);                            // 새로고침 후에도 선택 유지(연속 미세 조정)
        Assert.Contains("taskId 유지", vm.StatusMessage);
    }

    [Fact]
    public async Task UpdateTask_ServerRejects_ShowsReason()
    {
        var (vm, server) = await BuildWithSelectedTaskAsync();
        server.PutStatus = HttpStatusCode.BadRequest;

        await vm.UpdateTaskCommand.ExecuteAsync(null);

        Assert.Contains("영역(사각형) 내부가 아닙니다", vm.StatusMessage);
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
                    ? Json(new { taskId = Task1 })
                    : Json(new { error = "용접선 시작/끝점이 영역(사각형) 내부가 아닙니다." }, PutStatus);
            }
            if (path == "/api/internal/areas")
                return Json(new[]
                {
                    new
                    {
                        areaId = Area, tankId = "CT1", wallCode = "PM", level = 2, name = "PM-L2-01",
                        corners = new[] { new[] { 3.0, 3.0 }, new[] { 6.0, 3.0 }, new[] { 6.0, 4.6 }, new[] { 3.0, 4.6 } },
                        uMin = 3.0, vMin = 3.0, uMax = 6.0, vMax = 4.6, sortOrder = 0, taskCount = 1,
                    },
                });
            if (path == $"/api/internal/areas/{Area}/tasks")
                return Json(new[]
                {
                    new { taskId = Task1, seq = 3, name = "W3", seamType = "CROSS4", startU = 3.2, startV = 3.3, endU = 5.8, endV = 3.3, sectionDxfId = "DXF-1", profileId = "PROF-1" },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
