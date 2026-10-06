using System.Text.Json;
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
/// 이어하기(resume) — 가장 최근 run만 재개, 남은 정차는 현재 계획·캘리브레이션으로 다시 계산.
/// 2026-10-02 현장: 정차 이격을 바꾼 뒤 이어하기를 눌렀더니 전날 중단 run이 전날 좌표(우현 이동·벽 이동 전)로 재개되어
/// 로봇이 좌현 쪽 엉뚱한 곳으로 이동했다.
/// </summary>
public class ResumeTests
{
    private const double Tx = 9.35, Ty = 12.3, Yaw = -0.0027;

    private sealed record Fixture(AcsDbContext Db, InspectionDispatcher Dispatcher, MissionService Missions,
        Guid ScenarioId, InspectionAreaEntity Area);

    private static async Task<Fixture> Seed()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = Tx, Ty = Ty, YawRad = Yaw });
        var area = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01",
            Corners = "[[2.91,1.54],[3.11,1.54],[3.11,2.1],[2.91,2.1]]", StationStandoffM = 1.8,
            Tasks = { new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.01, StartV = 1.64, EndU = 3.01, EndV = 2.0,
                SectionDxfId = "DXF-1", ProfileId = "PROF-1" } },
        };
        db.InspectionAreas.Add(area);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = sc.ScenarioId, AreaId = area.AreaId });
        // 연결된 로봇(ONLINE·최근 state). 보고 층이 없어 배차는 보류된다 — 발행 없이 큐 상태만 검증
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var cfg = new ConfigurationBuilder().Build();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, cfg, NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);
        return new Fixture(db, dispatcher, missions, sc.ScenarioId, area);
    }

    /// <summary>run 시작(큐 전개) 후 첫 정차가 배차된 채 중단된 상태를 만든다. 로봇 컨텍스트가 없어 실제 발행은 없다.</summary>
    private static async Task<ScenarioRunEntity> AbortedRun(Fixture f, DateTimeOffset startedAt)
    {
        var run = new ScenarioRunEntity
        {
            RunId = Guid.NewGuid(), ScenarioId = f.ScenarioId, RobotId = "AMR-01", StartedAt = startedAt,
        };
        f.Db.ScenarioRuns.Add(run);
        await f.Dispatcher.BuildQueueAsync(run, "CT1", default);
        run.State = "ABORTED";
        run.EndedAt = startedAt.AddMinutes(1);
        await f.Db.SaveChangesAsync();
        var wi = await f.Db.WorkItems.SingleAsync(w => w.RunId == run.RunId);
        wi.Status = "DISPATCHED";
        wi.OrderId = Guid.NewGuid().ToString();
        await f.Db.SaveChangesAsync();
        return run;
    }

    private static async Task Edit(Fixture f, Action<InspectionAreaEntity> edit)
    {
        var a = await f.Db.InspectionAreas.Include(x => x.Tasks).SingleAsync(x => x.AreaId == f.Area.AreaId);
        edit(a);
        await f.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resume_RecomputesStopAndSeamFromCurrentPlan()
    {
        var f = await Seed();
        var run = await AbortedRun(f, DateTimeOffset.UtcNow.AddHours(-1));
        var before = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == run.RunId);

        // 계획 변경: 우현 2 m 이동(u +2) + 정차 이격 1.8 → 1.5
        await Edit(f, a =>
        {
            a.Corners = "[[4.91,1.54],[5.11,1.54],[5.11,2.1],[4.91,2.1]]";
            a.StationStandoffM = 1.5;
            foreach (var t in a.Tasks) { t.StartU = 5.01; t.EndU = 5.01; }
        });

        var summary = await f.Missions.ResumeRunAsync(run.RunId);
        Assert.Equal(new ResumeSummary(1, 0), summary);

        var after = await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == run.RunId);
        Assert.Equal("PENDING", after.Status);   // DISPATCHED 리셋, 로봇 위치 미상이라 배차 보류
        // 재계산 결과 = 지금 계획으로 조회한 계획 정차점에 T_W_D 적용한 값 (= 새 run이었다면 갈 자리)
        var st = (await f.Dispatcher.GetPlannedStationsAsync(f.ScenarioId, default))!.Stations.Single();
        var (mx, my) = new HD.Acs.Core.Geometry.DrawingTransform(Tx, Ty, Yaw).DrawingToMap(st.X, st.Y);
        Assert.Equal(mx, after.X, 9);
        Assert.Equal(my, after.Y, 9);
        Assert.True(Math.Abs(after.Y - before.Y) > 1.9);   // 우현 2 m 반영 (옛 스냅샷이면 그대로였을 것)
        // 액션 payload의 용접선 도면 위치도 새 u로
        var action = JsonDocument.Parse(after.Actions!).RootElement[0];
        Assert.Equal(5.01, action.GetProperty("position").GetProperty("drawingPos").GetProperty("u").GetDouble(), 6);
        Assert.Equal("RUNNING", (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == run.RunId)).State);
    }

    [Fact]
    public async Task Resume_SkipsStopWhoseAreaWasDeleted()
    {
        var f = await Seed();
        var run = await AbortedRun(f, DateTimeOffset.UtcNow.AddHours(-1));
        f.Db.InspectionAreas.Remove(await f.Db.InspectionAreas.SingleAsync(a => a.AreaId == f.Area.AreaId));
        await f.Db.SaveChangesAsync();

        var summary = await f.Missions.ResumeRunAsync(run.RunId);
        Assert.Equal(new ResumeSummary(0, 1), summary);
        Assert.Equal("SKIPPED", (await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == run.RunId)).Status);
        Assert.Equal(0, (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == run.RunId)).TotalTasks);
    }

    [Fact]
    public async Task OlderRun_IsNotResumable_WhenNewerRunExists()
    {
        var f = await Seed();
        var old = await AbortedRun(f, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.NotNull(await f.Missions.FindResumableRunAsync("AMR-01"));   // 최근 run이 이것뿐이면 재개 가능

        // 그 뒤 새 run이 시작되어 완료(건너뜀 포함) — 이어하기는 더 이상 옛 run을 고르지 않는다
        f.Db.ScenarioRuns.Add(new ScenarioRunEntity
        {
            RunId = Guid.NewGuid(), ScenarioId = f.ScenarioId, RobotId = "AMR-01", State = "COMPLETED",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), EndedAt = DateTimeOffset.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        Assert.Null(await f.Missions.FindResumableRunAsync("AMR-01"));
        var ex = await Assert.ThrowsAsync<RunStateException>(() => f.Missions.ResumeRunAsync(old.RunId));
        Assert.Contains("가장 최근 run만", ex.Message);
        Assert.Equal("DISPATCHED", (await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == old.RunId)).Status);
    }

    // ── AMR 연결 확인 — 미연결이면 명령을 보내지 않고 ROBOT_NOT_CONNECTED 알람 ──

    private static async Task SetConnection(Fixture f, string? state, DateTimeOffset? reportedAt, bool remove = false)
    {
        var ctx = await f.Db.RobotContexts.SingleAsync(c => c.RobotId == "AMR-01");
        if (remove) f.Db.RobotContexts.Remove(ctx);
        else { ctx.ConnectionState = state; ctx.ReportedAt = reportedAt; }
        await f.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("CONNECTIONBROKEN", 0, "연결 상태 CONNECTIONBROKEN")]
    [InlineData("OFFLINE", 0, "연결 상태 OFFLINE")]
    [InlineData("ONLINE", 60, "마지막 상태 수신 60초 전")]   // retained ONLINE만 남고 state가 끊긴 경우
    public async Task StartRun_IsBlockedWithAlarm_WhenRobotNotConnected(string state, int agoSec, string reason)
    {
        var f = await Seed();
        await SetConnection(f, state, DateTimeOffset.UtcNow.AddSeconds(-agoSec));

        var ex = await Assert.ThrowsAsync<RobotNotConnectedException>(() => f.Missions.StartRunAsync(f.ScenarioId, "AMR-01"));
        Assert.Contains(reason, ex.Message);
        Assert.Empty(await f.Db.ScenarioRuns.AsNoTracking().ToListAsync());   // run·큐·Order 없음
        var alarm = Assert.Single(await f.Db.Alarms.AsNoTracking().ToListAsync());
        Assert.Equal(("ROBOT_NOT_CONNECTED", "AMR-01"), (alarm.AlarmCode, alarm.RobotId));
        Assert.Contains("미션 시작 차단", JsonDocument.Parse(alarm.Detail!).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task StartRun_IsBlocked_WhenRobotNeverReported()
    {
        var f = await Seed();
        await SetConnection(f, null, null, remove: true);
        var ex = await Assert.ThrowsAsync<RobotNotConnectedException>(() => f.Missions.StartRunAsync(f.ScenarioId, "AMR-01"));
        Assert.Contains("한 번도 받지 못함", ex.Message);
    }

    [Fact]
    public async Task Resume_IsBlockedWithAlarm_WhenRobotNotConnected_AndRunUnchanged()
    {
        var f = await Seed();
        var run = await AbortedRun(f, DateTimeOffset.UtcNow.AddHours(-1));
        await SetConnection(f, "CONNECTIONBROKEN", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<RobotNotConnectedException>(() => f.Missions.ResumeRunAsync(run.RunId));
        Assert.Equal("ABORTED", (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == run.RunId)).State);
        Assert.Equal("DISPATCHED", (await f.Db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == run.RunId)).Status);
        Assert.Contains("이어하기 차단", JsonDocument.Parse((await f.Db.Alarms.AsNoTracking().SingleAsync()).Detail!)
            .RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task StartRun_Proceeds_WhenRobotConnected()
    {
        var f = await Seed();
        var runId = await f.Missions.StartRunAsync(f.ScenarioId, "AMR-01");
        Assert.Equal("RUNNING", (await f.Db.ScenarioRuns.AsNoTracking().SingleAsync(r => r.RunId == runId)).State);
        Assert.Empty(await f.Db.Alarms.AsNoTracking().ToListAsync());
    }
}
