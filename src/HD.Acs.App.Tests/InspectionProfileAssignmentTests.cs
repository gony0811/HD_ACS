using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// HD_AMR 프로필 지정표 — 유형·면·방향마다 어느 프로필로 순회하는지 받아 두고, 리치 시뮬레이터가 용접선마다 그 프로필(6축 경유점)로 판정한다.
/// </summary>
public class InspectionProfileAssignmentTests
{
    private static AcsDbContext NewDb() => new(new DbContextOptionsBuilder<AcsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProfileWaypoint P(double x, double theta, double z, double y = 0, double rz = 0) => new(x, y, z, theta, rz, 0, 0);

    private static ProfileUpload Up(int id, string name, string seamType, params ProfileWaypoint[] wps) =>
        new(id, name, seamType, true, null, wps);

    private static readonly ProfileUpload Line = Up(18, "LINE-A", "LINE", P(0, 0, 33.8), P(180, 0, 0), P(360, 0, 33.8));
    private static readonly ProfileUpload Cross = Up(9, "CROSS-A", "CROSS", P(0, 0, 33.8), P(360, 0, 33.8), P(205, 0, 29.8, y: -195, rz: -90));

    private static ProfileSyncRequest Req(IReadOnlyList<ProfileAssignment>? table, params ProfileUpload[] items) =>
        new("AMR-01", true, items, table);

    [Fact]
    public async Task 지정표를_통째로_바꾸고_같은_표를_다시_받으면_그대로다()
    {
        var db = NewDb();
        var table = new[] { new ProfileAssignment("LINE", "F", "H", 18), new ProfileAssignment("cross", "f", "h", 9) };   // 소문자·옛 이름도 받는다

        var first = await InspectionProfileService.SyncAsync(db, Req(table, Line, Cross));
        Assert.True(first.AssignmentsChanged);
        Assert.Equal(2, first.Assignments);
        Assert.Contains(await db.InspectionProfileAssignments.ToListAsync(), a => a is { SeamType: "CROSS4", WallCode: "F", Direction: "H", AmrProfileId: 9 });

        var again = await InspectionProfileService.SyncAsync(db, Req(table, Line, Cross));
        Assert.False(again.AssignmentsChanged);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "INSPECTION_PROFILE_SYNC"));

        var replaced = await InspectionProfileService.SyncAsync(db, Req(new[] { new ProfileAssignment("LINE", "F", "H", 9) }, Line, Cross));
        Assert.True(replaced.AssignmentsChanged);
        Assert.Equal(9, (await db.InspectionProfileAssignments.SingleAsync()).AmrProfileId);

        // 지정표를 싣지 않은 요청(구버전 AMR)은 표를 건드리지 않는다
        await InspectionProfileService.SyncAsync(db, Req(null, Line, Cross));
        Assert.Equal(1, await db.InspectionProfileAssignments.CountAsync());
    }

    /// <summary>CROSS4_T1·CROSS4_T2 [VDA 1.22] — 그 이름의 지정이 있으면 그것, 없으면 접미 없는 CROSS4 지정으로.</summary>
    [Fact]
    public async Task CROSS4_좌우는_자기_지정을_먼저_없으면_CROSS4_지정을_쓴다()
    {
        var db = NewDb();
        await InspectionProfileService.SyncAsync(db, Req(new[]
        {
            new ProfileAssignment("CROSS4", "F", "H", 9), new ProfileAssignment("CROSS4_T1", "F", "H", 18),
        }, Line, Cross));
        var profileOf = await InspectionProfileService.LoadSeamProfilesAsync(db);
        Assert.Equal("LINE-A", profileOf("CROSS4_T1", "F", "H")?.Name);    // 자기 지정
        Assert.Equal("CROSS-A", profileOf("cross4_t2", "F", "H")?.Name);   // 지정 없음 → CROSS4
        Assert.Equal("CROSS-A", profileOf("CROSS4", "F", "H")?.Name);
        Assert.Null(profileOf("CROSS4_T2", "A", "H"));                      // 그 면엔 CROSS4 지정도 없음
    }

    [Theory]
    [InlineData("LINE", "F", "X")]    // 방향이 H·V 가 아님
    [InlineData("LINE", " ", "H")]    // 면 없음
    [InlineData(null, "F", "H")]      // 유형 없음
    public async Task 잘못된_지정표는_거부한다(string? seamType, string wall, string dir)
    {
        var db = NewDb();
        await Assert.ThrowsAsync<ProfileSyncException>(() =>
            InspectionProfileService.SyncAsync(db, Req(new[] { new ProfileAssignment(seamType, wall, dir, 18) }, Line)));
        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, Req(
            new[] { new ProfileAssignment("LINE", "F", "H", 18), new ProfileAssignment("LINE", "F", "H", 9) }, Line)));   // 같은 조합 두 번
        Assert.Equal(0, await db.InspectionProfiles.CountAsync());
    }

    [Fact]
    public async Task 유형_면_방향으로_프로필을_찾는다()
    {
        var db = NewDb();
        var relative = new ProfileUpload(3, "REL", "LINE", false, null, new[] { P(0, 0, 33.8), P(360, 0, 33.8) });
        await InspectionProfileService.SyncAsync(db, Req(new[]
        {
            new ProfileAssignment("LINE", "F", "H", 18), new ProfileAssignment("CROSS4", "F", "H", 9),
            new ProfileAssignment("LINE", "PU", "V", 3),      // 절대 자세가 아닌 프로필
            new ProfileAssignment("LINE", "T", "H", 77),      // 받은 적 없는 프로필 번호
        }, Line, Cross, relative));
        var find = await InspectionProfileService.LoadSeamProfilesAsync(db);

        Assert.Equal(("LINE-A", 1, 3), (find("LINE", "F", "H")!.Name, find("line", "f", "H")!.Version, find("LINE", "F", "H")!.Waypoints.Count));
        var cross = find("CROSS", "F", "H")!;   // 옛 이름 CROSS = CROSS4
        Assert.Equal(new ScanWaypoint(205, -195, 29.8, 0, 0, -90), cross.Waypoints[2]);   // Theta → Ry
        Assert.Null(find("LINE", "F", "V"));      // 지정 없음
        Assert.Null(find("LINE", "PU", "V"));     // 절대 자세 아님
        Assert.Null(find("LINE", "T", "H"));      // 프로필 없음
    }

    [Theory]
    [InlineData(1.0, 2.0, 1.36, 2.0, "H")]
    [InlineData(1.0, 2.0, 1.0, 2.36, "V")]
    [InlineData(1.0, 2.0, 1.0, 2.0, "H")]   // 점 하나(교차 중심만 있는 작업)
    public void 진행_방향은_면_좌표에서_더_많이_변하는_축이다(double su, double sv, double eu, double ev, string expected) =>
        Assert.Equal(expected, InspectionProfileService.DirectionOf(su, sv, eu, ev));

    // ── 시뮬레이터 연결 ──

    private static async Task<(AcsDbContext Db, InspectionAreaEntity Area)> SeedTank()
    {
        var db = NewDb();
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        var area = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.6,
            Tasks =
            {
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.0, StartV = 1.8, EndU = 3.36, EndV = 1.8, SeamType = "LINE", SectionDxfId = "D", ProfileId = "P" },
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 2, StartU = 3.0, StartV = 1.8, EndU = 3.36, EndV = 1.8, SeamType = "CROSS4", SectionDxfId = "D", ProfileId = "P" },
            },
        };
        db.InspectionAreas.Add(area);
        await db.SaveChangesAsync();
        return (db, area);
    }

    private static ReachSimService Sim(AcsDbContext db, bool useAmr = true)
    {
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return new ReachSimService(db, dispatcher, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Acs:Reach:MountYawDeg"] = "180", ["Acs:Reach:ToolZMm"] = "365", ["Acs:Reach:ScanStandoffMm"] = "43.8",
            ["Acs:Reach:FocusStandoffMm"] = "10", ["Acs:Reach:UseAmrProfiles"] = useAmr ? "true" : "false",
        }).Build());
    }

    [Fact]
    public async Task 용접선마다_지정된_프로필로_순회를_판정한다()
    {
        var (db, area) = await SeedTank();
        // CROSS4 프로필의 마지막 경유점은 일부러 닿지 않는 곳(옆으로 3 m) — 같은 자리의 LINE 용접선은 그대로 닿아야 한다
        var farCross = Up(9, "CROSS-FAR", "CROSS", P(0, 0, 33.8), P(360, 0, 33.8), P(205, 0, 29.8, y: 3000, rz: -90));
        await InspectionProfileService.SyncAsync(db, Req(new[]
        {
            new ProfileAssignment("LINE", "F", "H", 18), new ProfileAssignment("CROSS4", "F", "H", 9),
        }, Line, farCross));

        var seams = (await Sim(db).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.OrderBy(s => s.Seq).ToList();
        Assert.NotEqual("Unreachable", seams[0].Verdict);                 // LINE — 지정된 직선 프로필
        Assert.Equal("Unreachable", seams[1].Verdict);                    // CROSS4 — 지정된 교차 프로필의 경유점 #3 에서 막힌다
        Assert.Contains("경유점 #3", seams[1].Blocker);

        // 받은 프로필을 쓰지 않으면(설정 끔) 두 용접선 모두 종전 판정(같은 자리라 결과가 같다)
        var off = (await Sim(db, useAmr: false).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.OrderBy(s => s.Seq).ToList();
        Assert.Equal(off[0].Verdict, off[1].Verdict);
        Assert.DoesNotContain("경유점 #", off[1].Blocker ?? "");
        _ = area;
    }
}
