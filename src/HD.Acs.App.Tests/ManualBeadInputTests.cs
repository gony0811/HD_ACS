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
/// 수동 비드 입력 요청(manualBeadInputRequired, WARNING) [VDA §6.4] — AMR이 팝업을 띄운 동안만 errors[]에 싣는다.
///  · 등장 → MANUAL_BEAD_INPUT_REQUIRED 알람 1건(반복 state에 중복 없음), 실패 집계·배차 변화 없음
///  · 사라짐 → 알람 해제 기록
///  · 오류 상태 판정(SAIGE ERROR)에는 세지 않는다
/// </summary>
public class ManualBeadInputTests
{
    private const string RobotId = "AMR-01";
    private static readonly RobotRef Robot = new(RobotId, "HD", "AMR-01");

    private sealed record Env(AcsDbContext Db, RobotStateService States, RobotErrorTracker Tracker);

    private static Env Build()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = RobotId, Manufacturer = "HD", SerialNumber = "AMR-01", Name = RobotId, VdaVersion = "2.0", IsActive = true });
        db.AlarmSpecs.AddRange(
            new AlarmSpecEntity { AlarmCode = "MANUAL_BEAD_INPUT_REQUIRED", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "COBOT_ZONE_OBSTACLE", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "EQUIPMENT_ERROR", Severity = "WARNING", Title = "" });
        db.SaveChanges();

        var tracker = new RobotErrorTracker();
        var vda = new Vda5050MasterClient("localhost");
        var config = new ConfigurationBuilder().Build();
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub, config, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var progress = new ProgressService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var states = new RobotStateService(db, hub, dispatcher, progress, tracker, missions, NullLogger<RobotStateService>.Instance);
        return new Env(db, states, tracker);
    }

    private static Vda5050State Waiting(string description = "Bead1 찾기: 수동 비드 입력 대기 — 자동 검출 신뢰도 낮음", params VdaError[] extra)
    {
        var s = new Vda5050State
        {
            Errors = { new VdaError { ErrorType = "manualBeadInputRequired", ErrorLevel = "WARNING", ErrorDescription = description } },
        };
        s.Errors.AddRange(extra);
        return s;
    }

    private static async Task<WorkItemEntity> SeedDispatchedRunAsync(AcsDbContext db)
    {
        var runId = Guid.NewGuid();
        var orderId = Guid.NewGuid().ToString();
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = RobotId, State = "RUNNING" });
        db.Missions.Add(new MissionEntity { MissionId = Guid.NewGuid(), RunId = runId, MapId = "CT1-L4", RobotId = RobotId, OrderId = orderId, State = "Running" });
        var wi = new WorkItemEntity
        { WorkItemId = Guid.NewGuid(), RunId = runId, AreaId = Guid.NewGuid(), MapId = "CT1-L4", Status = "DISPATCHED", OrderId = orderId, Actions = "[]" };
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi;
    }

    [Fact]
    public async Task 팝업이_뜨면_경고_알람_1건_반복_보고에도_중복_없고_작업은_그대로()
    {
        var env = Build();
        await SeedDispatchedRunAsync(env.Db);

        await env.States.HandleStateAsync(Robot, Waiting());
        await env.States.HandleStateAsync(Robot, Waiting());   // 2초 뒤 같은 보고
        await env.States.HandleStateAsync(Robot, Waiting());

        var alarm = await env.Db.Alarms.AsNoTracking().SingleAsync();
        Assert.Equal("MANUAL_BEAD_INPUT_REQUIRED", alarm.AlarmCode);
        Assert.Null(alarm.ClearedAt);
        Assert.Contains("Bead1 찾기", DescriptionOf(alarm));
        Assert.Contains("WARNING", alarm.Detail);

        // 실패가 아니다 — 정차·run은 그대로, 재시도·건너뜀 없음.
        var wi = await env.Db.WorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(("DISPATCHED", 0), (wi.Status, wi.Attempts));
        Assert.Equal("RUNNING", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task 팝업이_닫히면_알람이_해제되고_다시_뜨면_새_알람()
    {
        var env = Build();

        await env.States.HandleStateAsync(Robot, Waiting());
        await env.States.HandleStateAsync(Robot, new Vda5050State());   // 입력 완료 — errors 비었음

        var first = await env.Db.Alarms.AsNoTracking().SingleAsync();
        Assert.NotNull(first.ClearedAt);
        Assert.Equal("AMR", first.ClearedBy);

        await env.States.HandleStateAsync(Robot, Waiting("Bead2 찾기: 수동 비드 입력 대기 — 확인 요청(매번)"));

        var all = await env.Db.Alarms.AsNoTracking().OrderBy(a => a.RaisedAt).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Null(all[1].ClearedAt);
        Assert.Contains("Bead2 찾기", DescriptionOf(all[1]));
    }

    /// <summary>알람 detail(JSON — 한글은 \u 이스케이프로 저장됨)에서 AMR 설명을 꺼낸다.</summary>
    private static string DescriptionOf(AlarmEntity alarm) =>
        System.Text.Json.JsonDocument.Parse(alarm.Detail!).RootElement.GetProperty("ErrorDescription").GetString()!;

    [Fact]
    public async Task 입력_요청은_오류_상태로_세지_않지만_실제_오류와_함께면_오류다()
    {
        var env = Build();

        await env.States.HandleStateAsync(Robot, Waiting());
        Assert.True(env.Tracker.HasActiveError(RobotId, RobotErrorTracker.ManualBeadInputType));
        Assert.False(env.Tracker.HasActiveErrors(RobotId));   // SAIGE 로봇 상태가 ERROR로 가지 않는다

        await env.States.HandleStateAsync(Robot, Waiting(extra:
            new VdaError { ErrorType = "equipmentError", ErrorLevel = "WARNING", ErrorDescription = "x" }));
        Assert.True(env.Tracker.HasActiveErrors(RobotId));

        await env.States.HandleStateAsync(Robot, new Vda5050State());
        Assert.False(env.Tracker.HasActiveErrors(RobotId));
    }

    /// <summary>
    /// 코봇 반경 장애물(cobotZoneObstacle, WARNING) [VDA §6.4] — AMR이 주행 중 장애물을 보고 선 동안만 싣는다.
    /// 알람 1건·사라지면 해제, 정차·run·오류 상태 판정은 그대로(AMR이 기다렸다가 다시 출발한다).
    /// </summary>
    [Fact]
    public async Task 코봇_반경_장애물은_경고_알람만_남기고_사라지면_해제된다()
    {
        var env = Build();
        await SeedDispatchedRunAsync(env.Db);
        Vda5050State Held() => new()
        {
            Errors = { new VdaError { ErrorType = "cobotZoneObstacle", ErrorLevel = "WARNING",
                ErrorDescription = "코봇 반경 장애물 감지 — 주행 정지: 전방 0.62m (방향 +3°, 점 5개)" } },
        };

        await env.States.HandleStateAsync(Robot, Held());
        await env.States.HandleStateAsync(Robot, Held());   // 반복 보고 — 중복 없음

        var alarm = await env.Db.Alarms.AsNoTracking().SingleAsync();
        Assert.Equal("COBOT_ZONE_OBSTACLE", alarm.AlarmCode);
        Assert.Null(alarm.ClearedAt);
        Assert.Contains("전방 0.62m", DescriptionOf(alarm));
        Assert.False(env.Tracker.HasActiveErrors(RobotId));   // 고장이 아니다 — SAIGE 로봇 상태가 ERROR로 가지 않는다
        var wi = await env.Db.WorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(("DISPATCHED", 0), (wi.Status, wi.Attempts));
        Assert.Equal("RUNNING", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);

        await env.States.HandleStateAsync(Robot, new Vda5050State());   // 장애물이 비켜나 다시 출발

        var cleared = await env.Db.Alarms.AsNoTracking().SingleAsync();
        Assert.NotNull(cleared.ClearedAt);
        Assert.Equal("AMR", cleared.ClearedBy);
        Assert.True(RobotErrorTracker.IsObstacleHold("COBOTZONEOBSTACLE"));
        Assert.False(RobotErrorTracker.IsOperatorNotice("cobotZoneObstacle"));
    }

    [Theory]
    [InlineData("manualBeadInputRequired", true)]
    [InlineData("MANUALBEADINPUTREQUIRED", true)]
    [InlineData("equipmentError", false)]
    [InlineData("cobotLinkLost", false)]
    public void 알림성_유형_판정(string type, bool expected)
        => Assert.Equal(expected, RobotErrorTracker.IsOperatorNotice(type));

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
