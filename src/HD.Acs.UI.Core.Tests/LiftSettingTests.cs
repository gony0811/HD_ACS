using System.Net;
using System.Text;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>서버 리프트 사용 여부(<c>/api/settings/lift</c>)로 영역 폼 리프트 칸을 켜고 끈다.</summary>
public class LiftSettingTests
{
    private sealed class Server(string? liftBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(req.RequestUri!.AbsolutePath == "/api/settings/lift" && liftBody is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(liftBody, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static async Task<AreaPlanningViewModel> LoadWith(string? liftBody)
    {
        var http = new HttpClient(new Server(liftBody)) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task 서버가_리프트를_끄면_폼_칸을_끄고_안내를_보인다()
    {
        var off = await LoadWith("{\"enabled\":false}");
        Assert.False(off.LiftEnabled);
        Assert.Contains("리프트 미사용", off.LiftHint);

        Assert.True((await LoadWith("{\"enabled\":true}")).LiftEnabled);
        Assert.True((await LoadWith(null)).LiftEnabled);   // 구서버(404) = 종전대로 켬
    }

    [Fact]
    public async Task 고정_모드면_폼_칸을_끄고_고정_높이를_안내한다()
    {
        var f = await LoadWith("{\"enabled\":true,\"mode\":\"Fixed\",\"fixedStrokeMm\":350}");
        Assert.False(f.LiftEnabled);
        Assert.Equal("Fixed", f.LiftMode);
        Assert.Contains("리프트 고정 350 mm", f.LiftHint);

        var auto = await LoadWith("{\"enabled\":true,\"mode\":\"Auto\",\"fixedStrokeMm\":null}");
        Assert.True(auto.LiftEnabled);
        Assert.Equal("", auto.LiftHint);
        Assert.False((await LoadWith("{\"enabled\":false,\"mode\":\"Off\"}")).LiftEnabled);
    }
}
