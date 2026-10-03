using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Planning;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 계획 자연어 어시스턴트 [ADR-013] — 변경안 엔진(전개·검증·적용)과 가짜 Ollama 왕복.
/// 선창 = 사양서 예시 CT1(L45·wFloor 8.2·45°·1.9/5.4/1.9, 층 발판 [0,2.4,4.8,7.2]) — PM L2 도달 v = [0.5, 2.9].
/// </summary>
public class PlanningAssistantTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ServiceProvider Build(FakeOllama? ollama = null, bool enabled = true)
    {
        var dbName = Guid.NewGuid().ToString();
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddMemoryCache();
        sc.AddDbContext<AcsDbContext>(o => o.UseInMemoryDatabase(dbName));
        sc.AddSingleton(new LlmOptions { Enabled = enabled, Model = "test-model", MaxOps = 5000 });
        sc.AddHttpClient(OllamaClient.HttpClientName, c => c.BaseAddress = new Uri("http://ollama.test"))
            .ConfigurePrimaryHttpMessageHandler(() => ollama ?? new FakeOllama());
        sc.AddSingleton<OllamaClient>();
        sc.AddScoped<PlanChangeSetEngine>();
        sc.AddScoped<PlanningAssistantService>();
        var sp = sc.BuildServiceProvider();
        Seed(sp.GetRequiredService<AcsDbContext>());
        return sp;
    }

    private static void Seed(AcsDbContext db)
    {
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 45, WFloor = 8.2, ThetaLow = Math.PI / 4, HLow = 1.9, HWall = 5.4, ThetaUp = Math.PI / 4, HUp = 1.9,
            LevelZ = "[0,2.4,4.8,7.2]",
        });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        // PM L2 영역 2개(면-전체 v 0.6~2.0) + 작업
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "PM", Level = 2, Name = "PM-L2-01",
            Corners = "[[3,0.6],[4.4,0.6],[4.4,2.0],[3,2.0]]", UMin = 3, VMin = 0.6, UMax = 4.4, VMax = 2.0,
            Tasks =
            {
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, SeamType = "LINE", StartU = 3.2, StartV = 0.9, EndU = 4.2, EndV = 0.9, SectionDxfId = "D", ProfileId = "P" },
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 2, SeamType = "LINE", StartU = 3.5, StartV = 0.7, EndU = 3.5, EndV = 1.9, SectionDxfId = "D", ProfileId = "P" },
            },
        });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "PM", Level = 2, Name = "PM-L2-02",
            Corners = "[[5,0.6],[6.4,0.6],[6.4,2.0],[5,2.0]]", UMin = 5, VMin = 0.6, UMax = 6.4, VMax = 2.0,
        });
        db.Scenarios.Add(new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "좌현 정기", Version = 1, TankId = "CT1", Policy = "{}", Status = "DRAFT" });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static PlanChangeSetEngine Engine(ServiceProvider sp) => sp.GetRequiredService<PlanChangeSetEngine>();
    private static AcsDbContext Db(ServiceProvider sp) => sp.GetRequiredService<AcsDbContext>();

    private static async Task<(PlanPreview P, bool Ok)> PreviewApply(ServiceProvider sp, params PlanOp[] ops)
    {
        var p = await Engine(sp).PreviewAsync("CT1", ops, 5000);
        var (ok, _) = await Engine(sp).ApplyAsync("CT1", p.Ops.Select(o => o.Op).ToList(), p.Fingerprint);
        return (p, ok);
    }

    // ── 매크로 전개 ────────────────────────────────

    [Fact]
    public async Task GridAreas_FloorL1_CoversFaceWithin144_AndDerivesLevel()
    {
        using var sp = Build();
        var (p, ok) = await PreviewApply(sp, new PlanOp { Op = "gridAreas", WallCode = "B", Level = 1, CellU = 1.4, CellV = 1.4 });

        Assert.True(ok);
        Assert.True(p.AllOk);
        Assert.Equal(33 * 6, p.Creates);   // u 45/1.4 → 33칸(끝 0.2) · v 8.2/1.4 → 6칸(끝 1.2)
        var areas = await Db(sp).InspectionAreas.AsNoTracking().Where(a => a.WallCode == "B").ToListAsync();
        Assert.Equal(198, areas.Count);
        Assert.All(areas, a =>
        {
            Assert.Equal(1, a.Level);
            Assert.True(a.UMax - a.UMin <= 1.44 + 1e-9 && a.VMax - a.VMin <= 1.44 + 1e-9);
            Assert.InRange(a.UMax, 0, 45 + 1e-9);
            Assert.InRange(a.VMax, 0, 8.2 + 1e-9);
        });
        Assert.Contains(areas, a => a.Name == "B-L1-001");

        // 같은 격자를 다시 요청하면 기존 영역과 겹쳐 0건(중복 생성 방지)
        var again = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "gridAreas", WallCode = "B", Level = 1 }], 5000);
        Assert.Equal(0, again.Creates);
        Assert.Contains(again.Messages, m => m.Contains("겹쳐 198칸"));
    }

    [Fact]
    public async Task GridAreas_LevelLocalV_OnWall_StaysInsideLevelBand()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "gridAreas", WallCode = "SM", Level = 2, CellU = 1.2, CellV = 1.2 }], 5000);

        Assert.True(p.AllOk);
        Assert.All(p.Ops, o =>
        {
            Assert.Equal(2, o.Level);
            Assert.All(o.Corners!, c => Assert.InRange(c[1], 0.5 - 1e-9, 2.9 + 1e-9));   // SM L2 도달 v = [0.5, 2.9]
        });
    }

    [Fact]
    public async Task GridAreas_Bulkhead_SkipsCellsOutsideOctagon()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "gridAreas", WallCode = "F", Level = 1 }], 5000);

        Assert.True(p.AllOk);
        Assert.Contains(p.Messages, m => m.Contains("격벽 윤곽 밖"));
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        double b2 = geom.Derived().B / 2;
        Assert.All(p.Ops.SelectMany(o => o.Corners!), c => Assert.True(Math.Abs(c[0] - b2) <= geom.BulkheadHalfWidth(c[1]) + 1e-6));
    }

    [Fact]
    public async Task CreateArea_LevelLocalV_IsStoredFaceGlobal_ThenTaskInSameChangeSet()
    {
        using var sp = Build();
        var (p, ok) = await PreviewApply(sp,
            new PlanOp { Op = "createArea", WallCode = "PM", Level = 2, Name = "PM-L2-10", Corners = [[10, 0], [11, 0], [11, 1], [10, 1]] },
            new PlanOp { Op = "createTask", WallCode = "PM", AreaName = "PM-L2-10", StartU = 10.2, StartV = 0.5, EndU = 10.8, EndV = 0.5, SeamType = "cross4" });

        Assert.True(ok, string.Join(" / ", p.Ops.Select(o => o.Error)));
        var a = await Db(sp).InspectionAreas.AsNoTracking().Include(x => x.Tasks).SingleAsync(x => x.Name == "PM-L2-10");
        Assert.Equal(2, a.Level);
        Assert.Equal((0.5, 1.5), (a.VMin, a.VMax));                 // 층-로컬 0~1 → 면-전체 +0.5
        var t = Assert.Single(a.Tasks);
        Assert.Equal(1.0, t.StartV, 9);                              // 0.5 + VOff 0.5
        Assert.Equal("CROSS4", t.SeamType);                          // 대문자 정규화
        Assert.Equal(PlanChangeSetEngine.DefaultProfileId, t.ProfileId);
    }

    [Fact]
    public async Task RenameAndSeamType_AndQuery()
    {
        using var sp = Build();
        var (_, ok) = await PreviewApply(sp,
            new PlanOp { Op = "renameAreas", WallCode = "PM", Level = 2, Prefix = "P2-" },
            new PlanOp { Op = "setSeamType", NamePattern = "P2-PM-L2-*", SeamType = "CROSS4", Seqs = [2] });
        Assert.True(ok);

        var names = await Db(sp).InspectionAreas.AsNoTracking().OrderBy(a => a.Name).Select(a => a.Name).ToListAsync();
        Assert.Equal(["P2-PM-L2-01", "P2-PM-L2-02"], names);
        var seams = await Db(sp).AreaTasks.AsNoTracking().OrderBy(t => t.Seq).Select(t => t.SeamType).ToListAsync();
        Assert.Equal(["LINE", "CROSS4"], seams);

        var q = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "query", WallCode = "PM", Aggregate = "list" }], 5000);
        Assert.Empty(q.Ops);
        Assert.Contains("영역 2개 · 작업 2개 (CROSS4 1, LINE 1)", q.Messages.Single());
    }

    [Fact]
    public async Task RenameAreas_ExactNewName_KeepsAreaId()
    {
        using var sp = Build();
        var id = (await Db(sp).InspectionAreas.AsNoTracking().SingleAsync(a => a.Name == "PM-L2-01")).AreaId;
        var (p, ok) = await PreviewApply(sp, new PlanOp { Op = "renameAreas", AreaName = "PM-L2-01", Name = "PM-NEW" });

        Assert.True(ok, string.Join(" / ", p.Ops.Select(o => o.Error)));
        var a = await Db(sp).InspectionAreas.AsNoTracking().SingleAsync(x => x.AreaId == id);
        Assert.Equal("PM-NEW", a.Name);   // 같은 areaId — 작업(taskId)·이력 유지
    }

    [Fact]
    public async Task RenameAreas_ExactNewName_ToManyTargets_IsRejected()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "renameAreas", WallCode = "PM", Name = "X" }], 5000);
        Assert.False(p.AllOk);
        Assert.Contains("영역 1개에만", p.Ops.Single().Error);
    }

    [Fact]
    public async Task RenameAreas_NothingChanges_ExplainsWhy()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "renameAreas", AreaName = "PM-L2-01" }], 5000);
        Assert.Empty(p.Ops);
        Assert.Contains(p.Messages, m => m.Contains("바뀌는 이름이 없습니다"));
    }

    [Fact]
    public async Task RenameAreas_FindReplace_RemovesPart_AndDuplicateFails()
    {
        using var sp = Build();
        var (_, ok) = await PreviewApply(sp, new PlanOp { Op = "renameAreas", NamePattern = "PM-L2-*", Find = "L2-", Replace = "" });
        Assert.True(ok);
        Assert.Equal(["PM-01", "PM-02"], await Db(sp).InspectionAreas.AsNoTracking().OrderBy(a => a.Name).Select(a => a.Name).ToListAsync());

        var dup = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "renameAreas", AreaName = "PM-01", Name = "PM-02" }], 5000);
        Assert.False(dup.AllOk);
        Assert.Contains("이미 있습니다", dup.Ops.Single().Error);
    }

    [Fact]
    public async Task CopyArea_Left_SameSize_WithTasks()
    {
        using var sp = Build();
        var (p, ok) = await PreviewApply(sp, new PlanOp { Op = "copyArea", WallCode = "PM", AreaName = "PM-L2-01", Name = "PM-L2-01b", Placement = "left" });

        Assert.True(ok, string.Join(" / ", p.Ops.Select(o => o.Error)));
        var db = Db(sp);
        var src = await db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).SingleAsync(a => a.Name == "PM-L2-01");
        var copy = await db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).SingleAsync(a => a.Name == "PM-L2-01b");
        Assert.Equal((1.6, 3.0), (Math.Round(copy.UMin, 9), Math.Round(copy.UMax, 9)));      // 원본 u 3~4.4 → 폭 1.4만큼 왼쪽
        Assert.Equal((src.VMin, src.VMax, src.Level), (copy.VMin, copy.VMax, copy.Level));
        Assert.Equal(src.Tasks.Count, copy.Tasks.Count);
        foreach (var t in src.Tasks)
        {
            var c = copy.Tasks.Single(x => x.Seq == t.Seq);
            Assert.Equal(t.StartU - 1.4, c.StartU, 9);
            Assert.Equal((t.StartV, t.EndV, t.SeamType, t.ProfileId), (c.StartV, c.EndV, c.SeamType, c.ProfileId));
            Assert.NotEqual(t.TaskId, c.TaskId);   // 새 작업 = 새 taskId
        }
    }

    [Fact]
    public async Task CopyArea_OutOfLevelBand_FailsWithReason()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "copyArea", AreaName = "PM-L2-01", Name = "X", Placement = "above" }], 5000);
        Assert.False(p.AllOk);
        Assert.Contains("층", p.Ops.First().Error);   // v 2.0~3.4 → L2 도달 구간(≤2.9) 밖
    }

    [Fact]
    public async Task Context_IncludesSelectedWallAreaCoordinates_LevelLocal()
    {
        using var sp = Build();
        var svc = sp.GetRequiredService<PlanningAssistantService>();
        var ctx = await svc.BuildContextAsync("CT1", "PM", 2, default);
        Assert.Contains("PM-L2-01 u 3~4.4 v 0.1~1.5 작업 2", ctx);   // 면-전체 v 0.6~2.0 − VOff 0.5
    }

    [Theory]
    [InlineData("below", 0.3, null, 0.0, -0.3)]
    [InlineData("below", null, 0.3, 0.0, -0.3)]    // LLM이 "아래로 0.3"을 dv=+0.3으로 낸 경우 — 방향이 부호를 정한다
    [InlineData("above", 0.2, -0.2, 0.0, 0.2)]
    [InlineData("left", 0.5, null, -0.5, 0.0)]
    [InlineData(null, null, 0.3, 0.0, 0.3)]        // 방향 없으면 du/dv 그대로
    public void MoveOffset_DirectionDecidesSign(string? placement, double? distance, double? dv, double expDu, double expDv)
    {
        var (du, v) = PlanChangeSetEngine.MoveOffset(new PlanOp { Op = "moveAreas", Placement = placement, Distance = distance, Dv = dv })!.Value;
        Assert.Equal((expDu, expDv), (du, v));
    }

    [Fact]
    public async Task MoveAreas_Below_LowersArea()
    {
        using var sp = Build();
        var (_, ok) = await PreviewApply(sp, new PlanOp { Op = "moveAreas", AreaName = "PM-L2-01", Placement = "below", Distance = 0.1, Dv = 0.1 });
        Assert.True(ok);
        var a = await Db(sp).InspectionAreas.AsNoTracking().SingleAsync(x => x.Name == "PM-L2-01");
        Assert.Equal(0.5, a.VMin, 9);   // 0.6 → 0.5 (위가 아니라 아래)
    }

    [Fact]
    public async Task MoveAreas_MovesTasksToo_AndRejectsLeavingFace()
    {
        using var sp = Build();
        var (_, ok) = await PreviewApply(sp, new PlanOp { Op = "moveAreas", AreaName = "PM-L2-01", Du = 0.5, Dv = 0.1 });
        Assert.True(ok);
        var a = await Db(sp).InspectionAreas.AsNoTracking().Include(x => x.Tasks).SingleAsync(x => x.Name == "PM-L2-01");
        Assert.Equal((3.5, 0.7), (a.UMin, a.VMin));
        Assert.Equal(3.7, a.Tasks.Single(t => t.Seq == 1).StartU, 9);

        var bad = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "moveAreas", AreaName = "PM-L2-02", Du = 100 }], 5000);
        Assert.False(bad.AllOk);
        Assert.Contains("면 범위", bad.Ops.Single().Error);
    }

    [Fact]
    public async Task FailingOp_AppliesNothing()
    {
        using var sp = Build();
        var ops = new[]
        {
            new PlanOp { Op = "renameAreas", AreaName = "PM-L2-01", Suffix = "-A" },
            new PlanOp { Op = "createArea", WallCode = "PM", Level = 2, Name = "PM-L2-02", Corners = [[20, 0], [21, 0], [21, 1], [20, 1]] },   // 이름 중복
        };
        var p = await Engine(sp).PreviewAsync("CT1", ops, 5000);
        Assert.False(p.AllOk);
        Assert.Contains("이미 있습니다", p.Ops.Last().Error);

        var (ok, _) = await Engine(sp).ApplyAsync("CT1", p.Ops.Select(o => o.Op).ToList(), p.Fingerprint);
        Assert.False(ok);
        Assert.False(await Db(sp).InspectionAreas.AsNoTracking().AnyAsync(a => a.Name == "PM-L2-01-A"));   // 앞 연산도 미반영
    }

    [Fact]
    public async Task MacroWithoutFilter_IsRejected()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "deleteAreas" }], 5000);
        Assert.False(p.AllOk);
        Assert.Contains("필터", p.Ops.Single().Error);
    }

    [Fact]
    public async Task Apply_AfterConcurrentChange_Conflicts()
    {
        using var sp = Build();
        var p = await Engine(sp).PreviewAsync("CT1", [new PlanOp { Op = "renameAreas", AreaName = "PM-L2-02", Suffix = "x" }], 5000);

        var db = Db(sp);
        var a = await db.InspectionAreas.SingleAsync(x => x.Name == "PM-L2-01");
        a.Name = "다른 사람이 고침";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<PlanChangeSetEngine.PlanConflictException>(() =>
            Engine(sp).ApplyAsync("CT1", p.Ops.Select(o => o.Op).ToList(), p.Fingerprint));
    }

    [Fact]
    public async Task DeleteAreas_RemovesTasksAndScenarioLinks_ScenarioAddCreatesIfMissing()
    {
        using var sp = Build();
        var (_, ok) = await PreviewApply(sp,
            new PlanOp { Op = "setScenarioAreas", ScenarioName = "PM 부분", CreateIfMissing = true, WallCode = "PM" },
            new PlanOp { Op = "setScenarioAreas", ScenarioName = "좌현 정기", Mode = "add", AreaName = "PM-L2-02" });
        Assert.True(ok);
        var db = Db(sp);
        var sid = (await db.Scenarios.AsNoTracking().SingleAsync(s => s.Name == "PM 부분")).ScenarioId;
        Assert.Equal(2, await db.ScenarioAreas.CountAsync(x => x.ScenarioId == sid));

        var (_, ok2) = await PreviewApply(sp, new PlanOp { Op = "deleteAreas", AreaName = "PM-L2-01" });
        Assert.True(ok2);
        Assert.Equal(1, await db.ScenarioAreas.CountAsync(x => x.ScenarioId == sid));
        Assert.Equal(0, await db.AreaTasks.CountAsync());
    }

    // ── LLM 왕복 ───────────────────────────────────

    [Fact]
    public async Task Propose_ViaOllama_ReturnsPreview_ThenApply()
    {
        var fake = new FakeOllama(JsonSerializer.Serialize(new
        {
            reply = "PM L2 영역 이름에 접두어를 붙입니다.",
            ops = new[] { new { op = "renameAreas", wallCode = "PM", level = 2, prefix = "P2-" } },
        }));
        using var sp = Build(fake);
        var svc = sp.GetRequiredService<PlanningAssistantService>();

        var cs = await svc.ProposeAsync(new ProposeRequest("CT1", "PM 2층 영역 이름 앞에 P2- 붙여", "PM", 2, null), default);

        Assert.NotNull(cs.ChangeSetId);
        Assert.Equal(2, cs.Updates);
        Assert.Equal("PM L2 영역 이름에 접두어를 붙입니다.", cs.Reply);
        // LLM 요청: structured output 스키마·temperature 0·컨텍스트 요약 포함
        var req = JsonNode.Parse(fake.Requests.Single())!;
        Assert.Equal("test-model", req["model"]!.GetValue<string>());
        Assert.False(req["stream"]!.GetValue<bool>());
        Assert.NotNull(req["format"]!["properties"]!["ops"]);
        var system = req["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("PM L2: 영역 2 · 작업 2", system);
        Assert.Contains("현재 화면 선택: 면 PM, 층 L2", system);

        var r = await svc.ApplyAsync(cs.ChangeSetId!.Value, new ApplyChangeSetRequest("op1"), default);
        Assert.Equal(2, r.Updates);
        Assert.True(await Db(sp).AuditLogs.AnyAsync(l => l.Action == "PLANNING_ASSISTANT_APPLY" && l.UserId == "op1"));
        await Assert.ThrowsAsync<PlanningAssistantService.ChangeSetNotFoundException>(() =>
            svc.ApplyAsync(cs.ChangeSetId!.Value, new ApplyChangeSetRequest("op1"), default));   // 1회용
    }

    [Fact]
    public async Task Propose_RenameWithNewName_FromLlm()
    {
        var fake = new FakeOllama(JsonSerializer.Serialize(new
        {
            reply = "이름을 바꿉니다.",
            ops = new[] { new { op = "renameAreas", areaName = "PM-L2-01", name = "PM-A0001" } },
        }));
        using var sp = Build(fake);
        var cs = await sp.GetRequiredService<PlanningAssistantService>()
            .ProposeAsync(new ProposeRequest("CT1", "PM-L2-01 이름을 PM-A0001로 바꿔", null, null, null), default);

        Assert.NotNull(cs.ChangeSetId);
        Assert.Equal(1, cs.Updates);
        Assert.Contains("PM-L2-01", cs.Results.Single().Summary);
        Assert.Contains("PM-A0001", cs.Results.Single().Summary);
        var system = JsonNode.Parse(fake.Requests.Single())!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("\"name\":\"F-A0001\"", system);   // 프롬프트에 단일 이름 변경 예시
    }

    [Fact]
    public async Task Propose_EmptyOps_AsksOnceMore_ThenUsesFilledOps()
    {
        var fake = new FakeOllama(
            JsonSerializer.Serialize(new { reply = "F-SM-A0001 영역의 이름을 F-A0001로 변경합니다.", ops = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { reply = "이름을 바꿉니다.", ops = new[] { new { op = "renameAreas", areaName = "PM-L2-01", name = "PM-A0001" } } }));
        using var sp = Build(fake);
        var cs = await sp.GetRequiredService<PlanningAssistantService>()
            .ProposeAsync(new ProposeRequest("CT1", "PM-L2-01을 PM-A0001로 변경해줘", null, null, null), default);

        Assert.Equal(2, fake.Requests.Count);
        var nudge = JsonNode.Parse(fake.Requests[1])!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("ops 가 비어 있습니다", nudge);
        Assert.NotNull(cs.ChangeSetId);
        Assert.Equal(["renameAreas(areaName=PM-L2-01, name=PM-A0001)"], cs.OpsSummary!);
    }

    [Fact]
    public async Task Propose_EmptyOpsTwice_ReturnsNoChangesWithEmptySummary()
    {
        var empty = JsonSerializer.Serialize(new { reply = "변경합니다.", ops = Array.Empty<object>() });
        var fake = new FakeOllama(empty, empty);
        using var sp = Build(fake);
        var cs = await sp.GetRequiredService<PlanningAssistantService>()
            .ProposeAsync(new ProposeRequest("CT1", "바꿔줘", null, null, null), default);

        Assert.Equal(2, fake.Requests.Count);   // 되묻기는 1회뿐
        Assert.False(cs.HasChanges);
        Assert.Empty(cs.OpsSummary!);
    }

    [Fact]
    public void ReplySchema_ForbidsUnknownFields()
    {
        var schema = PlanningAssistantService.ReplySchema(["B"]);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.False(schema["properties"]!["ops"]!["items"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Propose_RetriesOnceOnBrokenJson()
    {
        var fake = new FakeOllama("{not json", JsonSerializer.Serialize(new { reply = "조회합니다.", ops = new[] { new { op = "query", wallCode = "PM" } } }));
        using var sp = Build(fake);
        var cs = await sp.GetRequiredService<PlanningAssistantService>().ProposeAsync(new ProposeRequest("CT1", "PM 영역 몇 개?", null, null, null), default);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Null(cs.ChangeSetId);          // 조회 전용 — 적용할 변경 없음
        Assert.False(cs.HasChanges);
        Assert.Contains("영역 2개", cs.Messages.Single());
    }

    [Fact]
    public async Task Propose_Disabled_Throws()
    {
        using var sp = Build(enabled: false);
        await Assert.ThrowsAsync<PlanningAssistantService.LlmDisabledException>(() =>
            sp.GetRequiredService<PlanningAssistantService>().ProposeAsync(new ProposeRequest("CT1", "아무거나", null, null, null), default));
    }

    [Fact]
    public void ReplySchema_SerializesPlanOpFieldNames()
    {
        // 스키마 필드명 = PlanOp 의 camelCase 속성명 — 어긋나면 LLM이 채운 값이 조용히 버려진다.
        var props = PlanningAssistantService.ReplySchema(["B"])["properties"]!["ops"]!["items"]!["properties"]!.AsObject()
            .Select(kv => kv.Key).ToHashSet();
        var opProps = typeof(PlanOp).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();
        Assert.Subset(opProps, props);
    }

    private sealed class FakeOllama(params string[] contents) : HttpMessageHandler
    {
        private int _i;
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/chat", request.RequestUri!.AbsolutePath);
            Assert.NotNull(request.Content!.Headers.ContentLength);   // chunked 금지(실소켓 수신기 호환)
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            var content = contents.Length == 0 ? "{\"reply\":\"\",\"ops\":[]}" : contents[Math.Min(_i++, contents.Length - 1)];
            var body = new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }, ["done"] = true };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
