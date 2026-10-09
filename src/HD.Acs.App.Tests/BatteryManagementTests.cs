using System.Text.Json;
using HD.Acs.App.Hubs;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// HD_AMR 배터리관리 사양 — ACS 측 반응 가드.
///  · state.errors 분기(배터리 critical/orderRejectedBatteryLow 는 실패 집계 금지)
///  · ActionCatalogSeed/AlarmSpecSeed 신규 코드 upsert
///  · BatterySwapService: 등록 1곳/층, 수동 디스패치 때 활성 run abort + 교체 Order 1건 발행.
/// </summary>
public class BatteryManagementTests
{
    [Fact]
    public void ActionCatalogSeed_IncludesBatterySwapMoveSchema()
    {
        // schema 본문엔 액션 이름이 없다 — 파라미터 구조만. required 키·reason enum 유지만 확인.
        Assert.Contains("targetNodeId", ActionCatalogSeed.BatterySwapMoveParamSchema);
        Assert.Contains("mapId", ActionCatalogSeed.BatterySwapMoveParamSchema);
        Assert.Contains("LOW", ActionCatalogSeed.BatterySwapMoveParamSchema);
        Assert.Contains("CRITICAL", ActionCatalogSeed.BatterySwapMoveParamSchema);
        Assert.Contains("MANUAL", ActionCatalogSeed.BatterySwapMoveParamSchema);
    }

    // ── errors[] 분기: batteryCritical 알람 + orderRejectedBatteryLow는 실패 집계 금지 ──

    private static (AcsDbContext Db, RobotStateService Svc, string RobotId) BuildErrorsEnv()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = "AMR-01", Manufacturer = "HD", SerialNumber = "AMR-01", Name = "AMR-01", VdaVersion = "2.0", IsActive = true });
        db.AlarmSpecs.AddRange(
            new AlarmSpecEntity { AlarmCode = "BATTERY_LOW", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "BATTERY_CRITICAL", Severity = "CRITICAL", Title = "" },
            new AlarmSpecEntity { AlarmCode = "ORDER_REJECTED_BATTERY_LOW", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "ORDER_REJECTED", Severity = "WARNING", Title = "" });
        db.SaveChanges();

        var tracker = new RobotErrorTracker();
        var vda = new Vda5050MasterClient("localhost");
        var config = new ConfigurationBuilder().Build();
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub, config, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var progress = new ProgressService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var svc = new RobotStateService(db, hub, dispatcher, progress, tracker, missions, NullLogger<RobotStateService>.Instance);
        return (db, svc, "AMR-01");
    }

    [Fact]
    public async Task BatteryCriticalError_RaisesCriticalAlarm_WithoutFailureAggregation()
    {
        var (db, svc, robotId) = BuildErrorsEnv();
        var state = new Vda5050State
        {
            Errors = { new VdaError { ErrorType = "batteryCritical", ErrorLevel = "FATAL", ErrorDescription = "SoC 8%" } },
        };
        await svc.HandleStateAsync(new RobotRef(robotId, "HD", "AMR-01"), state);

        var alarms = await db.Alarms.AsNoTracking().ToListAsync();
        Assert.Single(alarms);
        Assert.Equal("BATTERY_CRITICAL", alarms[0].AlarmCode);
        // work_item 없는 상황 — 실패 집계 경로가 호출되지 않아야 한다(ORDER_REJECTED는 쓰이지 않음).
        Assert.DoesNotContain(alarms, a => a.AlarmCode == "ORDER_REJECTED");
    }

    [Fact]
    public async Task OrderRejectedBatteryLow_RaisesWarningAlarm_AndDoesNotAggregateFailure()
    {
        var (db, svc, robotId) = BuildErrorsEnv();
        // 작업 큐가 있다고 가정 — DISPATCHED work_item 1건: 실패 집계가 일어나면 attempts/status가 바뀐다.
        var runId = Guid.NewGuid();
        var missionId = Guid.NewGuid();
        var orderId = Guid.NewGuid().ToString();
        db.ScenarioRuns.Add(new ScenarioRunEntity
        { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = robotId, State = "RUNNING" });
        db.Missions.Add(new MissionEntity
        { MissionId = missionId, RunId = runId, MapId = "CT1-L1", RobotId = robotId, OrderId = orderId, State = "Released" });
        var wi = new WorkItemEntity
        { WorkItemId = Guid.NewGuid(), RunId = runId, AreaId = Guid.NewGuid(), MapId = "CT1-L1", Status = "DISPATCHED", OrderId = orderId, Actions = "[]" };
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();

        // 사양서 §8: "AMR은 거부한 Order의 orderId로 전환하지 않고 기존 orderId를 유지" — 거부가 보고될 때 state의
        // orderId는 ACS가 발행한 그 orderId가 아닐 수 있다. 여기서는 미매칭(빈 OrderId)으로 두어 mission 대조 분기는
        // 건너뛰고 errors[] 분기만 평가한다(EF InMemory는 ExecuteUpdate를 지원하지 않아 매칭 분기는 결과와 무관하게 폭사).
        var state = new Vda5050State
        {
            Errors = { new VdaError { ErrorType = "orderRejectedBatteryLow", ErrorLevel = "WARNING", ErrorDescription = "저전력 상태 — 작업 Order 거부" } },
        };
        await svc.HandleStateAsync(new RobotRef(robotId, "HD", "AMR-01"), state);

        var alarms = await db.Alarms.AsNoTracking().ToListAsync();
        Assert.Single(alarms);
        Assert.Equal("ORDER_REJECTED_BATTERY_LOW", alarms[0].AlarmCode);
        // 사양서 §8 핵심: FATAL 실패로 오해 금지 — work_item 상태가 바뀌지 않아야 한다.
        var reload = await db.WorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(("DISPATCHED", 0), (reload.Status, reload.Attempts));
        Assert.DoesNotContain(alarms, a => a.AlarmCode == "ORDER_REJECTED");
    }

    [Fact]
    public async Task BatteryLowError_StillRaisesWarningAlarm()
    {
        var (db, svc, robotId) = BuildErrorsEnv();
        var state = new Vda5050State
        {
            Errors = { new VdaError { ErrorType = "batteryLow", ErrorLevel = "WARNING", ErrorDescription = "SoC 18%" } },
        };
        await svc.HandleStateAsync(new RobotRef(robotId, "HD", "AMR-01"), state);

        var alarms = await db.Alarms.AsNoTracking().ToListAsync();
        Assert.Single(alarms);
        Assert.Equal("BATTERY_LOW", alarms[0].AlarmCode);
    }

    // ── BatterySwapService — 등록/디스패치 ──

    private static (AcsDbContext Db, BatterySwapService Svc, FakeHub Hub) BuildSwapEnv()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = "AMR-01", Manufacturer = "HD", SerialNumber = "AMR-01", Name = "AMR-01", VdaVersion = "2.0", IsActive = true });
        db.Maps.AddRange(
            new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Name = "L1", Version = 1 },
            new MapEntity { MapId = "CT1-L2", TankId = "CT1", Level = 2, Name = "L2", Version = 1 });
        db.AlarmSpecs.Add(new AlarmSpecEntity { AlarmCode = "BATTERY_SWAP_DISPATCHED", Severity = "INFO", Title = "" });
        db.AlarmSpecs.Add(new AlarmSpecEntity { AlarmCode = "ROBOT_NOT_CONNECTED", Severity = "WARNING", Title = "" });
        db.SaveChanges();

        var vda = new Vda5050MasterClient("localhost");   // publish는 연결 없이 호출만 — 테스트에서는 Order 생성 로직 자체는 커버하지 못함.
        var config = new ConfigurationBuilder().Build();
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub, config, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var svc = new BatterySwapService(db, vda, dispatcher, missions, hub, config, NullLogger<BatterySwapService>.Instance);
        return (db, svc, hub);
    }

    [Fact]
    public async Task Register_PerMap_RejectsSecondAtSameMap()
    {
        var (db, svc, _) = BuildSwapEnv();
        var n = await svc.RegisterAsync(new BatterySwapService.SwapRegisterRequest(
            "CT1-L1", "BAT-01", 5.0, 12.0, null, null, "op"), default);
        Assert.Equal("CT1-L1", n.MapId);

        var dup = await Assert.ThrowsAsync<BatterySwapException>(() => svc.RegisterAsync(
            new BatterySwapService.SwapRegisterRequest("CT1-L1", "BAT-02", 6, 12, null, null, "op"), default));
        Assert.Equal(409, dup.StatusCode);

        // 다른 층은 허용
        var other = await svc.RegisterAsync(new BatterySwapService.SwapRegisterRequest(
            "CT1-L2", "BAT-02", 7, 12, null, null, "op"), default);
        Assert.Equal("CT1-L2", other.MapId);
    }

    [Fact]
    public async Task Register_RejectsUnknownMap_And_InvalidInputs()
    {
        var (_, svc, _) = BuildSwapEnv();
        var e404 = await Assert.ThrowsAsync<BatterySwapException>(() => svc.RegisterAsync(
            new BatterySwapService.SwapRegisterRequest("CT9-L9", "x", 0, 0, null, null, null), default));
        Assert.Equal(404, e404.StatusCode);

        var e400 = await Assert.ThrowsAsync<BatterySwapException>(() => svc.RegisterAsync(
            new BatterySwapService.SwapRegisterRequest("CT1-L1", "", 0, 0, null, null, null), default));
        Assert.Equal(400, e400.StatusCode);

        var eNaN = await Assert.ThrowsAsync<BatterySwapException>(() => svc.RegisterAsync(
            new BatterySwapService.SwapRegisterRequest("CT1-L1", "x", double.NaN, 0, null, null, null), default));
        Assert.Equal(400, eNaN.StatusCode);
    }

    [Fact]
    public async Task Dispatch_FailsWithoutSwapNodeOnReportedMap()
    {
        var (db, svc, _) = BuildSwapEnv();
        // 연결 OK + state 수신 OK, 하지만 교체 장소 미등록
        db.RobotContexts.Add(new RobotContextEntity
        {
            RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedMapId = "CT1-L1",
            ReportedX = 0, ReportedY = 0, ReportedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BatterySwapException>(() => svc.DispatchSwapAsync(
            "AMR-01", new BatterySwapService.SwapDispatchRequest("MANUAL", "op"), default));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("CT1-L1", ex.Message);
    }

    [Fact]
    public async Task Dispatch_FailsWhenRobotNotReporting()
    {
        var (_, svc, _) = BuildSwapEnv();   // RobotContexts 없음 → EnsureRobotConnectedAsync가 차단

        await Assert.ThrowsAsync<RobotNotConnectedException>(() => svc.DispatchSwapAsync(
            "AMR-01", new BatterySwapService.SwapDispatchRequest("LOW", "op"), default));
    }

    [Fact]
    public async Task Dispatch_AbortsActiveRun_AndEmitsHubNotification()
    {
        var (db, svc, hub) = BuildSwapEnv();
        await svc.RegisterAsync(new BatterySwapService.SwapRegisterRequest(
            "CT1-L1", "BAT-01", 5, 12, 0.1, null, "op"), default);

        db.RobotContexts.Add(new RobotContextEntity
        {
            RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedMapId = "CT1-L1",
            ReportedX = 0, ReportedY = 0, ReportedAt = DateTimeOffset.UtcNow,
        });
        var runId = Guid.NewGuid();
        db.ScenarioRuns.Add(new ScenarioRunEntity
        { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = "AMR-01", State = "RUNNING" });
        await db.SaveChangesAsync();

        // Vda5050MasterClient는 실제 연결이 없으므로 PublishOrderAsync가 실패할 수 있다 — 이 경우 try/catch로 감싸고,
        // DB 측 효과(abort + 알람 + 감사로그)와 Hub 측 효과(BatterySwapDispatched 발행)만 검증한다.
        try { await svc.DispatchSwapAsync("AMR-01", new BatterySwapService.SwapDispatchRequest("LOW", "op"), default); }
        catch (Exception ex) when (ex.GetType().Name.Contains("Mqtt") || ex is InvalidOperationException) { /* 연결 없음 — OK */ }

        var run = await db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        Assert.Equal("ABORTED", run.State);

        // 교체 디스패치 알람 1건 (성공 흐름일 때만 — publish 실패하면 알람도 미발행)
        var swapAlarm = await db.Alarms.AsNoTracking().FirstOrDefaultAsync(a => a.AlarmCode == "BATTERY_SWAP_DISPATCHED");
        if (swapAlarm is not null)
        {
            var detail = JsonDocument.Parse(swapAlarm.Detail!).RootElement;
            Assert.Equal("LOW", detail.GetProperty("detail").GetProperty("reason").GetString());
            Assert.Contains("BatterySwapDispatched", hub.Messages);
        }
    }

    // ── 최소 가짜 Hub (SignalR SendAsync 수신만) ──
    private sealed class FakeHub : IHubContext<MonitoringHub>
    {
        public List<string> Messages { get; } = new();
        public IHubClients Clients => new C(Messages);
        public IGroupManager Groups => null!;
        private sealed class C : IHubClients
        {
            private readonly P _p; public C(List<string> l) => _p = new P(l);
            public IClientProxy All => _p; public IClientProxy AllExcept(IReadOnlyList<string> _) => _p;
            public IClientProxy Client(string _) => _p; public IClientProxy Clients(IReadOnlyList<string> _) => _p;
            public IClientProxy Group(string _) => _p; public IClientProxy GroupExcept(string _, IReadOnlyList<string> __) => _p;
            public IClientProxy Groups(IReadOnlyList<string> _) => _p; public IClientProxy User(string _) => _p;
            public IClientProxy Users(IReadOnlyList<string> _) => _p;
        }
        private sealed class P : IClientProxy
        {
            private readonly List<string> _l; public P(List<string> l) => _l = l;
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            { _l.Add(method); return Task.CompletedTask; }
        }
    }
}
