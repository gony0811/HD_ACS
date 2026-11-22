using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 세이지 포밍가스 연동 규격 초안 v0.1 대조 — §2 결과 본문 규칙, §3 임계값 전달, §4 가스 센서 상태.
/// </summary>
public class GasSpecTests
{
    // 규격 §2 예시 본문(그대로)
    private const string SpecExample = """
        {"tankId":"CT1","taskId":"3f2b8c1e-7a4d-4e2b-9c11-5d0a6b7e8f90","round":1,"robotId":"AMR01",
         "startedAt":1762151520000,"finishedAt":1762151785000,"threshold":{"value":5.0,"unit":"ppm"},"result":"NG",
         "points":[
          {"pointSeq":1,"result":"OK","scans":[{"retry":0,"u":1200,"v":340,"value":0.8,"result":"OK","measuredAt":1762151521000}]},
          {"pointSeq":2,"result":"NG","scans":[
            {"retry":0,"u":1202,"v":340,"value":7.4,"result":"NG","measuredAt":1762151521100},
            {"retry":1,"u":1202,"v":340,"value":6.9,"result":"NG","measuredAt":1762151524000},
            {"retry":2,"u":1202,"v":340,"value":7.1,"result":"NG","measuredAt":1762151527000}]}]}
        """;

    private static JsonObject Example(Action<JsonObject>? change = null)
    {
        var o = JsonNode.Parse(SpecExample)!.AsObject();
        change?.Invoke(o);
        return o;
    }

    // ── §2 본문 규칙 ──
    [Fact]
    public void SpecExample_HasNoViolations() => Assert.Empty(SaigeGasResult.Violations(Example()));

    [Fact]
    public void Violations_CatchWhatSaigeWouldReject()
    {
        Assert.Contains(SaigeGasResult.Violations(Example(o => o.Remove("round"))), v => v.Contains("round"));       // 필수
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["round"] = 3)), v => v.Contains("round"));
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["result"] = "ERROR")), v => v.Contains("ERROR"));   // OK·NG 만
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["points"]![0]!["scans"] = new JsonArray())), v => v.Contains("scans"));   // 최초 측정 1건 필수
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["points"]![1]!["scans"]![2]!["retry"] = 3)), v => v.Contains("retry"));
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["points"]![0]!["scans"]![0]!["u"] = 1200.5)), v => v.Contains("u·v"));
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["threshold"]!.AsObject().Remove("unit"))), v => v.Contains("threshold"));
        Assert.Contains(SaigeGasResult.Violations(Example(o => o["tankId"] = "")), v => v.Contains("tankId"));
    }

    private sealed class CountingSaige : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>규격에 맞지 않는 결과(측정 실패 ERROR)는 보내지 않고 사유와 알람을 남긴다 — 뒤의 정상 결과는 그대로 나간다.</summary>
    [Fact]
    public async Task Sender_DoesNotSendOutOfSpecResult_AndExplainsWhy()
    {
        var saige = new CountingSaige();
        var dbName = Guid.NewGuid().ToString();
        var sc = new ServiceCollection();
        sc.AddDbContext<AcsDbContext>(o => o.UseInMemoryDatabase(dbName));
        sc.AddHttpClient(SaigeHealthReporter.HttpClientName, c => c.BaseAddress = new Uri("http://saige.test"))
            .ConfigurePrimaryHttpMessageHandler(() => saige);
        var sp = sc.BuildServiceProvider();
        var sender = new SaigeGasResultSender(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHttpClientFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Acs:Saige:Gas:Enabled"] = "true" }).Build(),
            NullLogger<SaigeGasResultSender>.Instance);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            GasResultEntity Row(JsonObject body, int minutesAgo)
            {
                var row = GasResultRecorder.Create(Guid.NewGuid(), new OrderActionEntity { ActionId = Guid.NewGuid(), TaskId = Guid.NewGuid() },
                    body.ToJsonString(), "AMR-01")!;
                row.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
                return row;
            }
            db.GasResults.AddRange(Row(Example(o => o["result"] = "ERROR"), 2), Row(Example(), 1));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await sender.TickAsync(default));

        Assert.Equal(1, saige.Requests);   // 규격 밖 결과는 요청 자체를 하지 않음
        using var check = sp.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AcsDbContext>().GasResults.AsNoTracking().OrderBy(g => g.CreatedAt).ToListAsync();
        Assert.Equal(new[] { "REJECTED", "SENT" }, rows.Select(g => g.SendState));
        Assert.Contains("규격 위반", rows[0].LastError);
        Assert.Equal(0, rows[0].SendAttempts);
        Assert.Equal("SAIGE_BAD_REQUEST", (await check.ServiceProvider.GetRequiredService<AcsDbContext>().Alarms.AsNoTracking().SingleAsync()).AlarmCode);
    }

    // ── §4 가스 센서 상태 ──
    private static VdaInformation Info(string type, params (string Key, string Value)[] refs) => new()
    {
        InfoType = type,
        InfoReferences = refs.Select(r => new VdaInfoReference { ReferenceKey = r.Key, ReferenceValue = r.Value }).ToList(),
    };

    [Fact]
    public void Tracker_KeepsLatestSensorState_AndDropsStaleOnes()
    {
        var tracker = new GasLinkTracker();
        Assert.Null(tracker.Sensor("AMR-01", TimeSpan.FromSeconds(30)));   // 용접검사 AMR — 보고 없음

        tracker.Update("AMR-01", new[] { Info("gasSensor", ("state", "MEASURING"), ("errorCode", "0")) });
        Assert.Equal(new GasSensorInfo("MEASURING", 0, null, default) with { At = tracker.Sensor("AMR-01", TimeSpan.FromSeconds(30))!.At },
            tracker.Sensor("AMR-01", TimeSpan.FromSeconds(30)));

        tracker.Update("AMR-01", new[] { Info("gasSensor", ("state", "ERROR"), ("errorCode", "412"), ("calibrated", "false")) });
        var s = tracker.Sensor("AMR-01", TimeSpan.FromSeconds(30))!;
        Assert.Equal(("ERROR", 412, false), (s.State, s.ErrorCode, s.Calibrated));

        Assert.Null(tracker.Sensor("AMR-01", TimeSpan.Zero - TimeSpan.FromSeconds(1)));   // 오래된 보고는 쓰지 않음
        Assert.Null(tracker.Sensor("AMR-02", TimeSpan.FromSeconds(30)));
    }

    /// <summary>상태 전송 본문 — 센서 보고가 있을 때만 sensor 를 싣고(§4 선택 항목), 없으면 용접검사 본문 그대로.</summary>
    [Fact]
    public void HealthPayload_CarriesSensorOnlyWhenReported()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plain = new SaigeRobotHealth("AMR-01", "WORKING", new SaigeRobotPosition(2, 12480, 5120, 90.0), 82, -1, 1762151600000);
        Assert.False(JsonSerializer.SerializeToElement(plain, web).TryGetProperty("sensor", out _));

        var gas = JsonSerializer.SerializeToElement(plain with { Sensor = new SaigeGasSensor("MEASURING", 0, true) }, web).GetProperty("sensor");
        Assert.Equal(("MEASURING", 0, true), (gas.GetProperty("state").GetString(), gas.GetProperty("errorCode").GetInt32(), gas.GetProperty("calibrated").GetBoolean()));
        var noCal = JsonSerializer.SerializeToElement(plain with { Sensor = new SaigeGasSensor("STANDBY", 0) }, web).GetProperty("sensor");
        Assert.False(noCal.TryGetProperty("calibrated", out _));   // AMR 이 주지 않으면 싣지 않는다
    }

    // ── §3 임계값 전달 ──
    [Theory]
    [InlineData(null, 4.5, "ppm", "op", 1L, "tankId")]
    [InlineData("CT1", null, "ppm", "op", 1L, "value")]
    [InlineData("CT1", 0.0, "ppm", "op", 1L, "value")]
    [InlineData("CT1", 4.5, " ", "op", 1L, "unit")]
    [InlineData("CT1", 4.5, "ppm", null, 1L, "requestedBy")]
    [InlineData("CT1", 4.5, "ppm", "op", null, "requestedAt")]
    public void ThresholdRequest_MissingOrBadValues_AreInvalid(string? tank, double? value, string? unit, string? by, long? at, string field) =>
        Assert.Contains(field, GasThresholdService.Validate(new GasThresholdRequest(tank, value, unit, by, at)));

    [Fact]
    public void ThresholdRequest_SpecExample_IsValid_AndBecomesInstantActionWithSameFields()
    {
        Assert.Null(GasThresholdService.Validate(new GasThresholdRequest("CT1", 4.5, "ppm", "operator01", 1762152000000)));

        var action = Assert.Single(GasThresholdService.BuildInstantAction("a-1", "CT1", 4.5, "ppm", "operator01", 1762152000000).Actions);
        Assert.Equal(("setGasThreshold", "a-1", "NONE"), (action.ActionType, action.ActionId, action.BlockingType));
        var p = action.ActionParameters.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(new object?[] { "CT1", 4.5, "ppm", "operator01", 1762152000000L },
            new[] { p["tankId"], p["value"], p["unit"], p["requestedBy"], p["requestedAt"] });
    }

    /// <summary>AMR 의 적용 확인(state.information gasThreshold) — 지령보다 먼저 처리돼도 놓치지 않고, 거부 사유도 전달된다.</summary>
    [Fact]
    public async Task Tracker_MatchesThresholdAckByActionId()
    {
        var tracker = new GasLinkTracker();

        var early = tracker.Expect("a-1");
        tracker.Update("AMR-01", new[] { Info("gasThreshold", ("value", "4.5"), ("lastActionId", "a-1"), ("lastAccepted", "true")) });
        Assert.Equal(new GasThresholdAck("a-1", true, null), await tracker.WaitAckAsync(early, "a-1", TimeSpan.FromSeconds(1), default));

        var rejected = tracker.Expect("a-2");
        tracker.Update("AMR-01", new[] { Info("gasThreshold", ("lastActionId", "a-2"), ("lastAccepted", "false"), ("lastError", "단위 불일치")) });
        Assert.Equal(new GasThresholdAck("a-2", false, "단위 불일치"), await tracker.WaitAckAsync(rejected, "a-2", TimeSpan.FromSeconds(1), default));

        var silent = tracker.Expect("a-3");
        tracker.Update("AMR-01", new[] { Info("gasThreshold", ("lastActionId", "다른 지령"), ("lastAccepted", "true")) });
        Assert.Null(await tracker.WaitAckAsync(silent, "a-3", TimeSpan.FromMilliseconds(50), default));   // 응답 없음 → 503 의 근거
    }

    private static (GasThresholdService Svc, AcsDbContext Db) BuildThreshold()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4]", InspectionKind = "GAS" });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.SaveChanges();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Acs:Saige:Gas:ThresholdAckTimeoutSec"] = "0.2" }).Build();
        return (new GasThresholdService(db, new Vda5050MasterClient("localhost"), new GasLinkTracker(), cfg,
            NullLogger<GasThresholdService>.Instance), db);
    }

    private static readonly GasThresholdRequest Good = new("CT1", 4.5, "ppm", "operator01", 1762152000000);

    [Fact]
    public async Task Threshold_BadValueOrUnknownTank_Is400()
    {
        var (svc, db) = BuildThreshold();
        await Assert.ThrowsAsync<GasThresholdInvalidException>(() => svc.ApplyAsync(Good with { Value = -1 }, default));
        await Assert.ThrowsAsync<GasThresholdInvalidException>(() => svc.ApplyAsync(Good with { TankId = "NOPE" }, default));
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    /// <summary>AMR 에 전달하지 못하면 503 — 연결된 AMR 이 없거나, 보냈지만 못 나갔거나, 적용 확인이 없을 때. 실패도 로그로 남긴다.</summary>
    [Fact]
    public async Task Threshold_NotDeliveredToAmr_Is503_AndLogged()
    {
        var (svc, db) = BuildThreshold();
        var none = await Assert.ThrowsAsync<GasThresholdUndeliveredException>(() => svc.ApplyAsync(Good, default));
        Assert.Contains("연결된 AMR 이 없습니다", none.Message);

        db.Robots.Add(new RobotEntity { RobotId = "AMR-01", Manufacturer = "HHI", SerialNumber = "AMR-01" });
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedMapId = "CT1-L1", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal("AMR-01", Assert.Single(await svc.TargetRobotsAsync("CT1", default)).RobotId);   // 그 선창의 층에 있는 로봇
        await Assert.ThrowsAsync<GasThresholdUndeliveredException>(() => svc.ApplyAsync(Good, default));   // 브로커 미연결 또는 확인 없음

        var logs = await db.AuditLogs.AsNoTracking().ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(("GAS_THRESHOLD_FAILED", "operator01", "CT1"), (l.Action, l.UserId, l.Target)));
    }
}
