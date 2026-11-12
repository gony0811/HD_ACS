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

/// <summary>영역 검사 가능 지도 — 운영 조건(저장된 정차·리프트)에서 영역의 용접선이 전부 가능해야 가능. 하나라도 불가면 불가.</summary>
public class ReachAreaMapTests
{
    private static async Task<(AcsDbContext Db, ReachSimService Sim, InspectionAreaEntity Near, InspectionAreaEntity Far)> Seed()
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

        static AreaTaskEntity Task(int seq, double u) => new()
        {
            TaskId = Guid.NewGuid(), Seq = seq, StartU = u, StartV = 1.64, EndU = u, EndV = 2.0,
            SectionDxfId = "DXF-1", ProfileId = "PROF-1",
        };
        var near = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.4,
            Tasks = { Task(1, 3.1), Task(2, 3.3) },
        };
        // 벽에서 3.5 m 떨어진 정차 — 코봇이 닿지 않는다
        var far = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-02", SortOrder = 1,
            Corners = "[[4.9,1.5],[5.6,1.5],[5.6,2.1],[4.9,2.1]]", StationStandoffM = 3.5,
            Tasks = { Task(1, 5.1), Task(2, 5.3) },
        };
        db.InspectionAreas.AddRange(near, far);
        await db.SaveChangesAsync();

        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return (db, new ReachSimService(db, dispatcher), near, far);
    }

    [Fact]
    public async Task Area_IsPossible_OnlyWhenEveryWeldIsClear_UnderStoredStationAndLift()
    {
        var (_, sim, near, far) = await Seed();

        var map = (await sim.AreaMapAsync("CT1", 1, "F", CancellationToken.None))!;
        // 같은 조건(저장된 정차, 리프트 0)의 조사 판정과 일치해야 한다
        var survey = (await sim.SurveyAsync("CT1", 1, "F", CancellationToken.None, strokeMm: 0))!;

        Assert.Equal(2, map.Count);
        foreach (var a in map)
        {
            var seams = survey.Single(s => s.AreaId == a.AreaId).Seams;
            var bad = seams.Where(s => s.Verdict != "Clear").Select(s => s.Seq).ToArray();
            Assert.Equal(bad.Length == 0, a.Possible);
            Assert.Equal(bad, a.BadSeqs);
            Assert.Equal((seams.Count, seams.Count - bad.Length, 0), (a.Welds, a.Ok, a.Disabled));
            Assert.Equal(4, a.Corners.Length);
            // 용접선마다 판정과 면 좌표가 실린다(3D 에 용접선을 색으로 그리는 자료)
            Assert.Equal(seams.Select(x => (x.Seq, (bool?)(x.Verdict == "Clear"))), a.Seams.Select(x => (x.Seq, x.Ok)));
            Assert.All(a.Seams, x => Assert.Equal(1.64, x.StartV));
        }
        var f2 = map.Single(a => a.AreaId == far.AreaId);
        Assert.False(f2.Possible);                 // 닿지 않는 정차 → 불가
        Assert.Equal(new[] { 1, 2 }, f2.BadSeqs);
        Assert.Contains(map, a => a.AreaId == near.AreaId);
    }

    [Fact]
    public async Task DisabledWelds_AreLeftOut_AndAreaWithNoTargetHasNoVerdict()
    {
        var (db, sim, _, far) = await Seed();
        foreach (var t in await db.AreaTasks.Where(t => t.AreaId == far.AreaId).ToListAsync()) t.Enabled = false;
        await db.SaveChangesAsync();

        var f2 = (await sim.AreaMapAsync("CT1", 1, "F", CancellationToken.None))!.Single(a => a.AreaId == far.AreaId);

        Assert.Null(f2.Possible);                  // 검사 대상 용접선이 없다 — 가능도 불가도 아님
        Assert.Equal((0, 0, 2), (f2.Welds, f2.Ok, f2.Disabled));
        Assert.Equal(2, f2.Seams.Count);            // 위치는 실리되 판정은 없다
        Assert.All(f2.Seams, x => Assert.Null(x.Ok));
    }

    [Fact]
    public async Task Filters_AndUnknownTank()
    {
        var (_, sim, _, _) = await Seed();
        Assert.Empty((await sim.AreaMapAsync("CT1", 2, null, CancellationToken.None))!);   // 그 층에 영역 없음
        Assert.Equal(2, (await sim.AreaMapAsync("CT1", null, null, CancellationToken.None))!.Count);
        Assert.Null(await sim.AreaMapAsync("NOPE", 1, null, CancellationToken.None));
    }
}
