using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>SAIGE 연동 1단계 [사양서 v2.6 §5.3/§5.5/§5.6/§9] — EF InMemory + 가짜 SAIGE 수신기.</summary>
public class SaigeInterfaceTests
{
    private static readonly Guid Scenario = Guid.NewGuid();
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Area = Guid.NewGuid();
    private static readonly Guid TaskOk = Guid.NewGuid(), TaskFail = Guid.NewGuid(), TaskRetrying = Guid.NewGuid();

    private static ServiceProvider BuildServices(HttpMessageHandler? saige = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var sc = new ServiceCollection();
        sc.AddDbContext<AcsDbContext>(o => o.UseInMemoryDatabase(dbName));
        sc.AddHttpClient(SaigeHealthReporter.HttpClientName, c => c.BaseAddress = new Uri("http://saige.test"))
            .ConfigurePrimaryHttpMessageHandler(() => saige ?? new FakeSaige(HttpStatusCode.OK));
        return sc.BuildServiceProvider();
    }

    /// <summary>run 1건: 영역 A(SKIPPED: 성공 1 + 2회 실패 1) + 영역 B(PENDING: 실패 1회, 재시도 대기).</summary>
    private static async Task SeedRunAsync(AcsDbContext db)
    {
        var areaB = Guid.NewGuid();
        var wiA = Guid.NewGuid(); var wiB = Guid.NewGuid(); var mission = Guid.NewGuid();
        db.Scenarios.Add(new ScenarioEntity { ScenarioId = Scenario, Name = "CT1 검사", Version = 1, TankId = "CT1" });
        db.ScenarioRuns.Add(new ScenarioRunEntity
        {
            RunId = Run, ScenarioId = Scenario, ScenarioVer = 1, RobotId = "AMR01", State = "RUNNING",
            StartedAt = new DateTimeOffset(2026, 8, 19, 14, 0, 0, TimeSpan.FromHours(9)),   // KST → 응답은 UTC Z
            Missions =
            {
                new MissionEntity { MissionId = mission, RunId = Run, Seq = 0, MapId = "CT1-L2", RobotId = "AMR01", State = "Running" },
            },
        });
        db.InspectionAreas.Add(new InspectionAreaEntity { AreaId = Area, TankId = "CT1", WallCode = "PM", Level = 2, Name = "PM-L2-01", Corners = "[]" });
        db.InspectionAreas.Add(new InspectionAreaEntity { AreaId = areaB, TankId = "CT1", WallCode = "SU", Level = 2, Name = "SU-L2-01", Corners = "[]" });
        db.AreaTasks.Add(new AreaTaskEntity { TaskId = TaskOk, AreaId = Area, Seq = 1, StartU = 3.2, StartV = 0.9, EndU = 5.8, EndV = 0.9 });
        db.AreaTasks.Add(new AreaTaskEntity { TaskId = TaskFail, AreaId = Area, Seq = 2, StartU = 12.5, StartV = 4.2, EndU = 17.8, EndV = 4.2 });
        db.AreaTasks.Add(new AreaTaskEntity { TaskId = TaskRetrying, AreaId = areaB, Seq = 1, StartU = 1, StartV = 1, EndU = 2, EndV = 1 });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = wiA, RunId = Run, AreaId = Area, MapId = "CT1-L2", Status = "SKIPPED", Attempts = 2 });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = wiB, RunId = Run, AreaId = areaB, MapId = "CT1-L2", Status = "PENDING", Attempts = 1 });

        var t0 = new DateTimeOffset(2026, 8, 19, 5, 10, 0, TimeSpan.Zero);
        OrderActionEntity Act(Guid task, Guid wi, string status, int minute, string? desc = null) => new()
        {
            ActionId = Guid.NewGuid(), MissionId = mission, WorkItemId = wi, TaskId = task,
            ActionType = "startWeldInspection", Status = status, CreatedAt = t0.AddMinutes(minute),
            Result = desc is null ? null : JsonSerializer.Serialize(new { ActionStatus = status, ResultDescription = desc }),
        };
        // 영역 A 1차: TaskOk 성공·TaskFail 실패 → 재큐잉, 2차: 둘 다 재실행(TaskOk는 이번엔 실패) → 소진 SKIPPED
        db.OrderActions.AddRange(
            Act(TaskOk, wiA, "FINISHED", 0, "OK"), Act(TaskFail, wiA, "FAILED", 0, "vision timeout"),
            Act(TaskOk, wiA, "FAILED", 2, "drivingFailed"), Act(TaskFail, wiA, "FAILED", 2, "vision timeout #2"),
            Act(TaskRetrying, wiB, "FAILED", 3, "vision timeout"));
        db.InspectionResults.Add(new InspectionResultEntity
        { ResultId = Guid.NewGuid(), RunId = Run, MissionId = mission, TaskId = TaskFail, Status = "FAILED", OccurredAt = t0.AddMinutes(2).AddSeconds(30) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListRuns_FiltersAndJoinsScenario()
    {
        using var sp = BuildServices();
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRunAsync(db);
        var svc = new RunQueryService(db);

        var running = await svc.ListRunsAsync("RUNNING", "CT1", 50);
        var r = Assert.Single(running);
        Assert.Equal(("CT1 검사", "CT1", "AMR01"), (r.ScenarioName, r.TankId, r.RobotId));
        Assert.Equal(TimeSpan.Zero, r.StartedAt!.Value.Offset);   // UTC 환산

        Assert.Empty(await svc.ListRunsAsync("COMPLETED", null, 50));   // 없으면 빈 배열(200)
        Assert.Empty(await svc.ListRunsAsync(null, "CT9", 50));
    }

    [Fact]
    public async Task RunDetail_HasTankAndMissionLevel_SerializedAsSpec()
    {
        using var sp = BuildServices();
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRunAsync(db);

        var detail = await new RunQueryService(db).GetRunAsync(Run);
        Assert.NotNull(detail);
        Assert.Equal("CT1", detail!.TankId);
        var m = Assert.Single(detail.Missions);
        Assert.Equal((2, "CT1-L2", "Running"), (m.Level, m.MapId, m.State));

        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"startedAt\":\"2026-08-19T05:00:00Z\"", json);   // ISO 8601 UTC "Z" [§5.1]
        Assert.Contains("\"endedAt\":null", json);
        Assert.Null(await new RunQueryService(db).GetRunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Results_OnePerTask_FinalOutcome_MmUnits()
    {
        using var sp = BuildServices();
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRunAsync(db);
        var svc = new RunQueryService(db);

        var res = await svc.GetResultsAsync(Run, null, 100, 0);
        Assert.NotNull(res);
        Assert.Equal(2, res!.Total);   // TaskRetrying은 재시도 여유(미종결) → 제외, 나머지 taskId당 1건

        var ok = res.Items.Single(i => i.TaskId == TaskOk);
        Assert.Equal("SUCCESS", ok.Status);   // 성공이 하나라도 있으면 성공 — 이후 재실행 실패로 뒤집히지 않음
        Assert.Equal("OK", ok.Description);

        var fail = res.Items.Single(i => i.TaskId == TaskFail);
        Assert.Equal(("FAILED", 2, "vision timeout #2"), (fail.Status, fail.Attempts, fail.Description));
        Assert.Equal((3, "PM", 2, "PM-L2-01"), (fail.WallId, fail.WallCode, fail.Level, fail.AreaName));
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 5, 12, 30, TimeSpan.Zero), fail.OccurredAt);
        Assert.Equal(new UvMm(12500, 4200), fail.Position!.SeamStart);
        Assert.Equal(new UvMm(17800, 4200), fail.Position.SeamEnd);
        Assert.Equal((5300, "LINE", 3), (fail.Position.SeamLength, fail.Position.SeamType, fail.Position.WallId));

        var failedOnly = await svc.GetResultsAsync(Run, "FAILED", 100, 0);
        Assert.Equal(TaskFail, Assert.Single(failedOnly!.Items).TaskId);
        var page = await svc.GetResultsAsync(Run, null, 1, 1);
        Assert.Equal((2, 1), (page!.Total, page.Items.Count));   // total은 필터 후 전체, items는 페이지

        Assert.Null(await svc.GetResultsAsync(Guid.NewGuid(), null, 100, 0));   // 없는 run → 404
    }

    // ── §9 로봇 상태 전송 ─────────────────────────────────────────────

    private static SaigeHealthReporter Reporter(ServiceProvider sp, RobotErrorTracker? errors = null) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHttpClientFactory>(),
            errors ?? new RobotErrorTracker(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acs:Saige:Enabled"] = "true", ["Acs:Saige:AlarmAfterFailures"] = "2", ["Acs:Saige:InternalErrorRetries"] = "1",
            }).Build(),
            NullLogger<SaigeHealthReporter>.Instance);

    private static async Task SeedRobotAsync(AcsDbContext db, bool calibrated = true, string conn = "ONLINE")
    {
        db.Robots.Add(new RobotEntity { RobotId = "AMR01", Name = "AMR01", Manufacturer = "HD", SerialNumber = "AMR-01" });
        db.Maps.Add(new MapEntity { MapId = "CT1-L2", TankId = "CT1", Level = 2, Version = 3 });
        // 맵 = 도면을 90° 회전 + (10, 20) 이동. 구버전(v2) 캘리브레이션만 있으면 무효.
        db.MapCalibrations.Add(new MapCalibrationEntity
        { MapId = "CT1-L2", MapVersion = calibrated ? 3 : 2, Tx = 10, Ty = 20, YawRad = Math.PI / 2, PointCount = 3 });
        db.RobotContexts.Add(new RobotContextEntity
        {
            RobotId = "AMR01", ReportedMapId = "CT1-L2", ConnectionState = conn, BatteryPct = 82.4,
            ReportedX = 10 - 5.12, ReportedY = 20 + 12.48,   // 도면 (12.48, 5.12)
            ReportedTheta = Math.PI,                          // 도면 yaw 90°
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Health_SendsSpecPayload_DrawingMm()
    {
        var saige = new FakeSaige(HttpStatusCode.OK);
        using var sp = BuildServices(saige);
        await SeedRobotAsync(sp.GetRequiredService<AcsDbContext>());

        await Reporter(sp).TickAsync(default);

        var (path, body) = Assert.Single(saige.Requests);
        Assert.Equal("/agent/v3/external/robot-health-check", path);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal("AMR01", j.GetProperty("robotId").GetString());
        Assert.Equal("IDLE", j.GetProperty("status").GetString());
        var pos = j.GetProperty("position");
        Assert.Equal((2, 12480, 5120, 90.0), (pos.GetProperty("level").GetInt32(), pos.GetProperty("x").GetInt32(),
            pos.GetProperty("y").GetInt32(), pos.GetProperty("yaw").GetDouble()));
        Assert.Equal((82, -1), (j.GetProperty("battery").GetInt32(), j.GetProperty("rssi").GetInt32()));
        Assert.True(j.GetProperty("timestamp").GetInt64() > 1_700_000_000_000);   // unix epoch ms
    }

    [Fact]
    public async Task Health_StatusReflectsConnectionErrorsAndWork()
    {
        using var sp = BuildServices();
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRobotAsync(db);
        await SeedRunAsync(db);
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = Guid.NewGuid(), RunId = Run, AreaId = Area, MapId = "CT1-L2", Status = "DISPATCHED" });
        await db.SaveChangesAsync();

        var errors = new RobotErrorTracker();
        var reporter = Reporter(sp, errors);
        Assert.Equal("WORKING", (await reporter.BuildSnapshotAsync(db, "AMR01", default))!.Status);

        errors.Update("AMR01", new[] { "equipmentError" });
        Assert.Equal("ERROR", (await reporter.BuildSnapshotAsync(db, "AMR01", default))!.Status);

        (await db.RobotContexts.FirstAsync()).ConnectionState = "CONNECTIONBROKEN";
        await db.SaveChangesAsync();
        Assert.Equal("DISCONNECTED", (await reporter.BuildSnapshotAsync(db, "AMR01", default))!.Status);   // 두절 최우선
    }

    /// <summary>유효 T_W_D 없는 층 — 원시 SLAM 좌표를 도면 좌표로 보내지 않고 전송 자체를 보류한다.</summary>
    [Fact]
    public async Task Health_NoValidCalibration_HoldsTransmission()
    {
        var saige = new FakeSaige(HttpStatusCode.OK);
        using var sp = BuildServices(saige);
        await SeedRobotAsync(sp.GetRequiredService<AcsDbContext>(), calibrated: false);

        await Reporter(sp).TickAsync(default);

        Assert.Empty(saige.Requests);
    }

    [Fact]
    public async Task Health_503_BacksOff_ThenAlarmsOnce_AndRecovers()
    {
        var saige = new FakeSaige(HttpStatusCode.ServiceUnavailable, "{\"code\":50302}");
        using var sp = BuildServices(saige);
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRobotAsync(db);
        var reporter = Reporter(sp);

        await reporter.TickAsync(default);
        Assert.True(reporter.InBackoff);
        Assert.Empty(await db.Alarms.AsNoTracking().ToListAsync());          // 임계(2회) 미만
        await reporter.TickAsync(default);
        await reporter.TickAsync(default);
        var alarm = Assert.Single(await db.Alarms.AsNoTracking().ToListAsync());   // 임계 초과 후에도 1회만
        Assert.Equal("SAIGE_UNREACHABLE", alarm.AlarmCode);

        saige.Status = HttpStatusCode.OK;
        await reporter.TickAsync(default);
        Assert.False(reporter.InBackoff);
        Assert.Equal(0, reporter.ConsecutiveFailures);
    }

    [Fact]
    public async Task Health_400_NoRetry_Alarm_500_LimitedRetry()
    {
        var saige = new FakeSaige(HttpStatusCode.BadRequest, "{\"code\":40001}");
        using var sp = BuildServices(saige);
        var db = sp.GetRequiredService<AcsDbContext>();
        await SeedRobotAsync(db);
        var reporter = Reporter(sp);

        await reporter.TickAsync(default);
        Assert.Single(saige.Requests);                                        // 재시도하지 않고 폐기
        Assert.False(reporter.InBackoff);
        Assert.Equal("SAIGE_BAD_REQUEST", Assert.Single(await db.Alarms.AsNoTracking().ToListAsync()).AlarmCode);

        saige.Requests.Clear();
        saige.Status = HttpStatusCode.InternalServerError;
        await reporter.TickAsync(default);
        Assert.Equal(2, saige.Requests.Count);                                // 최초 1 + 제한적 재시도 1
        Assert.False(reporter.InBackoff);
    }

    private sealed class FakeSaige(HttpStatusCode status, string body = "{\"status\":200,\"message\":\"success\"}") : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public List<(string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
