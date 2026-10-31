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
/// 리치·간섭 시뮬레이션 조회 — 배차와 같은 정차점에서 CT1 경사면·수직벽 용접선을 판정한다(분석 2026-11-01 과 같은 방향).
/// </summary>
public class ReachSimServiceTests
{
    private static readonly Guid SlArea = Guid.Parse("5e000000-0000-4000-8000-000000000001");
    private static readonly Guid SlTask = Guid.Parse("5e000000-0000-4000-8000-000000000002");
    private static readonly Guid SmArea = Guid.Parse("5e000000-0000-4000-8000-000000000003");
    private static readonly Guid SmTask = Guid.Parse("5e000000-0000-4000-8000-000000000004");

    private static async Task<ReachSimService> SeedAsync()
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
        // SL 2행(중심 높이 ≈ 1.45 m) — 기본 0.8 m 이격 정차는 경사면 끝단 안쪽이다.
        var v = 1.45 / Math.Sin(Math.PI / 4);
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = SlArea, TankId = "CT1", WallCode = "SL", Level = 1, Name = "L1-SL-R02",
            Corners = JsonSerializer.Serialize(new[] { new[] { 9.3, v - 0.6 }, new[] { 10.7, v - 0.6 }, new[] { 10.7, v + 0.6 }, new[] { 9.3, v + 0.6 } }),
            Tasks = { new AreaTaskEntity { TaskId = SlTask, Seq = 1, StartU = 9.5, StartV = v, EndU = 10.5, EndV = v } },
        });
        // L2 수직벽 SM — 데크(3.63) 위 1.5 m. SM 면 v 는 3.9776 부터.
        var vm = 3.63 + 1.5 - 3.9776;
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = SmArea, TankId = "CT1", WallCode = "SM", Level = 2, Name = "L2-SM-R01",
            Corners = JsonSerializer.Serialize(new[] { new[] { 11.3, vm - 0.6 }, new[] { 12.7, vm - 0.6 }, new[] { 12.7, vm + 0.6 }, new[] { 11.3, vm + 0.6 } }),
            Tasks = { new AreaTaskEntity { TaskId = SmTask, Seq = 1, StartU = 11.8, StartV = vm, EndU = 12.2, EndV = vm } },
        });
        await db.SaveChangesAsync();
        // 조회는 정차 기본 이격만 쓴다 — 허브(SignalR)는 닿지 않는다.
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return new ReachSimService(db, dispatcher);
    }

    [Fact]
    public async Task 조사는_경사면_정차의_차체_간섭과_수직벽_도달을_가른다()
    {
        var sim = await SeedAsync();
        var areas = (await sim.SurveyAsync("CT1", null, null, default))!;

        var sl = areas.Single(a => a.AreaId == SlArea).Seams.Single();
        var sm = areas.Single(a => a.AreaId == SmArea).Seams.Single();
        Assert.NotEqual("Clear", sl.Verdict);
        Assert.Equal("Clear", sm.Verdict);
        Assert.NotNull(sm.StrokeMm);
        Assert.InRange(sm.ZMin, 1.49, 1.51);   // 층 바닥 기준 높이
    }

    [Fact]
    public async Task 조사는_층과_면으로_거른다()
    {
        var sim = await SeedAsync();
        var areas = (await sim.SurveyAsync("CT1", 2, "SM", default))!;
        Assert.Equal(SmArea, Assert.Single(areas).AreaId);
        Assert.Null(await sim.SurveyAsync("NOPE", null, null, default));
    }

    [Fact]
    public async Task 상세는_세_자세와_차체_간섭을_돌려주고_JSON_으로_나간다()
    {
        var sim = await SeedAsync();
        var d = (await sim.DetailAsync(SlArea, SlTask, 1, 700, 0, 0, 0, default))!;

        Assert.True(d.Body.ClearanceM < -0.3);
        Assert.Equal("SL", d.Body.NearestPlane);
        Assert.Equal(700, d.StrokeMm);
        Assert.Equal(new[] { 400.0, 300, 0 }, d.Stages.Select(s => s.StandoffMm));
        var json = JsonSerializer.Serialize(d, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"stages\"", json);
    }

    [Fact]
    public async Task 상세는_리프트를_주지_않으면_조사로_고른다()
    {
        var sim = await SeedAsync();
        var d = (await sim.DetailAsync(SmArea, SmTask, 1, null, 0, 0, 0, default))!;
        var survey = (await sim.SurveyAsync("CT1", 2, "SM", default))!.Single().Seams.Single();

        Assert.Equal(survey.StrokeMm, d.StrokeMm);
        Assert.All(d.Stages, s => Assert.True(s.ChosenIndex >= 0));
        Assert.Null(await sim.DetailAsync(SmArea, SlTask, 1, null, 0, 0, 0, default));
    }
}
