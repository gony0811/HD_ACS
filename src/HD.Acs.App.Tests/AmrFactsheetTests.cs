using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>AMR factsheet(§7.3) — hdAmr 해석, 설정 키 변환, 설정 층 반영(appsettings &lt; AMR &lt; 화면), 변경 알람, run 중 대기.</summary>
public class AmrFactsheetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdacs-fs-" + Guid.NewGuid().ToString("N"));
    public AmrFactsheetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly double[] Home2 = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    /// <summary>HD_AMR AcsFactsheet.Build 와 같은 모양의 본문.</summary>
    private static string Body(int version, string liftMode = "Off", double? fixedMm = null, double mountZ = 927, string[]? changed = null)
    {
        static JsonArray A(double[] v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return new JsonObject
        {
            ["headerId"] = 1, ["version"] = "2.0.0", ["manufacturer"] = "HD", ["serialNumber"] = "AMR-01",
            ["typeSpecification"] = new JsonObject { ["seriesName"] = "HD_AMR" },
            ["hdAmr"] = new JsonObject
            {
                ["schemaVersion"] = 1, ["configVersion"] = version, ["changedAt"] = "2026-11-20T01:00:00.000Z",
                ["changed"] = new JsonArray((changed ?? Array.Empty<string>()).Select(c => (JsonNode?)c).ToArray()),
                ["fingerprints"] = new JsonObject { ["teaching"] = "a", ["lift"] = "b", ["mount"] = "c", ["cobot"] = "d" },
                ["teaching"] = new JsonObject
                {
                    ["liftMode"] = "SHORT",
                    ["startJointsByWall"] = new JsonObject { ["T"] = A(Home2), ["F"] = A(Ready), ["X"] = new JsonArray(1, 2) },
                },
                ["lift"] = new JsonObject { ["enabled"] = liftMode == "Auto", ["mode"] = liftMode, ["fixedStrokeMm"] = fixedMm },
                ["mount"] = new JsonObject { ["tAB"] = A(new[] { 0, 0, mountZ, 0, 0, 180 }) },
                ["cobot"] = new JsonObject { ["linksMm"] = A(new[] { 180, -970.317, -815.788, 159.25, 114.077, 105 }), ["maxEntryJointTravelDeg"] = 150 },
            },
        }.ToJsonString();
    }

    [Fact]
    public void hdAmr_확장을_읽고_설정_키로_옮긴다()
    {
        var f = AmrFactsheet.Parse(Body(3, "Fixed", 300))!;
        Assert.Equal(3, f.ConfigVersion);
        Assert.Equal(Home2, f.StartJointsByWall["T"]);
        Assert.False(f.StartJointsByWall.ContainsKey("X"));   // 6개가 아닌 값은 버린다
        Assert.Equal("Fixed", f.LiftMode);
        Assert.Equal(927, f.MountPose![2]);

        var all = new HashSet<string>(AmrFactsheetService.AllSections);
        var k = AmrFactsheetService.ConfigKeys(f, all);
        Assert.Equal("Fixed", k["Acs:Lift:Mode"]);
        Assert.Equal("300", k["Acs:Lift:FixedStrokeMm"]);
        Assert.Equal("-140.907", k["Acs:Reach:StartJoints:T:2"]);
        Assert.Equal("927", k["Acs:Reach:MountZMm"]);
        Assert.Equal("180", k["Acs:Reach:MountYawDeg"]);
        Assert.Equal("105", k["Acs:Reach:LinksMm:5"]);
        Assert.Equal("150", k["Acs:Reach:MaxEntryJointTravelDeg"]);

        // 가스검사 AMR 은 검사 종류 묶음으로, 꺼 둔 묶음은 싣지 않는다
        var gas = AmrFactsheetService.ConfigKeys(f, new HashSet<string> { "teaching", "lift" }, "Acs:Reach:Kinds:GAS:");
        Assert.True(gas.ContainsKey("Acs:Reach:Kinds:GAS:StartJoints:T:0"));
        Assert.True(gas.ContainsKey("Acs:Lift:Mode"));
        Assert.DoesNotContain(gas.Keys, x => x.Contains("MountZMm") || x.Contains("LinksMm"));

        // 높이 없는 고정·범위 밖 BASE 높이는 싣지 않는다
        var bad = AmrFactsheetService.ConfigKeys(AmrFactsheet.Parse(Body(1, "Fixed", null, mountZ: 50))!, all);
        Assert.False(bad.ContainsKey("Acs:Lift:Mode"));
        Assert.False(bad.ContainsKey("Acs:Reach:MountZMm"));

        Assert.Null(AmrFactsheet.Parse("{\"typeSpecification\":{}}"));   // 확장 없음
        Assert.Null(AmrFactsheet.Parse("not json"));
    }

    /// <summary>HD_AMR AcsFactsheet.Build 가 실제로 만든 본문(Data/amr_factsheet_v7.json — 리프트 꺼짐·서 있는 높이 300)을 그대로 읽는다.</summary>
    [Fact]
    public void HD_AMR_이_만든_본문을_그대로_읽는다()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HD.Acs.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? Environment.GetEnvironmentVariable("HDACS_SRC") ?? throw new InvalidOperationException("src 폴더 없음");
        var f = AmrFactsheet.Parse(File.ReadAllText(Path.Combine(root, "HD.Acs.App.Tests", "Data", "amr_factsheet_v7.json")))!;
        Assert.Equal(7, f.ConfigVersion);
        Assert.Equal(new[] { "lift", "mount" }, f.Changed);
        Assert.Equal(4, f.Fingerprints.Count);
        Assert.Equal("SHORT", f.TeachingLiftMode);
        Assert.Equal(-140.907, f.StartJointsByWall["T"][2]);     // 상부 면 = 홈 위치_2
        Assert.Equal(-9.414, f.StartJointsByWall["F"][1]);       // 그 외 = 작업 준비 위치
        Assert.Equal(10, f.StartJointsByWall.Count);             // 홈 위치_3 이 없어 하부 면도 ready — 10면 모두
        Assert.Equal("Fixed", f.LiftMode);
        Assert.Equal(300, f.FixedStrokeMm);
        Assert.Equal(927, f.MountPose![2]);
        Assert.Equal(140, f.MaxEntryJointTravelDeg);
        var k = AmrFactsheetService.ConfigKeys(f, new HashSet<string>(AmrFactsheetService.AllSections));
        Assert.Equal("Fixed", k["Acs:Lift:Mode"]);
        Assert.Equal("300", k["Acs:Lift:FixedStrokeMm"]);
    }

    [Fact]
    public void AMR_끼리_다른_값은_반영하지_않는다()
    {
        var (m, c) = AmrFactsheetService.Merge(new[]
        {
            new Dictionary<string, string> { ["Acs:Lift:Mode"] = "Off", ["Acs:Reach:MountZMm"] = "927" },
            new Dictionary<string, string> { ["Acs:Lift:Mode"] = "Auto", ["Acs:Reach:MountZMm"] = "927" },
        });
        Assert.Equal(new[] { "Acs:Lift:Mode" }, c);
        Assert.Equal("927", m["Acs:Reach:MountZMm"]);
        Assert.False(m.ContainsKey("Acs:Lift:Mode"));
    }

    private (IConfigurationRoot Cfg, AmrFactsheetService Svc, ServiceProvider Sp) Make()
    {
        var amr = Path.Combine(_dir, AmrFactsheetService.FileName);
        var page = Path.Combine(_dir, ReachRuntimeSettings.FileName);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acs:Lift:Mode"] = "Auto", ["Acs:Reach:MountZMm"] = "1635", ["Acs:Reach:StartJoints:T:0"] = "99",
                ["Acs:Reach:StartJoints:T:1"] = "99", ["Acs:Reach:StartJoints:T:2"] = "99", ["Acs:Reach:StartJoints:T:3"] = "99",
                ["Acs:Reach:StartJoints:T:4"] = "99", ["Acs:Reach:StartJoints:T:5"] = "99",
            })
            .AddJsonFile(amr, optional: true, reloadOnChange: false)
            .AddJsonFile(page, optional: true, reloadOnChange: false)
            .Build();
        var name = Guid.NewGuid().ToString();
        var sp = new ServiceCollection().AddDbContext<AcsDbContext>(o => o.UseInMemoryDatabase(name)).BuildServiceProvider();
        return (cfg, new AmrFactsheetService(cfg, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AmrFactsheetService>.Instance, amr), sp);
    }

    private static readonly RobotRef Robot = new("AMR-01", "HD", "AMR-01");

    [Fact]
    public async Task 받으면_바로_모델에_반영되고_버전이_오르면_알람()
    {
        var (cfg, svc, sp) = Make();
        await svc.HandleAsync(Robot, Body(3));

        var model = ReachSimService.ModelFrom(cfg);
        Assert.Equal(927, model.MountZMm);                         // appsettings 1635 → AMR 보고 927
        Assert.Equal(Home2, model.StartJointsByWall["T"]);         // 출발 자세도 AMR 값
        Assert.Equal(Ready, model.StartJointsByWall["F"]);
        Assert.Equal(LiftMode.Off, LiftSettings.From(cfg).Mode);
        Assert.Equal(150, model.MaxEntryJointTravelDeg);
        Assert.True(svc.Get("AMR-01")!.Applied);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            Assert.Empty(db.Alarms);                                // 처음 받은 값은 "변경" 이 아니다
            Assert.Single(db.AuditLogs.Where(a => a.Action == "AMR_FACTSHEET_APPLY"));
        }

        await svc.HandleAsync(Robot, Body(3));                      // 같은 버전 재수신 — 기록 없음
        await svc.HandleAsync(Robot, Body(4, "Fixed", 300, changed: new[] { "lift" }));
        Assert.Equal(300, LiftSettings.From(cfg).ForOrder(600));    // 지령 리프트 높이도 바로
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            var alarm = Assert.Single(db.Alarms);
            Assert.Equal("AMR_CONFIG_CHANGED", alarm.AlarmCode);
            Assert.Contains("\"changed\":[\"lift\"]", alarm.Detail);
            Assert.Equal(2, db.AuditLogs.Count(a => a.Action == "AMR_FACTSHEET_APPLY"));
        }

        // 화면 설정이 AMR 값보다 우선
        new ReachRuntimeSettings(cfg, Path.Combine(_dir, ReachRuntimeSettings.FileName))
            .Save(new ReachSettingsUpdate(null, null, 1635, null, null, null));
        Assert.Equal(1635, ReachSimService.ModelFrom(cfg).MountZMm);
        Assert.Contains("Acs:Reach:MountZMm", new ReachRuntimeSettings(cfg, Path.Combine(_dir, ReachRuntimeSettings.FileName)).Read().AmrReported!);
    }

    [Fact]
    public async Task 진행_중_run_이_있으면_끝난_뒤_반영한다()
    {
        var (cfg, svc, sp) = Make();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = Guid.NewGuid(), RobotId = "AMR-01", State = "RUNNING", StartedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await svc.HandleAsync(Robot, Body(2));
        Assert.Equal(1635, ReachSimService.ModelFrom(cfg).MountZMm);
        Assert.False(svc.Get("AMR-01")!.Applied);
        Assert.Contains("진행 중 run", svc.Get("AMR-01")!.Note);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
            (await db.ScenarioRuns.FirstAsync()).State = "COMPLETED";
            await db.SaveChangesAsync();
        }
        Assert.True(await svc.TryApplyAsync());
        Assert.Equal(927, ReachSimService.ModelFrom(cfg).MountZMm);
    }
}
