using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>정차 묶음 최적화 — 작업 시작·진행 조회·결과 표/지도·구서버.</summary>
public class SetCoverUiTests
{
    private sealed class Server(bool supported = true) : HttpMessageHandler
    {
        public JsonNode? StartBody;
        public int Polls, Applied;
        private static readonly Guid Job = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid T1 = Guid.NewGuid(), T2 = Guid.NewGuid(), T3 = Guid.NewGuid();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (!supported) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Post && path == "/api/reach-sim/tanks/CT1/set-cover")
            {
                StartBody = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct));
                return Json($"{{\"jobId\":\"{Job}\"}}");
            }
            if (req.Method == HttpMethod.Post && path == $"/api/reach-sim/set-cover/{Job}/apply")
            {
                Applied++;
                return Json($"{{\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"F\",\"areas\":[{{\"areaId\":\"{Guid.NewGuid()}\",\"name\":\"L4-S001\",\"taskCount\":2,\"inherited\":true,\"unreachable\":false}}," +
                    $"{{\"areaId\":\"{Guid.NewGuid()}\",\"name\":\"L4-X001\",\"taskCount\":1,\"inherited\":false,\"unreachable\":true}}],\"deletedAreas\":1,\"scenarioLinksAdded\":0,\"tasksMoved\":2}}");
            }
            if (path == $"/api/reach-sim/set-cover/{Job}")
            {
                Polls++;
                if (Polls < 2) return Json($"{{\"jobId\":\"{Job}\",\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"F\",\"state\":\"RUNNING\",\"done\":5,\"total\":10}}");
                var seam = (Guid id, double u) => $"{{\"taskId\":\"{id}\",\"seq\":1,\"startU\":{u},\"startV\":1,\"endU\":{u + 0.36},\"endV\":1,\"ok\":true}}";
                var result = $"{{\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"F\",\"welds\":3,\"safeCovered\":2,\"clearCovered\":0," +
                    $"\"currentStations\":3,\"currentClear\":1,\"stations\":[{{\"no\":1,\"x\":15.1,\"y\":2.5,\"theta\":0,\"strokeMm\":0," +
                    $"\"tier\":\"Safe\",\"minMarginDeg\":12.3,\"window\":[0,0,1.44,1.44],\"taskIds\":[\"{T1}\",\"{T2}\"],\"seams\":[{seam(T1, 1)},{seam(T2, 1.36)}]}}]," +
                    $"\"uncovered\":[\"{T3}\"],\"cells\":100,\"evaluations\":900,\"elapsedMs\":4200," +
                    $"\"uncoveredSeams\":[{seam(T3, 5).Replace("true", "false")}]}}";
                return Json($"{{\"jobId\":\"{Job}\",\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"F\",\"state\":\"DONE\",\"done\":10,\"total\":10,\"result\":{result}}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private sealed class Confirm(bool answer) : HD.Acs.UI.Abstractions.IDialogService
    {
        public int Asked;
        public string? Message;
        public Task ShowErrorAsync(string message, string caption) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, string caption) { Asked++; Message = message; return Task.FromResult(answer); }
    }

    private static (ReachSimViewModel Vm, Server S) Make(bool supported = true, Confirm? confirm = null)
    {
        var s = new Server(supported);
        var vm = new ReachSimViewModel(new AcsApiClient(new HttpClient(s) { BaseAddress = new Uri("http://acs.test") }), null, confirm)
        {
            SetCoverPollInterval = TimeSpan.FromMilliseconds(1),
        };
        vm.SelectedLevel = 4;
        vm.SelectedWall = "F";
        return (vm, s);
    }

    [Fact]
    public async Task 시작해서_끝날_때까지_조회하고_표와_지도에_채운다()
    {
        var (vm, s) = Make();
        vm.SetCoverGridStepM = 0.5;
        await vm.RunSetCoverAsync();

        Assert.Equal(4, (int)s.StartBody!["level"]!);
        Assert.Equal("F", (string)s.StartBody!["wallCode"]!);
        Assert.Equal(0.5, (double)s.StartBody!["gridStepM"]!);
        Assert.True(s.Polls >= 2);
        var row = Assert.Single(vm.SetCoverRows);
        Assert.Equal(2, row.Welds);
        Assert.Equal("12.3°", row.Margin);
        Assert.Contains("제안 정차 1곳", vm.SetCoverSummary);
        Assert.Contains("지금 배치: 정차 3곳", vm.SetCoverSummary);
        Assert.Contains("못 닿음 1", vm.SetCoverSummary);
        Assert.True(vm.AreaMapMode);
        Assert.Equal(2, vm.AreaMapAreas.Count);                  // 제안 정차 1 + 못 닿는 용접선 묶음
        Assert.Equal(false, vm.AreaMapAreas[1].Possible);
        Assert.Contains("정차 묶음 최적화 제안", vm.AreaMapSummary);
        Assert.False(vm.IsSetCovering);
    }

    [Fact]
    public void 지도_영역은_맡은_용접선_외곽이다()
    {
        var w = new ReachWeldVerdictDto(Guid.NewGuid(), 1, 1.0, 2.0, 1.36, 2.0, true);
        var plan = new SetCoverPlanDto("CT1", 4, "F", 1, 1, 0, 1, 1,
            new List<SetCoverStationDto> { new(1, 0, 0, 0, 0, "Safe", null, new double[] { 0, 0, 1.44, 1.44 }, new() { w.TaskId }, new() { w }) },
            new(), 1, 1, 1);
        var a = Assert.Single(ReachSimViewModel.ToMapAreas(plan));
        Assert.Equal(0.95, a.Corners[0][0], 6);
        Assert.Equal(1.41, a.Corners[2][0], 6);
        Assert.Equal(true, a.Possible);
    }

    [Fact]
    public async Task 구서버면_안내한다()
    {
        var (vm, _) = Make(supported: false);
        await vm.RunSetCoverAsync();
        Assert.Contains("구빌드", vm.SetCoverSummary);
        Assert.Empty(vm.SetCoverRows);
    }

    [Fact]
    public async Task 확인하면_적용하고_한_번만_된다()
    {
        var no = new Confirm(false);
        var (vm, s) = Make(confirm: no);
        Assert.False(vm.CanApplySetCover);                         // 결과 전에는 못 누른다
        await vm.RunSetCoverAsync();
        Assert.True(vm.CanApplySetCover);
        await vm.ApplySetCoverAsync();
        Assert.Equal(1, no.Asked);
        Assert.Contains("1곳", no.Message);
        Assert.Equal(0, s.Applied);                                // 취소하면 보내지 않는다

        var yes = new Confirm(true);
        var (vm2, s2) = Make(confirm: yes);
        await vm2.RunSetCoverAsync();
        await vm2.ApplySetCoverAsync();
        Assert.Equal(1, s2.Applied);
        Assert.Contains("적용함", vm2.SetCoverSummary);
        Assert.Contains("X 영역 1곳", vm2.SetCoverSummary);
        Assert.False(vm2.CanApplySetCover);                        // 같은 결과를 두 번 적용하지 않는다
        await vm2.ApplySetCoverAsync();
        Assert.Equal(1, s2.Applied);
    }
}
