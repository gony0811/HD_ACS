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
/// AMR `stationReachShortfall` → STATION_REACH_SHORTFALL 알람 [VDA §6.4 / N23 A안].
///  · 원래 정차점 기준 총 후진량 = extra(실제 선 자리 기준) + adjusted(이미 보정한 양)
///  · 같은 정차의 여러 액션은 같은 errorType 으로 차례로 오므로 **actionId 별** 1회, state 반복에도 중복 없음
/// </summary>
public class StationReachShortfallTests
{
    private const string RobotId = "AMR-01";
    private static readonly RobotRef Robot = new(RobotId, "HD", "AMR-01");

    // ── 해석 ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TotalIsExtraPlusAdjusted()
    {
        var sf = StationReachShortfall.TryParse("a1: normalMm=980 requiredMm=1120 extraBackwardMm=160 adjustedBackwardMm=400", null)!;
        Assert.Equal(("a1", 980.0, 1120.0), (sf.ActionId, sf.NormalMm, sf.RequiredMm));
        Assert.Equal(560, sf.TotalBackwardMm);
    }

    [Fact]
    public void Parse_Unfixable()
    {
        var sf = StationReachShortfall.TryParse("a1: normalMm=900 requiredMm=1120 extraBackwardMm=-1 adjustedBackwardMm=0", null)!;
        Assert.False(sf.Fixable);
        Assert.Null(sf.TotalBackwardMm);
    }

    [Fact]
    public void Parse_ReferenceWinsOverPrefix_AdjustedOptional()
    {
        var sf = StationReachShortfall.TryParse("x: normalMm=800 requiredMm=1120 extraBackwardMm=340", "ref-id")!;
        Assert.Equal("ref-id", sf.ActionId);
        Assert.Equal(340, sf.TotalBackwardMm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a1: 정차 거리 부족")]                       // 수치 없음
    [InlineData("a1: normalMm=800 requiredMm=1120")]          // extra 누락
    public void Parse_Incomplete_ReturnsNull(string? d) => Assert.Null(StationReachShortfall.TryParse(d, null));

    // ── 알람 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Alarm_PerAction_WithAreaName_NoDuplicateOnRepeat()
    {
        var (db, states) = Build();
        var (a1, a2) = await SeedAsync(db, "PM-03");

        await states.HandleStateAsync(Robot, State(Err(a1, "normalMm=800 requiredMm=1120 extraBackwardMm=340 adjustedBackwardMm=0")));
        await states.HandleStateAsync(Robot, State(Err(a1, "normalMm=800 requiredMm=1120 extraBackwardMm=340 adjustedBackwardMm=0")));   // state 반복
        await states.HandleStateAsync(Robot, State(Err(a2, "normalMm=950 requiredMm=1120 extraBackwardMm=190 adjustedBackwardMm=0")));   // 같은 errorType, 다음 액션

        var alarms = await db.Alarms.AsNoTracking().Where(a => a.AlarmCode == "STATION_REACH_SHORTFALL")
            .OrderBy(a => a.RaisedAt).ToListAsync();
        Assert.Equal(2, alarms.Count);
        var d = JsonDocument.Parse(alarms[0].Detail!).RootElement;
        Assert.Equal("PM-03", d.GetProperty("area").GetString());
        Assert.Equal(340, d.GetProperty("totalBackwardMm").GetDouble());
        Assert.Contains("총 340mm 후진 필요", d.GetProperty("title").GetString());
        Assert.NotNull(alarms[0].MissionId);
    }

    /// <summary>PostgreSQL jsonb 는 저장한 JSON 을 공백·키 순서를 바꿔 돌려준다 — 그래도 같은 액션으로 인식해야 한다.</summary>
    [Fact]
    public async Task Alarm_Dedupe_SurvivesJsonbReformatting()
    {
        var (db, states) = Build();
        var (a1, _) = await SeedAsync(db, "PM-03");
        db.Alarms.Add(new AlarmEntity
        {
            AlarmId = Guid.NewGuid(), AlarmCode = "STATION_REACH_SHORTFALL", RobotId = RobotId,
            Detail = $"{{\"area\": \"PM-03\", \"key\": \"{a1}\", \"title\": \"x\"}}",
        });
        await db.SaveChangesAsync();
        await states.HandleStateAsync(Robot, State(Err(a1, "normalMm=800 requiredMm=1120 extraBackwardMm=340 adjustedBackwardMm=0")));
        Assert.Single(await db.Alarms.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Alarm_Unfixable_SaysReviewLiftAndStop()
    {
        var (db, states) = Build();
        var (a1, _) = await SeedAsync(db, "B-01");
        await states.HandleStateAsync(Robot, State(Err(a1, "normalMm=900 requiredMm=1120 extraBackwardMm=-1 adjustedBackwardMm=0")));
        var d = JsonDocument.Parse((await db.Alarms.AsNoTracking().SingleAsync()).Detail!).RootElement;
        Assert.Contains("후진으로 해결 불가", d.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Alarm_UnparsableDescription_StillRaised()
    {
        var (db, states) = Build();
        await states.HandleStateAsync(Robot, State(new VdaError
        {
            ErrorType = StationReachShortfall.ErrorType, ErrorLevel = "WARNING", ErrorDescription = "정차 거리 부족(수치 없음)",
        }));
        Assert.Contains("해석 불가", JsonDocument.Parse((await db.Alarms.AsNoTracking().SingleAsync()).Detail!).RootElement
            .GetProperty("title").GetString());
    }

    private static VdaError Err(Guid actionId, string kv) => new()
    {
        ErrorType = StationReachShortfall.ErrorType, ErrorLevel = "WARNING",
        ErrorDescription = $"{actionId}: {kv}",
        ErrorReferences = new() { new ErrorReference { ReferenceKey = "actionId", ReferenceValue = actionId.ToString() } },
    };

    private static Vda5050State State(params VdaError[] errors)
    {
        var s = new Vda5050State { OrderId = "unmatched", AgvPosition = new AgvPosition { MapId = "CT1-L1", X = 1, Y = 2 } };
        s.Errors.AddRange(errors);
        return s;
    }

    private static async Task<(Guid A1, Guid A2)> SeedAsync(AcsDbContext db, string areaName)
    {
        var area = new InspectionAreaEntity { AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "PM", Level = 1, Name = areaName };
        db.InspectionAreas.Add(area);
        var mission = Guid.NewGuid();
        var wi = new WorkItemEntity { WorkItemId = Guid.NewGuid(), RunId = Guid.NewGuid(), AreaId = area.AreaId, MapId = "CT1-L1", Status = "DISPATCHED" };
        db.WorkItems.Add(wi);
        var a1 = Guid.NewGuid(); var a2 = Guid.NewGuid();
        foreach (var a in new[] { a1, a2 })
            db.OrderActions.Add(new OrderActionEntity
            {
                ActionId = a, MissionId = mission, WorkItemId = wi.WorkItemId, ActionType = "startWeldInspection", Status = "RUNNING",
            });
        await db.SaveChangesAsync();
        return (a1, a2);
    }

    private static (AcsDbContext Db, RobotStateService States) Build()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Robots.Add(new RobotEntity { RobotId = RobotId, Manufacturer = "HD", SerialNumber = "AMR-01", Name = RobotId, VdaVersion = "2.0", IsActive = true });
        db.SaveChanges();
        var tracker = new RobotErrorTracker();
        var vda = new Vda5050MasterClient("localhost");
        var hub = new FakeHub();
        var dispatcher = new InspectionDispatcher(db, vda, new GreedyNearestPolicy(), hub,
            new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance, tracker);
        var missions = new MissionService(db, vda, dispatcher, NullLogger<MissionService>.Instance);
        var progress = new ProgressService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        return (db, new RobotStateService(db, hub, dispatcher, progress, tracker, missions, NullLogger<RobotStateService>.Instance));
    }

    private sealed class FakeHub : IHubContext<MonitoringHub>
    {
        public IHubClients Clients => new C();
        public IGroupManager Groups => null!;
        private sealed class C : IHubClients
        {
            private static readonly P Proxy = new();
            public IClientProxy All => Proxy; public IClientProxy AllExcept(IReadOnlyList<string> _) => Proxy;
            public IClientProxy Client(string _) => Proxy; public IClientProxy Clients(IReadOnlyList<string> _) => Proxy;
            public IClientProxy Group(string _) => Proxy; public IClientProxy GroupExcept(string _, IReadOnlyList<string> __) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> _) => Proxy; public IClientProxy User(string _) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> _) => Proxy;
        }
        private sealed class P : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
