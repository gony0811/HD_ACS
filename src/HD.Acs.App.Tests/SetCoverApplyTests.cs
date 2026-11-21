using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>정차 묶음 최적화 결과 적용 — 면·층 영역을 제안 정차대로 다시 구성(taskId 유지·영역 ID 물려주기·X 영역·시나리오·거부).</summary>
public class SetCoverApplyTests
{
    internal static readonly Guid A1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    internal static readonly Guid A2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    internal static readonly Guid A3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    internal static readonly Guid Scenario = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    internal static readonly Guid[] T = Enumerable.Range(1, 9).Select(i => Guid.Parse($"cccccccc-0000-0000-0000-00000000000{i}")).ToArray();
    private static readonly double V = 3.63 + 1.5 - 3.9776;   // L2 SM 수직벽 v

    /// <summary>L2 SM — 영역 3개(A1: T0·T1, A2: T2·T3, A3: T4·T5·T6), 가로 용접선 한 줄 + T7(임시 제외) + T8(다른 층 — 건드리지 않음).</summary>
    internal static async Task<AcsDbContext> SeedAsync()
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
        AreaTaskEntity Seam(int i, double u, int seq, bool on = true) => new()
            { TaskId = T[i], Seq = seq, Name = $"H{seq:00}", SeamType = "LINE", StartU = u, StartV = V, EndU = u + 0.36, EndV = V, Enabled = on };
        InspectionAreaEntity Area(Guid id, string name, double u0, double u1, params AreaTaskEntity[] tasks)
        {
            var a = new InspectionAreaEntity
            {
                AreaId = id, TankId = "CT1", WallCode = "SM", Level = 2, Name = name, SortOrder = 1,
                Corners = JsonSerializer.Serialize(new[] { new[] { u0, V - 0.3 }, new[] { u1, V - 0.3 }, new[] { u1, V + 0.3 }, new[] { u0, V + 0.3 } }),
                UMin = u0, UMax = u1, VMin = V - 0.3, VMax = V + 0.3,
            };
            foreach (var t in tasks) a.Tasks.Add(t);
            return a;
        }
        db.InspectionAreas.Add(Area(A1, "L2-R01C01", 10.0, 10.9, Seam(0, 10.1, 1), Seam(1, 10.46, 2)));
        db.InspectionAreas.Add(Area(A2, "L2-R01C02", 10.9, 11.8, Seam(2, 10.95, 1), Seam(3, 11.31, 2)));
        db.InspectionAreas.Add(Area(A3, "L2-R01C03", 13.0, 14.4, Seam(4, 13.1, 1), Seam(5, 13.46, 2), Seam(6, 13.82, 3), Seam(7, 14.0, 4, on: false)));
        var vUp = 6.63 + 1.0 - 3.9776;
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "SM", Level = 3, Name = "L3-R01C01", SortOrder = 2,
            Corners = JsonSerializer.Serialize(new[] { new[] { 10.0, vUp - 0.3 }, new[] { 11.0, vUp - 0.3 }, new[] { 11.0, vUp + 0.3 }, new[] { 10.0, vUp + 0.3 } }),
            Tasks = { new AreaTaskEntity { TaskId = T[8], Seq = 1, Name = "H01", SeamType = "LINE", StartU = 10.1, StartV = vUp, EndU = 10.46, EndV = vUp } },
        });
        db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = Scenario, AreaId = A2, SortOrder = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static ReachWeldVerdict W(Guid id) => new(id, 1, 0, 0, 0, 0, true);

    /// <summary>제안: 정차 1이 T0~T3(u 10.1~11.67 — 1.44 m 를 조금 넘지 않게 T0..T3 은 1.57 m 라 둘로), 정차 2가 T4·T5, T6 못 닿음.</summary>
    private static ReachSetCoverPlan Plan() => new("CT1", 2, "SM", 7, 6, 0, 3, 4, new List<ReachSetCoverStation>
    {
        new(1, 10.8, -8.0, 1.57, 0, "Safe", 12, new[] { 10.1, V, 11.54, V }, new List<Guid> { T[0], T[1], T[2] }, new() { W(T[0]), W(T[1]), W(T[2]) }),
        new(2, 11.6, -8.0, 1.57, 300, "Safe", 11, new[] { 11.31, V, 12.75, V }, new List<Guid> { T[3] }, new() { W(T[3]) }),
        new(3, 13.5, -8.0, 1.57, 0, "Clear", 6, new[] { 13.1, V, 14.54, V }, new List<Guid> { T[4], T[5] }, new() { W(T[4]), W(T[5]) }),
    }, new List<Guid> { T[6] }, 10, 100, 1000, SetCoverOptions.Default);

    [Fact]
    public async Task 제안대로_영역을_다시_구성하고_작업과_영역_ID를_이어간다()
    {
        await using var db = await SeedAsync();
        var r = await new SetCoverApplyService(db).ApplyAsync(Plan(), "tester");
        db.ChangeTracker.Clear();

        var areas = await db.InspectionAreas.Include(a => a.Tasks).Where(a => a.Level == 2 && a.WallCode == "SM").ToListAsync();
        Assert.Equal(4, areas.Count);                                   // 정차 3 + X 1(T6 못 닿음 + T7 임시 제외)
        Assert.Equal(new[] { "L2-S001", "L2-S002", "L2-S003", "L2-X001" }, areas.Select(a => a.Name).OrderBy(n => n));
        var s1 = areas.Single(a => a.Name == "L2-S001");
        Assert.Equal(A1, s1.AreaId);                                    // T0·T1 이 있던 A1 을 물려받음
        Assert.Equal(new[] { T[0], T[1], T[2] }, s1.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));
        Assert.Equal(new[] { "H01", "H02", "H03" }, s1.Tasks.OrderBy(t => t.Seq).Select(t => t.Name));
        Assert.Equal((10.8, -8.0, 1.57, 0.0), (s1.StationX!.Value, s1.StationY!.Value, s1.StationTheta!.Value, s1.StationLiftMm!.Value));
        var s2 = areas.Single(a => a.Name == "L2-S002");
        Assert.Equal(A2, s2.AreaId);                                    // A2 는 T2 를 S001 에 넘겼지만 T3 으로 이어진다
        Assert.Equal(300, s2.StationLiftMm);
        var s3 = areas.Single(a => a.Name == "L2-S003");
        Assert.Equal(A3, s3.AreaId);
        var x = areas.Single(a => a.Name == "L2-X001");
        Assert.Null(x.StationX);                                        // X 영역 = 정차 덮어쓰기 없음
        Assert.Equal(new[] { T[6], T[7] }, x.Tasks.OrderBy(t => t.Seq).Select(t => t.TaskId));
        Assert.False(x.Tasks.Single(t => t.TaskId == T[7]).Enabled);   // 임시 제외 그대로
        Assert.All(areas, a => Assert.True(a.UMax - a.UMin <= AreaGeometry.MaxSpanM + 1e-9, $"{a.Name} {a.UMin}~{a.UMax}"));

        // 모든 작업이 정확히 한 번, 다른 층은 그대로
        Assert.Equal(8, areas.Sum(a => a.Tasks.Count));
        var t8 = await db.AreaTasks.SingleAsync(t => t.TaskId == T[8]);
        Assert.Equal("L3-R01C01", (await db.InspectionAreas.SingleAsync(a => a.AreaId == t8.AreaId)).Name);
        // A2 에 연결돼 있던 시나리오 — T2 를 받은 S001 도 연결
        var linked = await db.ScenarioAreas.Where(s => s.ScenarioId == Scenario).Select(s => s.AreaId).ToListAsync();
        Assert.Contains(A2, linked);
        Assert.Contains(A1, linked);
        Assert.Equal(1, r.ScenarioLinksAdded);
        Assert.Equal(0, r.DeletedAreas);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "SET_COVER_APPLY");
        Assert.Contains("L2-R01C02", audit.Detail);                    // 적용 전 배치(되돌리기 자료)
    }

    [Fact]
    public async Task 계산_뒤_작업이_바뀌었으면_거부하고_DB_는_그대로()
    {
        await using var db = await SeedAsync();
        var plan = Plan() with { Uncovered = new List<Guid>() };        // T6 이 빠짐 = 계산 뒤 바뀐 것과 같다
        var ex = await Assert.ThrowsAsync<AreaSplitException>(() => new SetCoverApplyService(db).ApplyAsync(plan, "tester"));
        Assert.Equal(409, ex.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "L2-R01C01", "L2-R01C02", "L2-R01C03" },
            await db.InspectionAreas.Where(a => a.Level == 2).Select(a => a.Name).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task 진행_중_run_에_있으면_거부한다()
    {
        await using var db = await SeedAsync();
        var run = Guid.NewGuid();
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = run, RobotId = "AMR-01", State = "RUNNING", StartedAt = DateTimeOffset.UtcNow });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = Guid.NewGuid(), RunId = run, AreaId = A3, MapId = "CT1-L2", Status = "PENDING", Actions = "[]" });
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<AreaSplitException>(() => new SetCoverApplyService(db).ApplyAsync(Plan(), "tester"));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task 같은_결과를_다시_적용해도_이름이_부딪치지_않는다()
    {
        await using var db = await SeedAsync();
        await new SetCoverApplyService(db).ApplyAsync(Plan(), "tester");
        db.ChangeTracker.Clear();
        var r = await new SetCoverApplyService(db).ApplyAsync(Plan(), "tester");   // 이름 L2-S001… 이 이미 있는 상태
        Assert.Equal(4, r.Areas.Count);
        Assert.All(r.Areas, a => Assert.True(a.Inherited));
    }
}
