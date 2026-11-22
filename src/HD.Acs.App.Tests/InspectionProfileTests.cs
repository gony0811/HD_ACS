using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>HD_AMR 검사 프로필 사본 — 받은 목록을 저장하고, 내용이 바뀐 것만 버전을 올린다. 직선 프로필은 리치 시뮬레이터가 쓴다.</summary>
public class InspectionProfileTests
{
    private static AcsDbContext NewDb() => new(new DbContextOptionsBuilder<AcsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProfileWaypoint P(double x, double theta, double z, double y = 0, double rz = 0, double rx = 0) =>
        new(x, y, z, theta, rz, rx, 0);

    private static ProfileUpload Line(int id, string name, params ProfileWaypoint[] wps) =>
        new(id, name, "LINE", true, DateTimeOffset.Parse("2026-11-09T06:26:03Z"), wps);

    private static ProfileSyncRequest Req(bool full, params ProfileUpload[] items) => new("AMR-01", full, items);

    [Fact]
    public async Task 처음_받으면_버전_1로_저장하고_같은_내용을_다시_받으면_그대로다()
    {
        var db = NewDb();
        var line = Line(18, "LINE-A", P(0, 0, 33.8), P(180, 0, 0), P(360, 0, 33.8));

        var first = await InspectionProfileService.SyncAsync(db, Req(true, line));
        Assert.Equal((1, 0, 0, 0), (first.Added, first.Changed, first.Unchanged, first.Removed));

        var again = await InspectionProfileService.SyncAsync(db, Req(true, line));
        Assert.Equal((0, 0, 1, 0), (again.Added, again.Changed, again.Unchanged, again.Removed));

        var e = await db.InspectionProfiles.SingleAsync();
        Assert.Equal(("AMR-01", 18, "LINE-A", 1, 3), (e.Source, e.AmrProfileId, e.Name, e.Version, e.PointCount));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "INSPECTION_PROFILE_SYNC"));   // 바뀐 것이 없으면 기록하지 않는다
    }

    [Fact]
    public async Task 경유점이_바뀌면_버전이_오르고_이름만_바뀌면_오르지_않는다()
    {
        var db = NewDb();
        await InspectionProfileService.SyncAsync(db, Req(true, Line(18, "LINE-A", P(0, 0, 33.8), P(360, 0, 33.8))));

        var edited = await InspectionProfileService.SyncAsync(db, Req(true, Line(18, "LINE-A", P(0, 0, 33.8), P(360, -10, 33.8))));
        Assert.Equal(1, edited.Changed);
        Assert.Equal(2, (await db.InspectionProfiles.SingleAsync()).Version);

        var renamed = await InspectionProfileService.SyncAsync(db, Req(true, Line(18, "LINE-B", P(0, 0, 33.8), P(360, -10, 33.8))));
        Assert.Equal(1, renamed.Changed);
        var e = await db.InspectionProfiles.SingleAsync();
        Assert.Equal(("LINE-B", 2), (e.Name, e.Version));
    }

    [Fact]
    public async Task 전체_목록에_없는_프로필은_삭제_표시하고_다시_오면_되살린다()
    {
        var db = NewDb();
        var a = Line(1, "A", P(0, 0, 0), P(100, 0, 0));
        var b = Line(2, "B", P(0, 0, 0), P(200, 0, 0));
        await InspectionProfileService.SyncAsync(db, Req(true, a, b));

        // 일부만 보내는 요청(full = false)은 나머지를 건드리지 않는다
        var partial = await InspectionProfileService.SyncAsync(db, Req(false, a));
        Assert.Equal(0, partial.Removed);
        Assert.False((await db.InspectionProfiles.SingleAsync(x => x.AmrProfileId == 2)).Deleted);

        var full = await InspectionProfileService.SyncAsync(db, Req(true, a));
        Assert.Equal(1, full.Removed);
        Assert.True((await db.InspectionProfiles.SingleAsync(x => x.AmrProfileId == 2)).Deleted);
        Assert.Null(await InspectionProfileService.FindByNameAsync(db, "B"));

        var back = await InspectionProfileService.SyncAsync(db, Req(true, a, b));
        Assert.Equal(1, back.Changed);
        var revived = await db.InspectionProfiles.SingleAsync(x => x.AmrProfileId == 2);
        Assert.False(revived.Deleted);
        Assert.Equal(1, revived.Version);   // 내용은 그대로라 버전도 그대로
    }

    [Fact]
    public async Task 잘못된_요청은_거부하고_아무것도_저장하지_않는다()
    {
        var db = NewDb();
        var ok = Line(1, "A", P(0, 0, 0), P(100, 0, 0));

        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, new ProfileSyncRequest(" ", true, new[] { ok })));
        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, Req(true, ok, ok)));                       // id 중복
        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, Req(true, Line(2, " ", P(0, 0, 0)))));    // 이름 없음
        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, Req(true, ok, Line(2, "B", P(double.NaN, 0, 0)))));

        Assert.Equal(0, await db.InspectionProfiles.CountAsync());
    }

    [Fact]
    public async Task 직선_프로필만_시뮬레이터_순회_프로필로_바뀐다()
    {
        var db = NewDb();
        var cross = new ProfileUpload(9, "CROSS-4", "CROSS", true, null, new[] { P(0, 0, 33.8), P(205, 0, 29.8, y: -195, rz: -90) });
        var relative = new ProfileUpload(3, "REL", "LINE", false, null, new[] { P(0, 0, 33.8), P(360, 0, 33.8) });
        var tilted = Line(4, "RX", P(0, 0, 33.8), P(360, 0, 33.8, rx: 7));
        await InspectionProfileService.SyncAsync(db, Req(true, Line(18, "LINE-A", P(0, 0, 33.8), P(4.91, 10, 33.37), P(360, 0, 33.8)), cross, relative, tilted));

        var (tilt, reason) = InspectionProfileService.ToTiltProfile((await InspectionProfileService.FindByNameAsync(db, "LINE-A"))!);
        Assert.Null(reason);
        Assert.Equal(new[] { 4.91, 10, 33.37 }, tilt![1]);   // [거리 mm, 기울기 도, Z mm]

        foreach (var name in new[] { "CROSS-4", "REL", "RX" })
        {
            var (t, why) = InspectionProfileService.ToTiltProfile((await InspectionProfileService.FindByNameAsync(db, name))!);
            Assert.Null(t);
            Assert.False(string.IsNullOrEmpty(why));
        }
    }

    [Fact]
    public void 내용_지문은_값이_같으면_같고_한_값만_달라도_다르다()
    {
        var a = new[] { P(0, 0, 33.8), P(360, 0, 33.8) };
        var same = new[] { P(0, 0, 33.8), P(360, 0, 33.8) };
        Assert.Equal(InspectionProfileService.ContentHash("LINE", true, a), InspectionProfileService.ContentHash("LINE", true, same));
        Assert.NotEqual(InspectionProfileService.ContentHash("LINE", true, a), InspectionProfileService.ContentHash("LINE", false, a));
        Assert.NotEqual(InspectionProfileService.ContentHash("LINE", true, a),
            InspectionProfileService.ContentHash("LINE", true, new[] { P(0, 0, 33.8), P(360, 0, 33.8, rz: -90) }));
    }

    [Theory]
    [InlineData("LINE-A", "AMR LINE-A v1")]   // 받은 직선 프로필
    [InlineData("CROSS-4", "설정")]            // 직선이 아니면 설정값 그대로
    [InlineData("없는-이름", "설정")]          // 받은 적 없는 이름
    [InlineData(null, "설정")]                 // 이름을 정하지 않음
    public async Task 시뮬레이터는_설정한_이름의_받은_직선_프로필을_쓴다(string? name, string expected)
    {
        var db = NewDb();
        await InspectionProfileService.SyncAsync(db, Req(true,
            Line(18, "LINE-A", P(0, 0, 33.8), P(180, 0, 0), P(360, 0, 33.8)),
            new ProfileUpload(9, "CROSS-4", "CROSS", true, null, new[] { P(0, 0, 33.8), P(205, 0, 29.8, y: -195, rz: -90) })));
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var sim = new ReachSimService(db, dispatcher, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Acs:Reach:ScanProfileName"] = name }).Build());

        Assert.Null(await sim.SurveyAsync("없는 선창", null, null, CancellationToken.None));   // 선창이 없어도 기준 모델은 정해진다
        Assert.Equal(expected, sim.ScanProfileSource);
    }
}
