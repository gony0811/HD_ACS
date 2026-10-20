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
/// HD_AMR 장치 통신 HEAVY 알람(rev.0) — ACS 측 반응 가드 [docs/HD_AMR_장치통신_HEAVY알람_사양.md §5].
///  · *LinkLost → DEVICE_LINK_LOST 알람 edge 1회(Latched 설명 변경에도 중복 없음), 사라지면 해제 기록
///  · 알람 중 배차 보류(거부→재배차 연쇄 방지), 명령 409, 해제 시 배차 재개
///  · orderRejectedDeviceFault → 거부 정차 PENDING 복귀(사용자 결정 — 실패 아님)
/// </summary>
public class DeviceLinkLostTests
{
    private const string RobotId = "AMR-01";
    private const string MapId = "CT1-L1";
    private static readonly RobotRef Robot = new(RobotId, "HD", "AMR-01");

    private sealed record Env(AcsDbContext Db, RobotStateService States, InspectionDispatcher Dispatcher,
        MissionService Missions, RobotErrorTracker Tracker);

    private static Env Build()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = RobotId, Manufacturer = "HD", SerialNumber = "AMR-01", Name = RobotId, VdaVersion = "2.0", IsActive = true });
        db.SaveChanges();

        var tracker = new RobotErrorTracker();
        var vda = new Vda5050MasterClient("localhost");   // 미연결 — 발행 시도는 예외(배차가 "나갔는지"의 증거로 쓰지 않는다)
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub,
            new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance, tracker);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var progress = new ProgressService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var states = new RobotStateService(db, hub, dispatcher, progress, tracker, missions, NullLogger<RobotStateService>.Instance);
        return new Env(db, states, dispatcher, missions, tracker);
    }

    private static VdaError LinkLost(string type, string device, string desc) => new()
    {
        ErrorType = type, ErrorLevel = "FATAL", ErrorDescription = desc,
        ErrorReferences = new() { new ErrorReference { ReferenceKey = "device", ReferenceValue = device } },
    };

    private static Vda5050State State(params VdaError[] errors)
    {
        // orderId 미매칭 — EF InMemory는 ExecuteUpdate 미지원이라 mission 대조 분기는 건너뛴다(CommLossFailSafeTests와 동일)
        var s = new Vda5050State { OrderId = "unmatched", AgvPosition = new AgvPosition { MapId = MapId, X = 1, Y = 2 } };
        s.Errors.AddRange(errors);
        return s;
    }

    /// <summary>RUNNING run + 미션 1개 + 지정 상태 work_item들.</summary>
    private static async Task<(Guid RunId, MissionEntity Mission)> SeedRunAsync(AcsDbContext db, params WorkItemEntity[] items)
    {
        var runId = Guid.NewGuid();
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = RobotId, State = "RUNNING" });
        var mission = new MissionEntity { MissionId = Guid.NewGuid(), RunId = runId, MapId = MapId, RobotId = RobotId, State = "Running" };
        db.Missions.Add(mission);
        foreach (var w in items) { w.RunId = runId; w.MapId = MapId; db.WorkItems.Add(w); }
        await db.SaveChangesAsync();
        return (runId, mission);
    }

    private static WorkItemEntity Wi(string status, string? orderId = null) => new()
    { WorkItemId = Guid.NewGuid(), AreaId = Guid.NewGuid(), Status = status, OrderId = orderId, Actions = "[]" };

    [Fact]
    public async Task LinkLost_RaisesHeavyAlarmOnce_EvenWhenDescriptionChangesToLatched()
    {
        var env = Build();
        await env.States.HandleStateAsync(Robot, State(LinkLost("cobotLinkLost", "cobot", "코봇 통신 두절 t0=14:03:21 — 상태 소켓(20004) 끊김")));
        await env.States.HandleStateAsync(Robot, State(LinkLost("cobotLinkLost", "cobot", "코봇 통신 두절 t0=14:03:21 — 복구됨, RESET 대기")));

        var alarm = Assert.Single(await env.Db.Alarms.AsNoTracking().ToListAsync());
        Assert.Equal("DEVICE_LINK_LOST", alarm.AlarmCode);
        Assert.Null(alarm.ClearedAt);
        var d = JsonDocument.Parse(alarm.Detail!).RootElement;
        Assert.Equal("cobot", d.GetProperty("device").GetString());
        Assert.Equal("HEAVY", d.GetProperty("grade").GetString());
        Assert.Equal("CRITICAL", d.GetProperty("severity").GetString());
        Assert.True(env.Tracker.HasDeviceFault(RobotId));
    }

    [Fact]
    public async Task TwoDevices_OnlyResetDeviceIsCleared()
    {
        var env = Build();
        await env.States.HandleStateAsync(Robot, State(
            LinkLost("cobotLinkLost", "cobot", "코봇 통신 두절"),
            LinkLost("visionLinkLost", "vision", "검사 비전카메라 통신 두절")));
        // 코봇만 복구·RESET → errors에서 cobot만 사라짐
        await env.States.HandleStateAsync(Robot, State(LinkLost("visionLinkLost", "vision", "검사 비전카메라 통신 두절")));

        var alarms = await env.Db.Alarms.AsNoTracking().ToListAsync();
        Assert.Equal(2, alarms.Count);
        string Dev(AlarmEntity a) => JsonDocument.Parse(a.Detail!).RootElement.GetProperty("device").GetString()!;
        Assert.NotNull(alarms.Single(a => Dev(a) == "cobot").ClearedAt);
        Assert.Equal("AMR RESET", alarms.Single(a => Dev(a) == "cobot").ClearedBy);
        Assert.Null(alarms.Single(a => Dev(a) == "vision").ClearedAt);
        Assert.True(env.Tracker.HasDeviceFault(RobotId));
        Assert.Equal(new[] { "visionLinkLost" }, env.Tracker.ActiveDeviceFaults(RobotId));
    }

    [Fact]
    public async Task DuringAlarm_DispatchIsHeld_ThenResumesAfterReset()
    {
        var env = Build();
        // 남은 작업 없음 — 배차가 실제로 돌면 run이 COMPLETED로 전이한다(MQTT 발행 없이 "배차 실행"을 관찰하는 방법)
        var (runId, _) = await SeedRunAsync(env.Db, Wi("DONE"));

        await env.States.HandleStateAsync(Robot, State(LinkLost("amrLinkLost", "amr", "AMR 통신 두절")));
        await env.Dispatcher.DispatchNextAsync(runId, default);
        Assert.Equal("RUNNING", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);   // 보류

        // RESET → errors에서 사라짐 → 배차 재개(여기선 남은 작업이 없어 run 완료)
        await env.States.HandleStateAsync(Robot, State());
        Assert.False(env.Tracker.HasDeviceFault(RobotId));
        Assert.Equal("COMPLETED", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task HeavyAbortedStop_IsFailed_ButNextStopIsNotDispatched()
    {
        // 위험 시나리오 가드: AMR이 HEAVY로 정차를 중단(액션 FAILED)해도 다음 정차를 보내면 거부→재배차 연쇄로 큐가 소진된다.
        var env = Build();
        var orderId = Guid.NewGuid().ToString();
        var cur = Wi("DISPATCHED", orderId);
        var next = Wi("PENDING");
        var (_, mission) = await SeedRunAsync(env.Db, cur, next);
        mission.OrderId = orderId;
        env.Db.OrderActions.Add(new OrderActionEntity
        {
            ActionId = Guid.NewGuid(), MissionId = mission.MissionId, WorkItemId = cur.WorkItemId, ActionType = "startWeldInspection",
            Status = "FAILED", Result = JsonSerializer.Serialize(new { ActionStatus = "FAILED", ResultDescription = "aborted: 코봇 통신 두절(상태 소켓(20004) 끊김) (HEAVY)" }),
        });
        await env.Db.SaveChangesAsync();

        await env.States.HandleStateAsync(Robot, State(LinkLost("cobotLinkLost", "cobot", "코봇 통신 두절")));
        await env.Dispatcher.HandleStopOutcomeAsync(mission, default);   // 미연결 MQTT라 배차되면 예외 — 보류되면 무예외

        var items = await env.Db.WorkItems.AsNoTracking().ToListAsync();
        Assert.Equal("FAILED", items.Single(w => w.WorkItemId == cur.WorkItemId).Status);    // §5.2 실패 집계
        Assert.Equal("PENDING", items.Single(w => w.WorkItemId == next.WorkItemId).Status);  // 다음 정차 보류
        Assert.Equal("RUNNING", (await env.Db.ScenarioRuns.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Commands_AreBlocked_WithConflict()
    {
        var env = Build();
        await env.States.HandleStateAsync(Robot, State(LinkLost("ioModuleLinkLost", "ioModule", "I/O 모듈 통신 두절")));
        var ex = Assert.Throws<RobotDeviceFaultException>(() => env.Dispatcher.EnsureNoDeviceFault(RobotId, "미션 시작"));
        Assert.Contains("I/O 모듈", ex.Message);
        Assert.Contains("RESET", ex.Message);
        // 알람 없는 로봇은 통과
        env.Dispatcher.EnsureNoDeviceFault("OTHER", "미션 시작");
    }

    [Fact]
    public async Task OrderRejectedDeviceFault_ReturnsStopToPending_WithoutFailure()
    {
        var env = Build();
        var orderId = Guid.NewGuid().ToString();
        var cur = Wi("DISPATCHED", orderId);
        var (_, mission) = await SeedRunAsync(env.Db, cur);
        env.Db.OrderActions.Add(new OrderActionEntity
        { ActionId = Guid.NewGuid(), MissionId = mission.MissionId, WorkItemId = cur.WorkItemId, ActionType = "startWeldInspection", Status = "WAITING" });
        await env.Db.SaveChangesAsync();

        VdaError Rejected(string oid) => new()
        {
            ErrorType = "orderRejectedDeviceFault", ErrorLevel = "WARNING",
            ErrorDescription = "장치 HEAVY 알람 — 코봇(끊김), … 복구 후 RESET 필요",
            ErrorReferences = new()
            {
                new ErrorReference { ReferenceKey = "orderId", ReferenceValue = oid },
                new ErrorReference { ReferenceKey = "device", ReferenceValue = "cobot" },
            },
        };

        // 다른 orderId → 무시
        await env.States.HandleStateAsync(Robot, State(LinkLost("cobotLinkLost", "cobot", "x"), Rejected("other-order")));
        Assert.Equal("DISPATCHED", (await env.Db.WorkItems.AsNoTracking().SingleAsync()).Status);

        await env.States.HandleStateAsync(Robot, State(LinkLost("cobotLinkLost", "cobot", "x"), Rejected(orderId)));
        var wi = await env.Db.WorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(("PENDING", 0), (wi.Status, wi.Attempts));
        Assert.Null(wi.OrderId);
        Assert.Empty(await env.Db.OrderActions.AsNoTracking().ToListAsync());

        var codes = await env.Db.Alarms.AsNoTracking().Select(a => a.AlarmCode).ToListAsync();
        Assert.Contains("ORDER_REJECTED_DEVICE_FAULT", codes);
        Assert.DoesNotContain("INSPECTION_FAILED", codes);
        Assert.DoesNotContain("ORDER_REJECTED", codes);
        Assert.Single(codes, c => c == "ORDER_REJECTED_DEVICE_FAULT");   // edge 1회
    }

    [Fact]
    public void ErrorReferences_Deserialize_AndLegacyErrorsStillParse()
    {
        const string json = """
        {"orderId":"o1","errors":[
          {"errorType":"cobotLinkLost","errorLevel":"FATAL","errorDescription":"코봇 통신 두절",
           "errorReferences":[{"referenceKey":"device","referenceValue":"cobot"}]},
          {"errorType":"batteryLow","errorLevel":"WARNING","errorDescription":"18%"}]}
        """;
        var s = JsonSerializer.Deserialize<Vda5050State>(json)!;
        Assert.Equal("cobot", s.Errors[0].Ref("device"));
        Assert.Null(s.Errors[1].ErrorReferences);
        Assert.Null(s.Errors[1].Ref("device"));
        // 참조 없는 에러는 직렬화에서 필드 생략(기존 출력 보존)
        Assert.DoesNotContain("errorReferences", JsonSerializer.Serialize(s.Errors[1]));
    }

    [Fact]
    public void AlarmSpecSeed_IncludesDeviceCodes()
    {
        var specs = typeof(AlarmSpecSeed).GetField("Specs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var codes = ((System.Collections.IEnumerable)specs).Cast<object>()
            .Select(o => ((System.Runtime.CompilerServices.ITuple)o)[0] as string).ToList();
        Assert.Contains("DEVICE_LINK_LOST", codes);
        Assert.Contains("ORDER_REJECTED_DEVICE_FAULT", codes);
    }

    [Theory]
    [InlineData("visionLinkLost", "vision")]
    [InlineData("DepthCameraLinkLost", "depthCamera")]
    [InlineData("laserSensorLinkLost", "laserSensor")]
    [InlineData("ioModuleLinkLost", "ioModule")]
    [InlineData("cobotLinkLost", "cobot")]
    [InlineData("amrLinkLost", "amr")]
    public void DeviceCatalog_SixTypes(string type, string device)
    {
        Assert.True(DeviceFaults.IsLinkLost(type));
        Assert.Equal(device, DeviceFaults.DeviceOf(type));
        Assert.False(DeviceFaults.IsLinkLost("orderRejectedDeviceFault"));
    }

    private sealed class FakeHub : IHubContext<MonitoringHub>
    {
        public IHubClients Clients => new C();
        public IGroupManager Groups => null!;
        private sealed class C : IHubClients
        {
            private static readonly P _p = new();
            public IClientProxy All => _p; public IClientProxy AllExcept(IReadOnlyList<string> _) => _p;
            public IClientProxy Client(string _) => _p; public IClientProxy Clients(IReadOnlyList<string> _) => _p;
            public IClientProxy Group(string _) => _p; public IClientProxy GroupExcept(string _, IReadOnlyList<string> __) => _p;
            public IClientProxy Groups(IReadOnlyList<string> _) => _p; public IClientProxy User(string _) => _p;
            public IClientProxy Users(IReadOnlyList<string> _) => _p;
        }
        private sealed class P : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
