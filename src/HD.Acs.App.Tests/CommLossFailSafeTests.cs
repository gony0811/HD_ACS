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
/// HD_AMR 통신 단절 Fail-Safe(rev.0) — ACS 측 반응 가드 [docs/HD_AMR_통신단절_FailSafe_사양.md §6].
///  · communicationLostPause → COMM_LOSS_PAUSED 알람 1회(PAUSE→HOLD 설명 변경에도 중복 없음), 실패 집계 금지
///  · resumeRejected → RESUME_REJECTED 알람(사유 detail)
///  · CommLossService: 재개는 일시정지 보고 중일 때만, run 중단 시 일시정지면 cancelOrder 시도
/// </summary>
public class CommLossFailSafeTests
{
    private const string RobotId = "AMR-01";
    private static readonly RobotRef Robot = new(RobotId, "HD", "AMR-01");

    private sealed record Env(AcsDbContext Db, RobotStateService States, CommLossService CommLoss, RobotErrorTracker Tracker);

    private static Env Build()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = RobotId, Manufacturer = "HD", SerialNumber = "AMR-01", Name = RobotId, VdaVersion = "2.0", IsActive = true });
        db.AlarmSpecs.AddRange(
            new AlarmSpecEntity { AlarmCode = "COMM_LOSS_PAUSED", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "RESUME_REJECTED", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "ORDER_REJECTED", Severity = "WARNING", Title = "" },
            new AlarmSpecEntity { AlarmCode = "ROBOT_NOT_CONNECTED", Severity = "WARNING", Title = "" });
        db.SaveChanges();

        var tracker = new RobotErrorTracker();
        var vda = new Vda5050MasterClient("localhost");   // 미연결 — 발행은 실패(테스트는 가드·분기 중심)
        var config = new ConfigurationBuilder().Build();
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub, config, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var progress = new ProgressService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var states = new RobotStateService(db, hub, dispatcher, progress, tracker, missions, NullLogger<RobotStateService>.Instance);
        var commLoss = new CommLossService(db, vda, dispatcher, missions, tracker, NullLogger<CommLossService>.Instance);
        return new Env(db, states, commLoss, tracker);
    }

    private static Vda5050State PausedState(string description, params VdaError[] extra)
    {
        var s = new Vda5050State
        {
            Paused = true,
            Errors = { new VdaError { ErrorType = "communicationLostPause", ErrorLevel = "WARNING", ErrorDescription = description } },
        };
        s.Errors.AddRange(extra);
        return s;
    }

    private static async Task<WorkItemEntity> SeedDispatchedRunAsync(AcsDbContext db)
    {
        var runId = Guid.NewGuid();
        var orderId = Guid.NewGuid().ToString();
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = RobotId, State = "RUNNING" });
        db.Missions.Add(new MissionEntity { MissionId = Guid.NewGuid(), RunId = runId, MapId = "CT1-L1", RobotId = RobotId, OrderId = orderId, State = "Running" });
        var wi = new WorkItemEntity
        { WorkItemId = Guid.NewGuid(), RunId = runId, AreaId = Guid.NewGuid(), MapId = "CT1-L1", Status = "DISPATCHED", OrderId = orderId, Actions = "[]" };
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi;
    }

    [Fact]
    public async Task CommLossPause_RaisesAlarmOnce_AcrossPauseAndHold_WithoutFailureAggregation()
    {
        var env = Build();
        var wi = await SeedDispatchedRunAsync(env.Db);

        // state.orderId 미매칭 — EF InMemory는 ExecuteUpdate 미지원이라 mission 대조 분기는 건너뛰고 errors[] 분기만 평가.
        // (PAUSED 액션이 정차를 종결시키지 않는 것은 종결 판정이 FINISHED/FAILED만 세는 구조로 보장된다.)
        await env.States.HandleStateAsync(Robot, PausedState("PAUSE t0=10:00:00 조건=A"));
        await env.States.HandleStateAsync(Robot, PausedState("HOLD t0=10:00:00 경과 60s 조건=A"));

        var alarms = await env.Db.Alarms.AsNoTracking().ToListAsync();
        Assert.Single(alarms);
        Assert.Equal("COMM_LOSS_PAUSED", alarms[0].AlarmCode);
        Assert.Contains("PAUSE", alarms[0].Detail);

        // 사양서 §6: 실패로 집계하지 않는다 — 재시도·재배차·스킵 없음, run도 그대로.
        var reload = await env.Db.WorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(("DISPATCHED", 0), (reload.Status, reload.Attempts));
        Assert.Equal("RUNNING", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
        Assert.DoesNotContain(alarms, a => a.AlarmCode == "ORDER_REJECTED");
        Assert.True(env.Tracker.HasActiveError(RobotId, "communicationLostPause"));
    }

    [Fact]
    public async Task ResumeRejected_RaisesAlarmWithReason()
    {
        var env = Build();
        await env.States.HandleStateAsync(Robot, PausedState("PAUSE",
            new VdaError { ErrorType = "resumeRejected", ErrorLevel = "WARNING", ErrorDescription = "비상정지 해제 필요" }));

        var codes = await env.Db.Alarms.AsNoTracking().Select(a => a.AlarmCode).ToListAsync();
        Assert.Contains("COMM_LOSS_PAUSED", codes);
        var rej = await env.Db.Alarms.AsNoTracking().SingleAsync(a => a.AlarmCode == "RESUME_REJECTED");
        // detail은 JSON 직렬화(한글 \uXXXX 이스케이프) — 파싱 후 비교
        var desc = System.Text.Json.JsonDocument.Parse(rej.Detail!).RootElement.GetProperty("ErrorDescription").GetString();
        Assert.Equal("비상정지 해제 필요", desc);
    }

    [Fact]
    public async Task Tracker_ClearsPause_WhenErrorDisappears()
    {
        var env = Build();
        await env.States.HandleStateAsync(Robot, PausedState("PAUSE"));
        Assert.True(env.Tracker.HasActiveError(RobotId, "CommunicationLostPause"));   // 대소문자 무시
        await env.States.HandleStateAsync(Robot, new Vda5050State());
        Assert.False(env.Tracker.HasActiveError(RobotId, "communicationLostPause"));
    }

    [Fact]
    public async Task ResumePause_Guards_UnknownRobot404_NotPaused409_NotConnected409()
    {
        var env = Build();
        var e404 = await Assert.ThrowsAsync<CommLossException>(() =>
            env.CommLoss.ResumePauseAsync("NOPE", new(null), default));
        Assert.Equal(404, e404.StatusCode);

        // 일시정지 보고 없음 → 오조작 방지 409
        var e409 = await Assert.ThrowsAsync<CommLossException>(() =>
            env.CommLoss.ResumePauseAsync(RobotId, new(null), default));
        Assert.Equal(409, e409.StatusCode);

        // 일시정지 보고 중이지만 연결 정보 없음 → 명령 차단(RobotNotConnected)
        await env.States.HandleStateAsync(Robot, PausedState("PAUSE"));
        await Assert.ThrowsAsync<RobotNotConnectedException>(() =>
            env.CommLoss.ResumePauseAsync(RobotId, new("op"), default));
        Assert.DoesNotContain(await env.Db.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "COMM_LOSS_RESUME");
    }

    [Fact]
    public async Task Abort_NotPaused_DoesNotTryCancelOrder()
    {
        var env = Build();
        var wi = await SeedDispatchedRunAsync(env.Db);
        var r = await env.CommLoss.AbortRunAsync(wi.RunId, default);
        Assert.Equal("ABORTED", r.State);
        Assert.False(r.CancelOrderSent);
        Assert.Equal("ABORTED", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Abort_WhilePaused_AbortsRun_EvenIfCancelOrderPublishFails()
    {
        var env = Build();
        var wi = await SeedDispatchedRunAsync(env.Db);
        await env.States.HandleStateAsync(Robot, PausedState("PAUSE"));

        // MQTT 미연결이라 cancelOrder 발행은 실패 — 중단 자체는 확정되고 cancelOrderSent=false로 알린다.
        var r = await env.CommLoss.AbortRunAsync(wi.RunId, default);
        Assert.Equal("ABORTED", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
        Assert.False(r.CancelOrderSent);
    }

    [Fact]
    public void AlarmSpecSeed_IncludesCommLossCodes()
    {
        var specs = typeof(AlarmSpecSeed).GetField("Specs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var codes = ((System.Collections.IEnumerable)specs).Cast<object>()
            .Select(o => ((System.Runtime.CompilerServices.ITuple)o)[0] as string).ToList();
        Assert.Contains("COMM_LOSS_PAUSED", codes);
        Assert.Contains("RESUME_REJECTED", codes);
    }

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
