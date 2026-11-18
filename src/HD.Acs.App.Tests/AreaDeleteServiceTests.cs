using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 영역 삭제 — 작업은 원래 영역으로 되돌리고 영역만 지운다(2026-11-18 현장: 분리 영역 L1-R03C12-S1 삭제로 작업 3개 소실).
/// </summary>
public class AreaDeleteServiceTests
{
    private static readonly Guid C12 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000012");
    private static readonly Guid C13 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000013");
    private static readonly Guid Overlap = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000ff");
    private static readonly Guid S1 = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid H07 = Guid.Parse("cccccccc-0000-0000-0000-000000000007");
    private static readonly Guid H08 = Guid.Parse("cccccccc-0000-0000-0000-000000000008");
    private static readonly Guid H05 = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    private static string Rect(double u0, double v0, double u1, double v1) =>
        JsonSerializer.Serialize(new[] { new[] { u0, v1 }, new[] { u1, v1 }, new[] { u1, v0 }, new[] { u0, v0 } });

    private static AreaTaskEntity Task(Guid id, int seq, string name, double u0, double u1, double v, string type = "LINE") =>
        new() { TaskId = id, Seq = seq, Name = name, SeamType = type, StartU = u0, StartV = v, EndU = u1, EndV = v, SectionDxfId = "WALL B", ProfileId = "PROF-1" };

    /// <summary>현장과 같은 꼴 — B면 L1-R03C12·C13 과, 두 영역에서 위쪽 줄 3개를 모아 분리한 L1-R03C12-S1.</summary>
    private static async Task<AcsDbContext> SeedAsync(bool withOverlap = false)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(31.785, 10.1497, Math.PI / 4, 3.9776, 6.9097, Math.PI / 4, 2.8976, new[] { 0.0, 3.63, 6.63, 10.53 });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        if (withOverlap)   // 정렬 순서가 앞이고 작업을 품지만 분리의 원래 영역은 아닌 영역 — 원래 영역이 먼저여야 한다
            db.InspectionAreas.Add(new InspectionAreaEntity
            {
                AreaId = Overlap, TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-OVERLAP", SortOrder = 0,
                Corners = Rect(15.0, 3.9, 18.0, 4.1),
            });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = C12, TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-R03C12", SortOrder = 12,
            Corners = Rect(15.84, 2.894, 17.28, 4.034),
            Tasks = { Task(Guid.NewGuid(), 1, "H01", 15.84, 16.2, 2.924, "CROSS4"), Task(Guid.NewGuid(), 2, "H02", 16.2, 16.56, 2.924),
                      new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 9, Name = "V09", SeamType = "LINE", StartU = 16.02, StartV = 3.104, EndU = 16.02, EndV = 3.464 } },
        });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = C13, TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-R03C13", SortOrder = 13,
            Corners = Rect(17.28, 2.894, 18.72, 4.034),
            Tasks = { Task(Guid.NewGuid(), 6, "H06", 17.64, 18.0, 4.004) },
        });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = S1, TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-R03C12-S1", SortOrder = 40,
            Corners = Rect(16.51, 3.954, 17.69, 4.054),
            Tasks = { Task(H07, 1, "H01", 16.56, 16.92, 4.004), Task(H08, 2, "H02", 16.92, 17.28, 4.004, "CROSS4"), Task(H05, 3, "H03", 17.28, 17.64, 4.004) },
        });
        db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = "operator", Action = "AREA_SPLIT_TASKS", Target = S1.ToString(),
            Detail = JsonSerializer.Serialize(new { Name = "L1-R03C12-S1", sourceAreaIds = new[] { C12, C13 }, taskIds = new[] { H07, H08, H05 } }),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 분리_영역을_지우면_작업이_원래_영역으로_돌아간다(bool withOverlap)
    {
        await using var db = await SeedAsync(withOverlap);
        var r = await new AreaDeleteService(db).DeleteAsync(S1, deleteOrphanTasks: false, "tester");

        Assert.NotNull(r);
        Assert.Empty(r!.DeletedTasks);
        Assert.Equal(3, r.Moved.Count);
        db.ChangeTracker.Clear();
        Assert.False(await db.InspectionAreas.AnyAsync(a => a.AreaId == S1));

        // taskId 그대로, 위치에 맞는 원래 영역으로(겹치는 다른 영역이 있어도 분리의 원래 영역이 먼저)
        Assert.Equal(C12, (await db.AreaTasks.SingleAsync(t => t.TaskId == H07)).AreaId);
        Assert.Equal(C12, (await db.AreaTasks.SingleAsync(t => t.TaskId == H08)).AreaId);
        Assert.Equal(C13, (await db.AreaTasks.SingleAsync(t => t.TaskId == H05)).AreaId);

        // 받은 영역은 라인 순으로 다시 매김 — 가로 v 2.924 줄 → v 4.004 줄 → 세로
        var c12 = await db.AreaTasks.Where(t => t.AreaId == C12).OrderBy(t => t.Seq).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, c12.Select(t => t.Seq));
        Assert.Equal(new[] { "H01", "H02", "H03", "H04", "V05" }, c12.Select(t => t.Name));
        Assert.Equal(H07, c12[2].TaskId);
        Assert.Equal("CROSS4", c12[3].SeamType);
        var c13 = await db.AreaTasks.Where(t => t.AreaId == C13).OrderBy(t => t.Seq).ToListAsync();
        Assert.Equal(new[] { H05 }, c13.Take(1).Select(t => t.TaskId));
        Assert.Equal(new[] { "H01", "H02" }, c13.Select(t => t.Name));

        if (withOverlap) Assert.False(await db.AreaTasks.AnyAsync(t => t.AreaId == Overlap));
        Assert.True(await db.AuditLogs.AnyAsync(l => l.Action == "AREA_DELETE" && l.Target == S1.ToString()));
    }

    [Fact]
    public async Task 옮길_곳이_없는_작업이_있으면_거부하고_확인하면_함께_지운다()
    {
        await using var db = await SeedAsync();
        var lone = Guid.NewGuid();
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-LONE", SortOrder = 50,
            Corners = Rect(5.0, 5.0, 6.0, 6.0), Tasks = { Task(lone, 1, "H01", 5.2, 5.56, 5.5) },
        });
        await db.SaveChangesAsync();
        var areaId = (await db.InspectionAreas.SingleAsync(a => a.Name == "L1-LONE")).AreaId;
        db.ChangeTracker.Clear();

        var svc = new AreaDeleteService(db);
        var ex = await Assert.ThrowsAsync<AreaDeleteBlockedException>(() => svc.DeleteAsync(areaId, false, "tester"));
        Assert.Equal(new[] { "H01" }, ex.Orphans);
        db.ChangeTracker.Clear();
        Assert.True(await db.InspectionAreas.AnyAsync(a => a.AreaId == areaId));       // 아무것도 바뀌지 않음
        Assert.True(await db.AreaTasks.AnyAsync(t => t.TaskId == lone));

        var r = await new AreaDeleteService(db).DeleteAsync(areaId, true, "tester");
        Assert.Equal(new[] { "H01" }, r!.DeletedTasks);
        db.ChangeTracker.Clear();
        Assert.False(await db.InspectionAreas.AnyAsync(a => a.AreaId == areaId));
        Assert.False(await db.AreaTasks.AnyAsync(t => t.TaskId == lone));
    }

    [Fact]
    public async Task 작업_없는_영역과_없는_영역()
    {
        await using var db = await SeedAsync();
        var empty = Guid.NewGuid();
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = empty, TankId = "CT1", WallCode = "B", Level = 1, Name = "L1-EMPTY", SortOrder = 60, Corners = Rect(5.0, 5.0, 6.0, 6.0),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var svc = new AreaDeleteService(db);
        Assert.NotNull(await svc.DeleteAsync(empty, false, "tester"));
        Assert.Null(await svc.DeleteAsync(Guid.NewGuid(), false, "tester"));
    }
}
