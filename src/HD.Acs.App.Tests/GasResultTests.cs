using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 가스 측정 결과 — 가스검사 AMR 이 검사 액션이 끝날 때 보낸 본문(JSON)을 저장하고 세이지로 올린다.
/// 결과는 잃으면 안 되므로 DB 에 쌓아 두고 순서대로 보낸다(세이지 불능이면 그대로 두고 재시도, 형식 거부면 그 건만 뺀다).
/// </summary>
public class GasResultTests
{
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");

    private static string Body(string result = "NG", int? round = 1, string tank = "GT1", string taskId = "3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47") =>
        "{\"tankId\":\"" + tank + "\",\"taskId\":\"" + taskId + "\"," + (round is int r ? $"\"round\":{r}," : "") +
        "\"robotId\":\"AMR-01\",\"startedAt\":1762151520000,\"finishedAt\":1762151785000," +
        "\"threshold\":{\"value\":5.0,\"unit\":\"ppm\"},\"result\":\"" + result + "\"," +
        "\"points\":[{\"pointSeq\":1,\"result\":\"OK\",\"scans\":[{\"retry\":0,\"u\":1200,\"v\":340,\"value\":0.8,\"result\":\"OK\",\"measuredAt\":1762151521000}]}," +
        "{\"pointSeq\":2,\"result\":\"" + result + "\",\"scans\":[{\"retry\":0,\"u\":1300,\"v\":340,\"value\":7.2,\"result\":\"" + result + "\",\"measuredAt\":1762151530000}]}]}";

    // ── 본문 해석 ──
    [Theory]
    [InlineData("OK;anchor=FULL;jobRef=JOB-1;attempt=1")]   // 용접검사 결과
    [InlineData("inspectionFailed: 비드 미검출")]             // 실패 사유
    [InlineData("{\"result\":\"OK\"}")]                      // points 없음
    [InlineData("{\"points\":[]}")]                          // result 없음
    [InlineData("{깨진 JSON")]
    [InlineData("")]
    [InlineData(null)]
    public void NonGasResults_AreNotRecorded(string? resultDescription) =>
        Assert.Null(GasResultRecorder.Create(Guid.NewGuid(), new OrderActionEntity { ActionId = Guid.NewGuid() }, resultDescription, "AMR-01"));

    [Fact]
    public void GasResult_IsRecordedWithVerdictRoundAndPoints()
    {
        var action = new OrderActionEntity { ActionId = Guid.NewGuid(), TaskId = Task1 };

        var row = GasResultRecorder.Create(Guid.NewGuid(), action, Body("NG", 2), "AMR-01")!;

        Assert.Equal((action.ActionId, Task1, "GT1", 2, "NG", 2, "PENDING"),
            (row.ActionId, row.TaskId!.Value, row.TankId, row.Round!.Value, row.Result, row.PointCount, row.SendState));
        Assert.Equal(7.2, JsonDocument.Parse(row.Body).RootElement.GetProperty("points")[1].GetProperty("scans")[0].GetProperty("value").GetDouble());
    }

    /// <summary>
    /// 세이지로 보내는 taskId 는 ACS 가 발급한 값을 세이지 조회 API 와 같은 표기로 — 앞 세 묶음이 뒤집힌 표기
    /// (용접검사 촬영 경로 때문에 세이지 쪽 taskId 가 그 표기다). AMR 이 되돌려 준 문자열을 믿지 않는다.
    /// </summary>
    [Fact]
    public void ForSaige_UsesAcsTaskIdInWireForm_AndFillsOnlyMissingValues()
    {
        Assert.True(SaigeGasResult.TryParse(Body("OK", round: null, tank: "", taskId: "not-the-acs-id"), out var body));

        var le = SaigeGasResult.ForSaige(body, Task1, taskIdLittleEndian: true, tankId: "GT1", round: 2);
        Assert.Equal(SaigeTaskId.Flip(Task1).ToString(), le["taskId"]!.GetValue<string>());
        Assert.Equal("142c9f3a-518e-7a4d-b2c9-1f6e0a5d3b47", le["taskId"]!.GetValue<string>());
        Assert.Equal(("GT1", 2), (le["tankId"]!.GetValue<string>(), le["round"]!.GetValue<int>()));   // 없던 값만 채움
        Assert.Equal("not-the-acs-id", body["taskId"]!.GetValue<string>());                           // 원본 불변

        var canonical = SaigeGasResult.ForSaige(body, Task1, taskIdLittleEndian: false, tankId: null, round: null);
        Assert.Equal(Task1.ToString(), canonical["taskId"]!.GetValue<string>());

        Assert.True(SaigeGasResult.TryParse(Body("OK", round: 1, tank: "GT1"), out var full));
        var kept = SaigeGasResult.ForSaige(full, Task1, true, tankId: "OTHER", round: 2);
        Assert.Equal(("GT1", 1), (kept["tankId"]!.GetValue<string>(), kept["round"]!.GetValue<int>()));   // AMR 값 우선
    }

    // ── 전송 ──
    private sealed class FakeSaige : HttpMessageHandler
    {
        /// <summary>요청 순번(0부터) → 응답 코드. 없으면 200.</summary>
        public Func<int, HttpStatusCode?> Respond { get; set; } = _ => HttpStatusCode.OK;
        public List<(string Path, JsonElement Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.NotNull(request.Content!.Headers.ContentLength);   // 고정 길이(chunked 아님)
            Requests.Add((request.RequestUri!.AbsolutePath,
                JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone()));
            var code = Respond(Requests.Count - 1) ?? throw new HttpRequestException("연결 불가");
            return new HttpResponseMessage(code) { Content = new StringContent("{\"message\":\"x\"}", Encoding.UTF8, "application/json") };
        }
    }

    private static (ServiceProvider Sp, SaigeGasResultSender Sender, FakeSaige Saige) Build()
    {
        var saige = new FakeSaige();
        var dbName = Guid.NewGuid().ToString();
        var sc = new ServiceCollection();
        sc.AddDbContext<AcsDbContext>(o => o.UseInMemoryDatabase(dbName));
        sc.AddHttpClient(SaigeHealthReporter.HttpClientName, c => c.BaseAddress = new Uri("http://saige.test"))
            .ConfigurePrimaryHttpMessageHandler(() => saige);
        var sp = sc.BuildServiceProvider();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Saige:BaseUrl"] = "http://saige.test", ["Acs:Saige:Gas:Enabled"] = "true", ["Acs:Saige:Gas:IntervalSec"] = "5",
        }).Build();
        return (sp, new SaigeGasResultSender(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHttpClientFactory>(), cfg, NullLogger<SaigeGasResultSender>.Instance), saige);
    }

    /// <summary>결과 n건을 받은 순서(1분 간격)로 넣는다. 반환 = taskId 목록.</summary>
    private static async Task<List<Guid>> SeedAsync(ServiceProvider sp, int n)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        var ids = new List<Guid>();
        for (int i = 0; i < n; i++)
        {
            var action = new OrderActionEntity { ActionId = Guid.NewGuid(), TaskId = Guid.NewGuid() };
            var row = GasResultRecorder.Create(Guid.NewGuid(), action, Body(i % 2 == 0 ? "OK" : "NG"), "AMR-01")!;
            row.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-n + i);
            db.GasResults.Add(row);
            ids.Add(action.TaskId!.Value);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private static async Task<List<GasResultEntity>> RowsAsync(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AcsDbContext>().GasResults.AsNoTracking()
            .OrderBy(g => g.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task Sender_PostsPendingInOrder_WithSaigeTaskId_AndMarksSent()
    {
        var (sp, sender, saige) = Build();
        var ids = await SeedAsync(sp, 3);

        Assert.Equal(3, await sender.TickAsync(default));

        Assert.All(saige.Requests, r => Assert.Equal("/admin/v3/external/gas/results", r.Path));
        Assert.Equal(ids.Select(id => SaigeTaskId.Flip(id).ToString()),                       // 받은 순서 · 세이지 표기
            saige.Requests.Select(r => r.Body.GetProperty("taskId").GetString()));
        Assert.Equal("ppm", saige.Requests[0].Body.GetProperty("threshold").GetProperty("unit").GetString());   // 본문은 그대로
        Assert.All(await RowsAsync(sp), g => Assert.Equal(("SENT", 1), (g.SendState, g.SendAttempts)));
        Assert.Equal(0, await sender.TickAsync(default));   // 보낸 것은 다시 보내지 않는다
        Assert.Equal(3, saige.Requests.Count);
    }

    /// <summary>세이지 불능(503·연결 불가) — 그 건에서 멈추고 PENDING 으로 둔다. 다음 주기에 같은 건부터 다시(순서 유지).</summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(null)]
    public async Task Sender_KeepsOrder_WhenSaigeIsDown(HttpStatusCode? failure)
    {
        var (sp, sender, saige) = Build();
        var ids = await SeedAsync(sp, 3);
        saige.Respond = i => i == 1 ? failure : HttpStatusCode.OK;   // 두 번째 건에서 실패

        Assert.Equal(1, await sender.TickAsync(default));

        var rows = await RowsAsync(sp);
        Assert.Equal(new[] { "SENT", "PENDING", "PENDING" }, rows.Select(g => g.SendState));
        Assert.NotNull(rows[1].LastError);
        Assert.True(sender.InBackoff);
        Assert.Equal(2, saige.Requests.Count);   // 세 번째 건은 시도하지 않음

        saige.Respond = _ => HttpStatusCode.OK;
        Assert.Equal(2, await sender.TickAsync(default));
        Assert.Equal(SaigeTaskId.Flip(ids[1]).ToString(), saige.Requests[2].Body.GetProperty("taskId").GetString());   // 실패했던 건부터
        Assert.All(await RowsAsync(sp), g => Assert.Equal("SENT", g.SendState));
    }

    /// <summary>세이지가 본문을 거부(400) — 다시 보내도 같으므로 그 건만 REJECTED 로 빼고 알람, 다음 건은 계속 보낸다.</summary>
    [Fact]
    public async Task Sender_RejectedResult_IsSetAside_AndTheRestGoOn()
    {
        var (sp, sender, saige) = Build();
        await SeedAsync(sp, 3);
        saige.Respond = i => i == 0 ? HttpStatusCode.BadRequest : HttpStatusCode.OK;

        Assert.Equal(2, await sender.TickAsync(default));

        var rows = await RowsAsync(sp);
        Assert.Equal(new[] { "REJECTED", "SENT", "SENT" }, rows.Select(g => g.SendState));
        Assert.Contains("HTTP 400", rows[0].LastError);
        Assert.False(sender.InBackoff);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
        Assert.Equal("SAIGE_BAD_REQUEST", (await db.Alarms.AsNoTracking().SingleAsync()).AlarmCode);
        Assert.Equal(0, await sender.TickAsync(default));   // 거부된 건은 다시 보내지 않는다
        var status = await sender.StatusAsync(db, default);
        Assert.Equal((0, 2, 1), (status.Pending, status.Sent, status.Rejected));
    }
}
