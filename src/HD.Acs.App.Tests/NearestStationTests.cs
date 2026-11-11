using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 배차 직전 정차 선택(Acs:Dispatch:NearestStation) — 저장된 정차 대신 로봇 현재 위치에서 가장 가까운 수행 가능한 정차를 골라
/// 그 정차(work_item)에만 쓴다.
/// </summary>
public class NearestStationTests
{
    private sealed class Provider : IServiceProvider
    {
        public ReachSimService? Sim;
        public object? GetService(Type t) => t == typeof(ReachSimService) ? Sim : null;
    }

    private sealed record Fixture(AcsDbContext Db, InspectionDispatcher Dispatcher, MissionService Missions,
        Guid ScenarioId, InspectionAreaEntity Area);

    /// <summary>F면 L1 영역 1개(용접선 3개), T_W_D = 평행이동 (9.35, 12.3).</summary>
    private static async Task<Fixture> Seed(bool enabled)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = 0 });

        static AreaTaskEntity Task(int seq, double u) => new()
        {
            TaskId = Guid.NewGuid(), Seq = seq, StartU = u, StartV = 1.64, EndU = u, EndV = 2.0,
            SectionDxfId = "DXF-1", ProfileId = "PROF-1",
        };
        var a = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.8,
            Tasks = { Task(1, 3.0), Task(2, 3.2), Task(3, 3.4) },
        };
        db.InspectionAreas.Add(a);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Dispatch:NearestStation:Enabled"] = enabled ? "true" : "false",
            ["Acs:Dispatch:AllowedDevXy"] = "0.2",
        }).Build();
        var provider = new Provider();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, cfg, NullLogger<InspectionDispatcher>.Instance, services: provider);
        provider.Sim = new ReachSimService(db, dispatcher);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return new Fixture(db, dispatcher, missions, sc.ScenarioId, a);
    }

    [Fact]
    public async Task Picks_TheStationNearestToTheRobot_ForThisStopOnly()
    {
        var f = await Seed(enabled: true);
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");   // 층 미보고 → 배차 보류, 큐만 생긴다
        var run = await f.Db.ScenarioRuns.SingleAsync(r => r.RunId == runId);
        var wi = await f.Db.WorkItems.SingleAsync(w => w.RunId == runId);
        var (savedX, savedY) = (wi.X, wi.Y);

        // 로봇은 저장된 정차에서 면을 따라 옆으로 1.5 m 떨어진 곳에 있다(맵 프레임 — F면은 도면 y 방향이 면 가로)
        var (rx, ry) = (savedX, savedY + 1.5);
        var pick = await f.Dispatcher.SelectNearestStopAsync(run, wi, rx, ry, default);

        Assert.NotNull(pick);
        Assert.True(pick!.TravelM < pick.SavedTravelM - 0.05, $"travel {pick.TravelM} vs saved {pick.SavedTravelM}");
        Assert.Equal(1.5, pick.SavedTravelM, 2);
        // 이 정차(work_item)만 바뀐다 — 고른 정차에 T_W_D 적용, 액션에 고른 리프트·고정 표시
        Assert.Equal(pick.X + 9.35, wi.X, 3);
        Assert.Equal(pick.Y + 12.3, wi.Y, 3);
        Assert.Equal(pick.TravelM, Math.Sqrt(Math.Pow(wi.X - rx, 2) + Math.Pow(wi.Y - ry, 2)), 2);
        var actions = JsonNode.Parse(wi.Actions!)!.AsArray();
        Assert.Equal(3, actions.Count);
        Assert.All(actions, a =>
        {
            Assert.True(a!["liftFixed"]!.GetValue<bool>());
            Assert.Equal(pick.LiftMm, a["params"]!["liftStrokeMm"]!.GetValue<double>());
        });
        // 영역에 저장된 정차는 그대로
        var area = await f.Db.InspectionAreas.AsNoTracking().SingleAsync();
        Assert.Equal((null, null, null, 1.8), (area.StationX, area.StationY, area.StationLiftMm, area.StationStandoffM));

        // 고른 정차는 그대로 간다(재시도) — 다시 고르지 않는다
        Assert.Null(await f.Dispatcher.SelectNearestStopAsync(run, wi, rx, ry, default));
    }

    [Fact]
    public async Task RobotAtTheSavedStation_KeepsIt()
    {
        var f = await Seed(enabled: true);
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        var run = await f.Db.ScenarioRuns.SingleAsync(r => r.RunId == runId);
        var wi = await f.Db.WorkItems.SingleAsync(w => w.RunId == runId);
        var (savedX, savedY) = (wi.X, wi.Y);

        var pick = await f.Dispatcher.SelectNearestStopAsync(run, wi, savedX, savedY, default);

        // 저장된 정차가 조건을 채우면 그 자리(주행 0), 못 채우면 근처 — 어느 쪽이든 저장된 정차보다 멀지 않다
        if (pick is not null)
        {
            Assert.True(pick.TravelM <= 0.45, $"travel {pick.TravelM}");
            Assert.Equal(0, pick.SavedTravelM, 3);
        }
        else Assert.Equal((savedX, savedY), (wi.X, wi.Y));
    }

    [Fact]
    public async Task RobotStandingNearby_OnlyTurnsInPlace()
    {
        var f = await Seed(enabled: true);
        var sim = new ReachSimService(f.Db, f.Dispatcher);
        var on = new HD.Acs.Core.Reach.NearestOptions(RobustM: 0.1);
        var planned = (await f.Dispatcher.GetPlannedStationsAsync(f.ScenarioId, default))!.Stations.Single();
        // 조건을 채우는 정차 하나를 찾고, 로봇을 거기서 격자와 어긋나게 5 cm · 5 cm 옮겨 벽 정면에서 10° 돌려 세운다
        var good = await sim.NearestStopAsync(f.Area.AreaId, planned.X, planned.Y, on, default);
        Assert.NotNull(good);
        var (rx, ry, ryaw) = (good!.X + 0.05, good.Y + 0.05, good.Yaw + 10 * Math.PI / 180);

        var turn = await sim.NearestStopAsync(f.Area.AreaId, rx, ry, on, default, ryaw);
        var drive = await sim.NearestStopAsync(f.Area.AreaId, rx, ry, on with { InPlace = false }, default, ryaw);

        Assert.NotNull(turn);
        Assert.Equal(0, turn!.TravelM, 3);                          // 주행 없이 그 자리에서
        Assert.Equal((rx, ry), (turn.X, turn.Y));
        // 방향은 지금 방향에서 가장 덜 도는 통과 후보(벽면은 정면 ±15° 안, 5° 간격)
        Assert.True(Math.Abs(turn.Yaw - ryaw) <= 25 * Math.PI / 180 + 1e-6, $"yaw {turn.Yaw} vs {ryaw}");
        // 제자리 회전 후보를 끄면 격자점까지 움직인다
        Assert.True(drive is null || drive.TravelM > 0.03, $"drive {drive?.TravelM}");
    }

    [Fact]
    public async Task Off_Or_RepairRun_LeavesTheSavedStation()
    {
        var off = await Seed(enabled: false);
        var runId = await off.Missions.StartRunAsync(off.ScenarioId, "AMR-01");
        var run = await off.Db.ScenarioRuns.SingleAsync(r => r.RunId == runId);
        var wi = await off.Db.WorkItems.SingleAsync(w => w.RunId == runId);
        var before = (wi.X, wi.Y, wi.Actions);
        Assert.Null(await off.Dispatcher.SelectNearestStopAsync(run, wi, wi.X, wi.Y + 1.5, default));
        Assert.Equal(before, (wi.X, wi.Y, wi.Actions));

        // 리페어 run 은 재검사 전 정차 최적화가 따로 고른다
        var on = await Seed(enabled: true);
        var repair = (await on.Missions.StartRepairRunAsync("AMR-01", new[] { on.Area.Tasks[0].TaskId })).RunId;
        var rrun = await on.Db.ScenarioRuns.SingleAsync(r => r.RunId == repair);
        var rwi = await on.Db.WorkItems.SingleAsync(w => w.RunId == repair);
        Assert.Null(await on.Dispatcher.SelectNearestStopAsync(rrun, rwi, rwi.X, rwi.Y + 1.5, default));
    }
}
