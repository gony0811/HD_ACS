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
/// HD_AMR 교시 위치 사본 — 프로필 전송에 함께 온 홈 위치_2·작업 준비 위치·면별 출발 자세를 저장하고, 리치 시뮬레이터가
/// 설정(<c>Acs:Reach:StartJoints</c>)보다 먼저 쓴다. 2026-11-19 리프트 탈거 뒤 홈 위치_2 재교시가 설정에 반영되지 않았던 사례.
/// </summary>
public class AmrTeachingTests
{
    private static readonly double[] OldHome2 = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
    private static readonly double[] NewHome2 = { -0.414, -11.992, 121.972, -110.169, 89.975, 180.392 };
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    private static AcsDbContext NewDb() => new(new DbContextOptionsBuilder<AcsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProfileSyncRequest Req(IReadOnlyList<TeachingJointsUpload>? positions,
        IReadOnlyDictionary<string, double[]>? starts = null, string source = "AMR-01") =>
        new(source, false, Array.Empty<ProfileUpload>(), TeachingPositions: positions, StartJointsByWall: starts);

    [Fact]
    public void 면별_출발_자세가_없으면_교시_위치에서_만든다()
    {
        var (positions, starts) = AmrTeachingService.Normalize(
            new[] { new TeachingJointsUpload(" home2 ", NewHome2), new TeachingJointsUpload("ready", Ready) }, null)!.Value;

        Assert.Equal(new[] { "home2", "ready" }, positions.Select(p => p.Key));
        foreach (var w in new[] { "T", "SU", "PU" }) Assert.Equal(NewHome2, starts[w]);   // 상부 면 = 홈 위치_2
        foreach (var w in new[] { "B", "SL", "PL", "SM", "PM", "F", "A" }) Assert.Equal(Ready, starts[w]);
    }

    [Fact]
    public void 면별_출발_자세를_보내면_그대로_쓰고_모르는_면은_버린다()
    {
        var (_, starts) = AmrTeachingService.Normalize(
            new[] { new TeachingJointsUpload("home2", NewHome2) },
            new Dictionary<string, double[]> { ["t"] = Ready, ["X9"] = Ready })!.Value;
        Assert.Equal(Ready, starts["T"]);            // 교시 위치보다 면별 값이 우선(면 검사 준비 슬롯 등 AMR 규칙 결과)
        Assert.Single(starts);
    }

    [Fact]
    public void HD_AMR_본문을_읽는다()
    {
        // HD_AMR AcsProfilePayload.Body 직렬화 모양(웹 기본 = camelCase)
        const string json = """
            {"source":"AMR-01","full":true,"profiles":[],"assignments":[],
             "teachingPositions":[{"key":"home2","joints":[-0.414,-11.992,121.972,-110.169,89.975,180.392]}],
             "startJointsByWall":{"T":[-0.414,-11.992,121.972,-110.169,89.975,180.392]}}
            """;
        var req = JsonSerializer.Deserialize<ProfileSyncRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("home2", req.TeachingPositions!.Single().Key);
        Assert.Equal(NewHome2, req.StartJointsByWall!["T"]);
    }

    [Fact]
    public void 구버전_AMR_은_교시_위치가_없다() =>
        Assert.Null(AmrTeachingService.Normalize(null, null));

    [Theory]
    [InlineData("짧음")]
    [InlineData("NaN")]
    [InlineData("범위 밖")]
    [InlineData("키 없음")]
    [InlineData("키 중복")]
    public async Task 잘못된_교시_위치는_거부하고_아무것도_저장하지_않는다(string kind)
    {
        var bad = kind switch
        {
            "짧음" => new[] { new TeachingJointsUpload("home2", new[] { 1.0, 2, 3 }) },
            "NaN" => new[] { new TeachingJointsUpload("home2", new[] { 1.0, 2, 3, 4, 5, double.NaN }) },
            "범위 밖" => new[] { new TeachingJointsUpload("home2", new[] { 1.0, 2, 3, 4, 5, 900 }) },
            "키 없음" => new[] { new TeachingJointsUpload(" ", Ready) },
            _ => new[] { new TeachingJointsUpload("ready", Ready), new TeachingJointsUpload("ready", Ready) },
        };
        var db = NewDb();
        await Assert.ThrowsAsync<ProfileSyncException>(() => InspectionProfileService.SyncAsync(db, Req(bad)));
        Assert.Empty(db.AmrTeachings);
    }

    [Fact]
    public async Task 저장하고_같은_값이면_바뀌지_않았다고_하고_구버전_전송은_저장값을_건드리지_않는다()
    {
        var db = NewDb();
        var first = await InspectionProfileService.SyncAsync(db, Req(new[] { new TeachingJointsUpload("home2", OldHome2) }));
        Assert.True(first.TeachingChanged);
        Assert.Equal((1, 3), (first.TeachingPositions, first.StartJoints));   // home2 만 → T·SU·PU

        var same = await InspectionProfileService.SyncAsync(db, Req(new[] { new TeachingJointsUpload("home2", OldHome2) }));
        Assert.False(same.TeachingChanged);

        var retaught = await InspectionProfileService.SyncAsync(db, Req(new[] { new TeachingJointsUpload("home2", NewHome2) }));
        Assert.True(retaught.TeachingChanged);
        Assert.Contains(db.AuditLogs, a => a.Action == "INSPECTION_PROFILE_SYNC");

        var legacy = await InspectionProfileService.SyncAsync(db, Req(null));   // 교시 위치 필드 없음
        Assert.Null(legacy.TeachingPositions);
        var loaded = await AmrTeachingService.LoadStartJointsAsync(db);
        Assert.Equal(NewHome2, loaded!.Value.Starts["T"]);
    }

    [Fact]
    public async Task 출발_자세는_검사_종류의_AMR_것을_쓴다()
    {
        var db = NewDb();
        await InspectionProfileService.SyncAsync(db, Req(null, new Dictionary<string, double[]> { ["T"] = NewHome2 }, "AMR-01"));
        await InspectionProfileService.SyncAsync(db, Req(null, new Dictionary<string, double[]> { ["T"] = OldHome2 }, "GAS-01"));

        Assert.Equal(NewHome2, (await AmrTeachingService.LoadStartJointsAsync(db, source: "amr-01"))!.Value.Starts["T"]);
        // 지정이 없으면 다른 검사 종류의 AMR 을 뺀 가장 최근 AMR
        Assert.Equal(NewHome2, (await AmrTeachingService.LoadStartJointsAsync(db, excludeSources: new[] { "GAS-01" }))!.Value.Starts["T"]);
        Assert.Null(await AmrTeachingService.LoadStartJointsAsync(db, source: "AMR-99"));
    }

    [Fact]
    public void 설정과_다른_면만_알린다()
    {
        var configured = new Dictionary<string, double[]> { ["T"] = OldHome2, ["F"] = Ready };
        var amr = new Dictionary<string, double[]>
        {
            ["T"] = NewHome2, ["F"] = Ready.Select(v => v + 0.005).ToArray(), ["A"] = Ready,
        };
        var diff = AmrTeachingService.Differences(configured, amr);
        Assert.Equal(2, diff.Count);
        Assert.StartsWith("T: AMR [-0.414, -11.992", diff[0]);
        Assert.Equal("A: AMR [-0.391, -9.414, 119.984, -110.76, 90.001, 180.392] ≠ 설정 없음", diff[1]);
    }

    // ── 시뮬레이터 연결 ──

    private static async Task<AcsDbContext> SeedTank()
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
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.6,
            Tasks =
            {
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.0, StartV = 1.8, EndU = 3.36, EndV = 1.8, SeamType = "LINE", SectionDxfId = "D", ProfileId = "P" },
            },
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static ReachSimService Sim(AcsDbContext db, bool useAmrStarts = true)
    {
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var cfg = new Dictionary<string, string?>
        {
            ["Acs:Reach:MountYawDeg"] = "180", ["Acs:Reach:ToolZMm"] = "365", ["Acs:Reach:ScanStandoffMm"] = "43.8",
            ["Acs:Reach:FocusStandoffMm"] = "10", ["Acs:Reach:UseAmrStartJoints"] = useAmrStarts ? "true" : "false",
        };
        for (var i = 0; i < 6; i++) cfg[$"Acs:Reach:StartJoints:F:{i}"] = Ready[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ReachSimService(db, dispatcher, new ConfigurationBuilder().AddInMemoryCollection(cfg).Build());
    }

    [Fact]
    public async Task 받은_출발_자세로_조사_판정이_바뀐다()
    {
        var db = await SeedTank();
        var before = (await Sim(db).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.Single();
        Assert.NotEqual("Unreachable", before.Verdict);                   // 설정 출발 자세(작업 준비 위치) — 닿는다

        // AMR 이 F 면 출발 자세를 J1 이 반대쪽(180° 돌린)인 자세로 교시했다고 보낸다 → 그 자세에서는 가지 전환으로 진입 불가
        var turned = Ready.ToArray();
        turned[0] += 180;
        await InspectionProfileService.SyncAsync(db, Req(null, new Dictionary<string, double[]> { ["F"] = turned }));
        var after = (await Sim(db).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.Single();
        Assert.Equal("Unreachable", after.Verdict);
        Assert.Contains("가지 전환", after.Blocker);

        // 받은 값을 쓰지 않으면(설정 끔) 설정 출발 자세 그대로
        var off = (await Sim(db, useAmrStarts: false).SurveyAsync("CT1", 1, "F", CancellationToken.None))!.Single().Seams.Single();
        Assert.Equal(before.Verdict, off.Verdict);
    }
}
