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
    [InlineData(true, 2, 2)]        // 가스검사 선창 + 2회차
    [InlineData(true, 0, null)]     // 가스검사 선창 + 회차 없음
    [InlineData(false, 2, null)]    // 용접검사 선창 — 고른 값이 남아 있어도 보내지 않는다
    public async Task StartRun_SendsRoundOnlyForGasTank(bool gas, int roundIndex, int? sent)
    {
        var server = new FakeAcs();
        var api = new AcsApiClient(new HttpClient(server) { BaseAddress = new Uri("http://acs.test") });

        await api.StartRunAsync(Guid.NewGuid(), "AMR-01", gas && roundIndex is 1 or 2 ? roundIndex : null, default);

        var round = Assert.Single(server.RunPosts).GetProperty("round");
        Assert.Equal(sent, round.ValueKind == JsonValueKind.Null ? null : round.GetInt32());
        Assert.Equal(new[] { "회차 없음", "1회차", "2회차" }, MissionRoundLabels);
    }

    private static readonly string[] MissionRoundLabels = { "회차 없음", "1회차", "2회차" };
}
