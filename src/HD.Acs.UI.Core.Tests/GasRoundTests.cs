using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>가스검사 회차 — 가스검사 선창에서 고른 회차만 미션 시작 요청(round)에 실린다.</summary>
public class GasRoundTests
{
    /// <summary>아무 일도 하지 않는 대역(이벤트 구독·해지 포함).</summary>
    public class QuietProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
        {
            var rt = m!.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt == typeof(Task)) return Task.CompletedTask;
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<JsonElement> RunPosts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path == "/api/runs")
            {
                RunPosts.Add(JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
                return Json(new { runId = Guid.NewGuid() });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    [Theory]
    [InlineData(true, 1, null)]     // 가스검사 선창 — 회차를 보내지 않는다(서버가 구역별 검사 횟수를 센다)
    [InlineData(true, 0, null)]
    [InlineData(false, 1, null)]    // 용접검사 선창
    public async Task StartRun_NeverSendsRound(bool gas, int roundIndex, int? sent)
    {
        var server = new FakeAcs();
        var api = new AcsApiClient(new HttpClient(server) { BaseAddress = new Uri("http://acs.test") });

        var mission = new MissionViewModel(api, System.Reflection.DispatchProxy.Create<IMonitoringClient, QuietProxy>()) { IsGasTank = gas, RoundIndex = roundIndex };

        await api.StartRunAsync(Guid.NewGuid(), "AMR-01", mission.Round, default);

        var round = Assert.Single(server.RunPosts).GetProperty("round");
        Assert.Equal(sent, round.ValueKind == JsonValueKind.Null ? null : round.GetInt32());
    }
}
