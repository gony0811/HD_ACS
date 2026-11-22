using HD.Acs.App.Services;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static HD.Acs.App.Tests.SetCoverApplyTests;

namespace HD.Acs.App.Tests;

/// <summary>균등 격자 재분할 — 면·층 영역을 칸 크기로 다시 나눠 용접선을 묶는다(taskId 유지·영역 ID 물려주기·정차 지움·미리보기·거부).</summary>
public class AreaGridServiceTests
{
    private static AreaTaskEntity H(double u0, double u1, double v = 1.0) => new()
        { TaskId = Guid.NewGuid(), SeamType = "LINE", StartU = u0, EndU = u1, StartV = v, EndV = v };
    private static AreaTaskEntity Vt(double u, double v0, double v1) => new()
        { TaskId = Guid.NewGuid(), SeamType = "LINE", StartU = u, EndU = u, StartV = v0, EndV = v1 };

    [Fact]
    public void 칸_경계는_용접선_끝점에_맞추고_칸_크기를_넘지_않는다()
    {
        // 가로 용접선 0.36 m × 8칸(u 0~2.88), 세로 용접선 0.36 m × 4칸(v 0~1.44) — 면 3 × 3 m
        var tasks = Enumerable.Range(0, 8).Select(i => H(i * 0.36, (i + 1) * 0.36, 0.5))
            .Concat(Enumerable.Range(0, 4).Select(j => Vt(0.2, j * 0.36, (j + 1) * 0.36))).ToList();

        var (c144, left144) = AreaGridService.Cells(tasks, 3.0, 3.0, 1.44, 1.44);
        Assert.Empty(left144);
        Assert.Equal(new[] { (1, 1), (1, 2) }, c144.Keys.OrderBy(k => k).ToArray());   // u 0~1.44 / 1.44~2.88, v 0~1.44
        Assert.Equal(8, c144[(1, 1)].Count);                                            // 가로 4 + 세로 4
        Assert.Equal(4, c144[(1, 2)].Count);

        var (c108, left108) = AreaGridService.Cells(tasks, 3.0, 3.0, 1.08, 1.08);
        Assert.Empty(left108);
        Assert.Equal(new[] { (1, 1), (1, 2), (1, 3), (2, 1) }, c108.Keys.OrderBy(k => k).ToArray());   // u 3칸(1.08) · v 2칸
        Assert.Equal(12, c108.Values.Sum(l => l.Count));
        Assert.All(c108.Values, l => Assert.True(l.Max(t => Math.Max(t.StartU, t.EndU)) - l.Min(t => Math.Min(t.StartU, t.EndU)) <= 1.08 + 1e-9));

        // 칸 경계를 넘는 용접선은 따로 돌려준다
        var straddle = H(0.9, 1.26, 0.5);
        var (_, left) = AreaGridService.Cells(tasks.Append(straddle).ToList(), 3.0, 3.0, 1.08, 1.08);
        Assert.Contains(straddle, left);
    }

    [Fact]
    public async Task 격자로_다시_나누면_작업과_영역_ID를_이어가고_정차는_지운다()
    {
        await using var db = await SeedAsync();
        var a1 = await db.InspectionAreas.SingleAsync(a => a.AreaId == A1);
        (a1.StationX, a1.StationY, a1.StationTheta, a1.StationLiftMm) = (10.8, -8.0, 1.57, 300);   // set cover 가 넣어 둔 정차
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var pre = await new AreaGridService(db).PreviewAsync("CT1", 2, "SM", 1.08, null);
        Assert.Equal((3, 4, 8, 1, false), (pre.AreasBefore, pre.AreasAfter, pre.Tasks, pre.LeftoverAreas, pre.Busy));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "L2-R01C01", "L2-R01C02", "L2-R01C03" },                               // 미리보기는 DB 를 바꾸지 않는다
            await db.InspectionAreas.Where(a => a.Level == 2).Select(a => a.Name).OrderBy(n => n).ToListAsync());

        var (_, r) = await new AreaGridService(db).ApplyAsync("CT1", 2, "SM", 1.08, null, "tester");
        db.ChangeTracker.Clear();

        var areas = await db.InspectionAreas.Include(a => a.Tasks).Where(a => a.Level == 2 && a.WallCode == "SM").ToListAsync();
        // u 창(1.08 이하): 10.1~10.95 / 10.95~11.67 / 13.1~14.18 — T7(14.0~14.36)은 칸을 넘어 따로(G)
        Assert.Equal(new[] { "L2-G001", "L2-R01C02", "L2-R01C03", "L2-R01C05" }, areas.Select(a => a.Name).OrderBy(n => n));
        var c2 = areas.Single(a => a.Name == "L2-R01C02");
        Assert.Equal(A1, c2.AreaId);
        Assert.Equal(new[] { T[0], T[1] }, c2.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));
        Assert.Null(c2.StationX);                                       // 정차 덮어쓰기·리프트 지움 — 정차 최적화로 다시 정한다
        Assert.Null(c2.StationLiftMm);
        Assert.Equal(A2, areas.Single(a => a.Name == "L2-R01C03").AreaId);
        var c5 = areas.Single(a => a.Name == "L2-R01C05");
        Assert.Equal(A3, c5.AreaId);
        Assert.Equal(new[] { T[4], T[5], T[6] }, c5.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));
        var g = areas.Single(a => a.Name == "L2-G001");
        Assert.False(Assert.Single(g.Tasks).Enabled);                   // 임시 제외 표시는 그대로
        Assert.Equal(8, areas.Sum(a => a.Tasks.Count));
        Assert.Equal(1, r.TasksMoved);
        Assert.Equal(0, r.DeletedAreas);
        Assert.Contains(A2, await db.ScenarioAreas.Where(s => s.ScenarioId == Scenario).Select(s => s.AreaId).ToListAsync());
        Assert.Equal("L3-R01C01", (await db.InspectionAreas.SingleAsync(a => a.Level == 3)).Name);   // 다른 층은 그대로
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "AREA_GRID_REGROUP");
        Assert.Contains("cellUM", audit.Detail);

        // 1.44 로 다시 — T0~T3(10.1~11.67 = 1.57 m)은 한 칸에 못 들어가 둘로, 이름이 부딪치지 않는다
        var (pre2, _) = await new AreaGridService(db).ApplyAsync("CT1", 2, "SM", 1.44, null, "tester");
        Assert.Equal(8, pre2.Tasks);
        Assert.All(pre2.Cells, c => Assert.True(c.UMax - c.UMin <= 1.44 + 1e-9, c.Name));
    }

    [Fact]
    public async Task 칸_크기가_범위_밖이거나_진행_중_run_이면_거부한다()
    {
        await using var db = await SeedAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<AreaSplitException>(() => new AreaGridService(db).PreviewAsync("CT1", 2, "SM", 1.6, null))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<AreaSplitException>(() => new AreaGridService(db).PreviewAsync("CT1", 2, "SM", 1.08, 3.5))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<AreaSplitException>(() => new AreaGridService(db).PreviewAsync("CT1", 4, "SM", 1.08, null))).Status);   // 용접선 없음
        Assert.Equal(404, (await Assert.ThrowsAsync<AreaSplitException>(() => new AreaGridService(db).PreviewAsync("CT1", 2, "ZZ", 1.08, null))).Status);

        var run = Guid.NewGuid();
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = run, RobotId = "AMR-01", State = "RUNNING", StartedAt = DateTimeOffset.UtcNow });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = Guid.NewGuid(), RunId = run, AreaId = A3, MapId = "CT1-L2", Status = "PENDING", Actions = "[]" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True((await new AreaGridService(db).PreviewAsync("CT1", 2, "SM", 1.08, null)).Busy);
        db.ChangeTracker.Clear();
        Assert.Equal(409, (await Assert.ThrowsAsync<AreaSplitException>(() => new AreaGridService(db).ApplyAsync("CT1", 2, "SM", 1.08, null, "tester"))).Status);
    }
}
