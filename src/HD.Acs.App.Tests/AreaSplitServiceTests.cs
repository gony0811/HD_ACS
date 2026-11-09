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

/// <summary>영역 재분할 — 제안(조회 전용)과 적용(한 트랜잭션: 원래 영역 축소·새 영역·작업 이동·정차·시나리오 연결).</summary>
public class AreaSplitServiceTests
{
    private static readonly Guid Area = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Scenario = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid[] T = Enumerable.Range(1, 4).Select(i => Guid.Parse($"cccccccc-0000-0000-0000-00000000000{i}")).ToArray();

    /// <summary>L2 SM 수직벽 1.44 × 0.72 m 영역, 가로 용접선 4개(두 열 × 두 줄).</summary>
    private static async Task<AcsDbContext> SeedAsync()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976, new[] { 0.0, 3.63, 6.63, 10.53 });
        db.TankGeometries.Add(new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 31.785, WFloor = 10.1497, ThetaLow = Math.PI / 4, HLow = 3.9776, HWall = 6.9097,
            ThetaUp = Math.PI / 4, HUp = 2.8976, LevelZ = "[0,3.63,6.63,10.53]",
        });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        var v = 3.63 + 1.5 - 3.9776;
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Area, TankId = "CT1", WallCode = "SM", Level = 2, Name = "L2-SM-R01C01", SortOrder = 5,
            Corners = JsonSerializer.Serialize(new[] { new[] { 11.28, v - 0.3 }, new[] { 12.72, v - 0.3 }, new[] { 12.72, v + 0.42 }, new[] { 11.28, v + 0.42 } }),
            StationLiftMm = 100,
            Tasks =
            {
                new AreaTaskEntity { TaskId = T[0], Seq = 1, Name = "H01", SeamType = "LINE", StartU = 11.3, StartV = v, EndU = 11.66, EndV = v },
                new AreaTaskEntity { TaskId = T[1], Seq = 2, Name = "H02", SeamType = "LINE", StartU = 12.3, StartV = v, EndU = 12.66, EndV = v },
                new AreaTaskEntity { TaskId = T[2], Seq = 3, Name = "H03", SeamType = "LINE", StartU = 11.3, StartV = v + 0.36, EndU = 11.66, EndV = v + 0.36 },
                new AreaTaskEntity { TaskId = T[3], Seq = 4, Name = "H04", SeamType = "LINE", StartU = 12.3, StartV = v + 0.36, EndU = 12.66, EndV = v + 0.36 },
            },
        });
        db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = Scenario, AreaId = Area, SortOrder = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    /// <param name="name">새 영역이 되는 묶음(뒤 묶음 — 같은 크기면 앞 묶음이 원래 영역에 남는다)의 이름.</param>
    private static ApplyAreaSplitRequest TwoColumns(string? name = null) => new(new()
    {
        new AreaSplitGroupRequest(new[] { T[1], T[3] }, 12.5, -7.0, 1.57, 300),
        new AreaSplitGroupRequest(new[] { T[0], T[2], }, 11.5, -7.0, 1.57, 0, name),
    }, "tester");

    [Fact]
    public async Task 적용하면_큰_묶음은_원래_영역에_남고_나머지는_새_영역으로_옮긴다()
    {
        await using var db = await SeedAsync();
        // 같은 크기면 앞 묶음이 원래 영역에 남는다.
        var r = await new AreaSplitService(db).ApplyAsync(Area, TwoColumns());

        Assert.Equal(2, r.Areas.Count);
        Assert.False(r.Areas[0].Created);
        Assert.Equal(Area, r.Areas[0].AreaId);
        Assert.Equal("L2-SM-R01C01-2", r.Areas[1].Name);

        db.ChangeTracker.Clear();
        var keep = await db.InspectionAreas.Include(a => a.Tasks).SingleAsync(a => a.AreaId == Area);
        var made = await db.InspectionAreas.Include(a => a.Tasks).SingleAsync(a => a.AreaId == r.Areas[1].AreaId);
        Assert.Equal(new[] { T[1], T[3] }, keep.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));   // taskId 유지
        Assert.Equal(new[] { T[0], T[2] }, made.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));
        Assert.Equal(new[] { 1, 2 }, made.Tasks.Select(t => t.Seq).OrderBy(s => s));
        Assert.Equal(new[] { "H01", "H02" }, made.Tasks.OrderBy(t => t.Seq).Select(t => t.Name));   // 도구 형식 이름은 새 순번
        // 사각형은 묶음 용접선만 감싸도록 축소 — 원래 영역은 u 12.3 부터
        Assert.True(keep.UMin >= 12.2 && keep.UMax <= 12.72, $"{keep.UMin}~{keep.UMax}");
        Assert.True(made.UMax <= 11.8, $"{made.UMin}~{made.UMax}");
        Assert.Equal((12.5, -7.0, 1.57, 300.0), (keep.StationX!.Value, keep.StationY!.Value, keep.StationTheta!.Value, keep.StationLiftMm!.Value));
        Assert.Equal(0, made.StationLiftMm);
        Assert.Equal(2, made.Level);
        // 원래 영역이 연결된 시나리오에 새 영역도 연결
        Assert.Equal(2, await db.ScenarioAreas.CountAsync(sa => sa.ScenarioId == Scenario));
        Assert.Single(await db.AuditLogs.Where(l => l.Action == "AREA_SPLIT_PLAN_APPLY").ToListAsync());
    }

    [Fact]
    public async Task 작업이_빠지거나_겹치면_거부하고_DB_를_바꾸지_않는다()
    {
        await using var db = await SeedAsync();
        var svc = new AreaSplitService(db);
        var missing = new ApplyAreaSplitRequest(new()
        {
            new AreaSplitGroupRequest(new[] { T[0] }, 1, 1, 0),
            new AreaSplitGroupRequest(new[] { T[1] }, 1, 1, 0),
        });
        Assert.Equal(400, (await Assert.ThrowsAsync<AreaSplitException>(() => svc.ApplyAsync(Area, missing))).Status);
        var dup = new ApplyAreaSplitRequest(new()
        {
            new AreaSplitGroupRequest(new[] { T[0], T[1] }, 1, 1, 0),
            new AreaSplitGroupRequest(new[] { T[1], T[2], T[3] }, 1, 1, 0),
        });
        Assert.Equal(400, (await Assert.ThrowsAsync<AreaSplitException>(() => svc.ApplyAsync(Area, dup))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<AreaSplitException>(() => svc.ApplyAsync(Guid.NewGuid(), TwoColumns()))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<AreaSplitException>(() => svc.ApplyAsync(Area, TwoColumns("L2-SM-R01C01")))).Status);

        db.ChangeTracker.Clear();
        Assert.Single(await db.InspectionAreas.ToListAsync());
        Assert.All(await db.AreaTasks.ToListAsync(), t => Assert.Equal(Area, t.AreaId));
    }

    [Fact]
    public async Task 제안은_모든_작업을_한_번씩_묶고_새_사각형을_싣는다()
    {
        await using var db = await SeedAsync();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var sim = new ReachSimService(db, dispatcher);

        var plan = (await sim.SplitPlanAsync(Area, 4, default))!;

        Assert.Equal(Area, plan.AreaId);
        Assert.True(plan.GridCells > 0);
        Assert.Equal(T.OrderBy(x => x), plan.Groups.SelectMany(g => g.TaskIds).OrderBy(x => x));
        Assert.All(plan.Groups, g =>
        {
            Assert.Equal(4, g.Corners.Length);
            Assert.Equal(g.TaskIds.Count, g.Seams.Count);
        });
        Assert.Null(await sim.SplitPlanAsync(Guid.NewGuid(), 4, default));
    }
}
