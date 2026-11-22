using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>균등 격자 재분할 — 미리보기·확인 후 적용·거부 사유·구서버.</summary>
public class GridAreasUiTests
{
    private sealed class Server(bool supported = true, bool busy = false) : HttpMessageHandler
    {
        public readonly List<(bool Apply, JsonNode Body)> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (!supported || req.Method != HttpMethod.Post || req.RequestUri!.AbsolutePath != "/api/reach-sim/tanks/CT1/grid-areas")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var apply = req.RequestUri.Query.Contains("apply=true");
            Calls.Add((apply, JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!));
            var preview = "{\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"T\",\"cellUM\":1.08,\"cellVM\":1.08,\"areasBefore\":262,\"areasAfter\":334,\"tasks\":1229," +
                          $"\"leftoverAreas\":0,\"busy\":{(busy ? "true" : "false")},\"cells\":[" +
                          "{\"name\":\"L4-R01C01\",\"row\":1,\"col\":1,\"taskCount\":3,\"uMin\":0.1,\"vMin\":0.1,\"uMax\":1.18,\"vMax\":1.18,\"leftover\":false}," +
                          "{\"name\":\"L4-R01C02\",\"row\":1,\"col\":2,\"taskCount\":7,\"uMin\":1.2,\"vMin\":0.1,\"uMax\":2.2,\"vMax\":1.0,\"leftover\":false}]}";
            var result = $"{{\"tankId\":\"CT1\",\"level\":4,\"wallCode\":\"T\",\"areas\":[{{\"areaId\":\"{Guid.NewGuid()}\",\"name\":\"L4-R01C01\",\"taskCount\":3,\"inherited\":true,\"unreachable\":false}}],\"deletedAreas\":5,\"scenarioLinksAdded\":2,\"tasksMoved\":40}}";
            var json = apply ? $"{{\"applied\":true,\"preview\":{preview},\"result\":{result}}}" : $"{{\"applied\":false,\"preview\":{preview}}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Confirm(bool answer) : HD.Acs.UI.Abstractions.IDialogService
    {
        public int Asked;
        public string? Message;
        public Task ShowErrorAsync(string message, string caption) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, string caption) { Asked++; Message = message; return Task.FromResult(answer); }
    }

    private static (ReachSimViewModel Vm, Server S) Make(Confirm? confirm = null, bool supported = true, bool busy = false)
    {
        var s = new Server(supported, busy);
        var vm = new ReachSimViewModel(new AcsApiClient(new HttpClient(s) { BaseAddress = new Uri("http://acs.test") }), null, confirm);
        vm.SelectedLevel = 4;
        vm.SelectedWall = "T";
        vm.GridOptimizeAfter = false;
        return (vm, s);
    }

    [Fact]
    public async Task 미리보기는_적용하지_않고_요약을_보인다()
    {
        var (vm, s) = Make();
        vm.SetGridCellCommand.Execute("1.08");
        vm.GridCellVM = 0;
        await vm.PreviewGridAsync();

        var call = Assert.Single(s.Calls);
        Assert.False(call.Apply);
        Assert.Equal(4, (int)call.Body["level"]!);
        Assert.Equal("T", (string)call.Body["wallCode"]!);
        Assert.Equal(1.08, (double)call.Body["cellUM"]!);
        Assert.Null(call.Body["cellVM"]);                               // 0 = 가로와 같게(서버가 정한다)
        Assert.Contains("미리보기", vm.GridSummary);
        Assert.Contains("영역 262 → 334개", vm.GridSummary);
        Assert.Contains("영역당 용접선 3~7개", vm.GridSummary);
        Assert.False(vm.IsGridding);
    }

    [Fact]
    public async Task 확인을_취소하면_적용하지_않고_확인하면_적용한다()
    {
        var no = new Confirm(false);
        var (vm, s) = Make(no);
        await vm.ApplyGridAsync();
        Assert.Equal(1, no.Asked);
        Assert.Contains("262개 → 334개", no.Message);
        Assert.Contains("정차 덮어쓰기", no.Message);
        Assert.DoesNotContain(s.Calls, c => c.Apply);

        var (vm2, s2) = Make(new Confirm(true));
        vm2.GridCellVM = 1.2;
        await vm2.ApplyGridAsync();
        var applied = Assert.Single(s2.Calls, c => c.Apply);
        Assert.Equal(1.2, (double)applied.Body["cellVM"]!);
        Assert.Contains("적용함", vm2.GridSummary);
        Assert.Contains("옮긴 작업 40", vm2.GridSummary);
        Assert.Contains("시나리오 연결 추가 2", vm2.GridSummary);
        Assert.False(vm2.IsGridding);
    }

    [Fact]
    public async Task 진행_중_run_이거나_구서버면_적용하지_않고_안내한다()
    {
        var yes = new Confirm(true);
        var (vm, s) = Make(yes, busy: true);
        await vm.ApplyGridAsync();
        Assert.Equal(0, yes.Asked);
        Assert.DoesNotContain(s.Calls, c => c.Apply);
        Assert.Contains("진행 중인 run", vm.GridSummary);

        var (old, _) = Make(supported: false);
        await old.PreviewGridAsync();
        Assert.Contains("구빌드", old.GridSummary);
    }
}
