using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>CROSS4 정면 좌우 판정·적용 [VDA §8.2 · 1.22] — 미리보기는 DB 를 바꾸지 않고, 적용은 바뀐 작업만 저장한다.</summary>
public class Cross4SideServiceTests
{
    private static async Task<(Cross4SideService Svc, AcsDbContext Db, Dictionary<string, Guid> Ids)> BuildAsync()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geometry = new TankGeometryService(db, NullLogger<TankGeometryService>.Instance);
        await geometry.RegisterAsync("CT1", new TankGeometry(30, 10, Math.PI / 4, 3, 8, Math.PI / 4, 2, new[] { 0.0, 3.2, 6.4, 9.6 }),
            null, null, null, "tester");

        var ids = new Dictionary<string, Guid>();
        AreaTaskEntity Task(string key, Guid area, int seq, string type, double su, double sv, double eu, double ev, string? points = null)
        {
            var t = new AreaTaskEntity { TaskId = Guid.NewGuid(), AreaId = area, Seq = seq, SeamType = type,
                StartU = su, StartV = sv, EndU = eu, EndV = ev, Points = points, SectionDxfId = "D", ProfileId = "P" };
            ids[key] = t.TaskId;
            return t;
        }
        Guid Area(string wall, string name)
        {
            var a = new InspectionAreaEntity { AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = wall, Level = 2, Name = name,
                Corners = "[[0,0],[5,0],[5,5],[0,5]]", UMax = 5, VMax = 5 };
            db.InspectionAreas.Add(a);
            return a.AreaId;
        }
        var pm = Area("PM", "PM-1");
        var sm = Area("SM", "SM-1");
        var tt = Area("T", "T-1");
        db.AreaTasks.AddRange(
            // 좌현: 수직선 u 1.20 이 가운데(1.18)보다 선수 쪽 → 좌현 벽 정면에서 오른쪽 = R
            Task("pmLine", pm, 1, "LINE", 1.20, 2.18, 1.20, 2.54),
            Task("pmCross", pm, 2, "CROSS4", 1.0, 2.0, 1.36, 2.0),
            // 우현: 같은 배치 → 우현 벽 정면에서 선수 쪽은 왼쪽 = L
            Task("smLine", sm, 1, "LINE", 1.20, 2.18, 1.20, 2.54),
            Task("smCross", sm, 2, "CROSS4", 1.0, 2.0, 1.36, 2.0),
            // 판정 불가: 세로 작업, 가지를 그린 교차, 수직선 없음, 천장, 수직선이 정가운데(CT1 도면)
            Task("pmVert", pm, 3, "CROSS4", 3.0, 1.0, 3.0, 1.36),
            Task("pmArms", pm, 4, "CROSS4", 4.0, 3.0, 4.0, 3.0, "[[4.2,3],[4,3.2],[3.8,3],[4,2.8]]"),
            Task("pmAlone", pm, 5, "CROSS4", 2.0, 4.0, 2.36, 4.0),
            Task("tCross", tt, 1, "CROSS4", 1.0, 1.0, 1.36, 1.0),
            Task("pmMidLine", pm, 7, "LINE", 4.18, 1.18, 4.18, 1.54),
            Task("pmMid", pm, 8, "CROSS4", 4.0, 1.0, 4.36, 1.0),
            // 이미 정한 값은 overwrite 가 아니면 건드리지 않는다
            Task("pmFixed", pm, 6, "CROSS4_L", 1.36, 0.5, 1.72, 0.5));
        await db.SaveChangesAsync();
        return (new Cross4SideService(db, NullLogger<Cross4SideService>.Instance), db, ids);
    }

    [Fact]
    public async Task Preview_ThenApply()
    {
        var (svc, db, ids) = await BuildAsync();

        var preview = await svc.ClassifyAsync("CT1", apply: false, overwrite: false, "tester");
        Assert.False(preview.Applied);
        Assert.Equal(7, preview.Total);                          // 접미 없는 CROSS4 만(이미 L 인 작업 제외)
        Assert.Equal(Cross4Side.Right, preview.Rows.Single(r => r.TaskId == ids["pmCross"]).After);
        Assert.Equal(Cross4Side.Left, preview.Rows.Single(r => r.TaskId == ids["smCross"]).After);
        Assert.Equal("→", preview.Rows.Single(r => r.TaskId == ids["pmCross"]).StartToEnd);   // 좌현: 시작→끝이 정면에서 오른쪽으로
        Assert.Equal("←", preview.Rows.Single(r => r.TaskId == ids["smCross"]).StartToEnd);   // 우현: 왼쪽으로
        foreach (var k in new[] { "pmVert", "pmArms", "pmAlone", "tCross", "pmMid" })
            Assert.NotNull(preview.Rows.Single(r => r.TaskId == ids[k]).Reason);
        Assert.Equal((1, 1, 5, 2), (preview.Left, preview.Right, preview.Undecided, preview.Changed));
        Assert.All(await db.AreaTasks.AsNoTracking().Where(t => t.SeamType.StartsWith("CROSS4")).ToListAsync(),
            t => Assert.True(t.SeamType == "CROSS4" || t.TaskId == ids["pmFixed"]));   // 미리보기는 저장하지 않는다

        var applied = await svc.ClassifyAsync("CT1", apply: true, overwrite: false, "tester");
        Assert.True(applied.Applied);
        var types = await db.AreaTasks.AsNoTracking().ToDictionaryAsync(t => t.TaskId, t => t.SeamType);
        Assert.Equal("CROSS4_R", types[ids["pmCross"]]);
        Assert.Equal("CROSS4_L", types[ids["smCross"]]);
        Assert.Equal("CROSS4", types[ids["pmVert"]]);
        Assert.Equal("CROSS4_L", types[ids["pmFixed"]]);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.Action == "CROSS4_SIDE_APPLY");

        // 다시 돌리면 정할 것이 남지 않는다(판정 불가 작업만)
        var again = await svc.ClassifyAsync("CT1", apply: true, overwrite: false, "tester");
        Assert.Equal(0, again.Changed);
        Assert.Equal(5, again.Total);
    }

    [Fact]
    public async Task Overwrite_ReclassifiesExistingSides()
    {
        var (svc, db, ids) = await BuildAsync();
        // pmFixed(L)는 가로지르는 수직선이 없어 판정 불가 → overwrite 여도 그대로
        var r = await svc.ClassifyAsync("CT1", apply: true, overwrite: true, "tester");
        Assert.Equal(8, r.Total);
        Assert.Null(r.Rows.Single(x => x.TaskId == ids["pmFixed"]).After);
        Assert.Equal("CROSS4_L", (await db.AreaTasks.FindAsync(ids["pmFixed"]))!.SeamType);
    }

    [Fact]
    public async Task UnknownTank_Throws()
    {
        var (svc, _, _) = await BuildAsync();
        await Assert.ThrowsAsync<Cross4SideService.NotFoundException>(() => svc.ClassifyAsync("XX", false, false, null));
    }
}
