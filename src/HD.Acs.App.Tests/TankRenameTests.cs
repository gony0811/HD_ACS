using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>선창 ID 변경 — 형상·면·영역·시나리오·층 맵과 그 맵을 가리키는 것들이 함께 옮겨 가고 영역 ID·taskId 는 그대로.</summary>
public class TankRenameTests
{
    private sealed record Seeded(AcsDbContext Db, TankRenameService Svc, Guid AreaId, Guid TaskId, Guid ScenarioId, Guid RunId, Guid PointId);

    private static async Task<Seeded> Seed(string runState = "COMPLETED")
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LengthL = 45, WFloor = 8.2, LevelZ = "[0,2.4,4.8,7.2]", CreatedBy = "kim" });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        for (var lv = 1; lv <= 4; lv++)
            db.Maps.Add(new MapEntity { MapId = $"CT1-L{lv}", TankId = "CT1", Level = lv, Name = $"CT1 L{lv}", Version = lv == 4 ? 3 : 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L4", MapVersion = 3, Tx = 11.9, Ty = 16.6, YawRad = 1.574, RmsM = 0.02, PointCount = 3 });
        var pointId = Guid.NewGuid();
        db.MapCalibrationPoints.Add(new MapCalibrationPointEntity { Id = pointId, MapId = "CT1-L4", MapVersion = 3 });
        db.Nodes.Add(new NodeEntity { NodeId = "SWAP-L4", MapId = "CT1-L4", NodeType = "BATTERY_SWAP" });

        var task = new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 3.1, StartV = 1.64, EndU = 3.1, EndV = 2.0, SectionDxfId = "D", ProfileId = "P" };
        var area = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 4, Name = "L4-R01C01",
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationLiftMm = 100, Tasks = { task },
        };
        db.InspectionAreas.Add(area);
        var scenario = new ScenarioEntity { ScenarioId = Guid.NewGuid(), TankId = "CT1", Name = "전체" };
        db.Scenarios.Add(scenario);
        db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = scenario.ScenarioId, AreaId = area.AreaId });

        var run = new ScenarioRunEntity { RunId = Guid.NewGuid(), ScenarioId = scenario.ScenarioId, RobotId = "AMR-01", State = runState };
        run.Missions.Add(new MissionEntity { MissionId = Guid.NewGuid(), RunId = run.RunId, RobotId = "AMR-01", MapId = "CT1-L4", Seq = 1 });
        db.ScenarioRuns.Add(run);
        db.WorkItems.Add(new WorkItemEntity
        {
            WorkItemId = Guid.NewGuid(), RunId = run.RunId, AreaId = area.AreaId, MapId = "CT1-L4", Status = "DONE",
            Actions = "[{\"jobRef\":\"JOB-CT1-L4-F-L4-R01C01-1\"}]",
        });
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ReportedMapId = "CT1-L4", ManualMapId = "CT1-L4" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(db, new TankRenameService(db, NullLogger<TankRenameService>.Instance),
            area.AreaId, task.TaskId, scenario.ScenarioId, run.RunId, pointId);
    }

    [Fact]
    public async Task Rename_MovesEverythingToTheNewId_AndKeepsAreaAndTaskIds()
    {
        var s = await Seed();

        var r = await s.Svc.RenameAsync("CT1", " HN8250-CT2 ", "kim");

        Assert.Equal(("CT1", "HN8250-CT2", 10, 1, 1, 1, 1, 1, 1, 1),
            (r.OldTankId, r.NewTankId, r.Walls, r.Areas, r.Scenarios, r.Calibrations, r.CalibrationPoints, r.Missions, r.WorkItems, r.Robots));
        Assert.Equal("HN8250-CT2-L4", r.Maps["CT1-L4"]);
        var db = s.Db;
        db.ChangeTracker.Clear();

        // 옛 ID 는 어디에도 남지 않는다
        Assert.False(await db.TankGeometries.AnyAsync(g => g.TankId == "CT1"));
        Assert.False(await db.Walls.AnyAsync(w => w.TankId == "CT1"));
        Assert.False(await db.Maps.AnyAsync(m => m.TankId == "CT1" || m.MapId.StartsWith("CT1-")));
        Assert.False(await db.MapCalibrations.AnyAsync(c => c.MapId.StartsWith("CT1-")));

        // 형상·면·맵·캘리브레이션은 값 그대로 새 ID 로
        var g = await db.TankGeometries.SingleAsync();
        Assert.Equal(("HN8250-CT2", 45.0, 8.2, "[0,2.4,4.8,7.2]", "kim"), (g.TankId, g.LengthL, g.WFloor, g.LevelZ, g.CreatedBy));
        Assert.Equal(10, await db.Walls.CountAsync(w => w.TankId == "HN8250-CT2"));
        var map = await db.Maps.SingleAsync(m => m.MapId == "HN8250-CT2-L4");
        Assert.Equal(("HN8250-CT2", 4, 3, "HN8250-CT2 L4"), (map.TankId, map.Level, map.Version, map.Name));   // 맵 버전 유지 → 캘리브레이션이 계속 유효
        var cal = await db.MapCalibrations.SingleAsync();
        Assert.Equal(("HN8250-CT2-L4", 3, 11.9, 16.6, 1.574, 3), (cal.MapId, cal.MapVersion, cal.Tx, cal.Ty, cal.YawRad, cal.PointCount));
        Assert.Equal("HN8250-CT2-L4", (await db.MapCalibrationPoints.SingleAsync(p => p.Id == s.PointId)).MapId);
        Assert.Equal("HN8250-CT2-L4", (await db.Nodes.SingleAsync()).MapId);

        // 영역 ID·taskId·정차 리프트·시나리오 연결은 그대로
        var area = await db.InspectionAreas.Include(a => a.Tasks).SingleAsync();
        Assert.Equal((s.AreaId, "HN8250-CT2", "F", 100.0), (area.AreaId, area.TankId, area.WallCode, area.StationLiftMm!.Value));
        Assert.Equal(s.TaskId, area.Tasks.Single().TaskId);
        Assert.Equal("HN8250-CT2", (await db.Scenarios.SingleAsync(x => x.ScenarioId == s.ScenarioId)).TankId);
        Assert.True(await db.ScenarioAreas.AnyAsync(x => x.ScenarioId == s.ScenarioId && x.AreaId == s.AreaId));

        // 지난 run 의 층·정차 큐·로봇 보고 층도 새 맵을 가리키고, 배차 기록의 jobRef 문자열은 그대로(기록)
        Assert.Equal("HN8250-CT2-L4", (await db.Missions.SingleAsync()).MapId);
        var wi = await db.WorkItems.SingleAsync();
        Assert.Equal("HN8250-CT2-L4", wi.MapId);
        Assert.Contains("JOB-CT1-L4-F-L4-R01C01-1", wi.Actions);
        var ctx = await db.RobotContexts.SingleAsync();
        Assert.Equal(("HN8250-CT2-L4", "HN8250-CT2-L4"), (ctx.ReportedMapId, ctx.ManualMapId));

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "TANK_RENAME");
        Assert.Equal(("kim", "HN8250-CT2"), (audit.UserId, audit.Target));
        Assert.Contains("\"OldTankId\":\"CT1\"", audit.Detail);

        // 되돌릴 수 있다
        await s.Svc.RenameAsync("HN8250-CT2", "CT1", null);
        db.ChangeTracker.Clear();
        Assert.Equal("CT1", (await db.InspectionAreas.SingleAsync()).TankId);
        Assert.Equal("CT1-L4", (await db.MapCalibrations.SingleAsync()).MapId);
    }

    [Fact]
    public async Task Rename_IsRefused_WithoutTouchingData_WhenIdIsBadTakenMissingOrARunIsActive()
    {
        var s = await Seed(runState: "WAITING_FLOOR_TRANSFER");
        s.Db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT2" });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<TankRenameService.InvalidException>(() => s.Svc.RenameAsync("CT1", "", null));
        await Assert.ThrowsAsync<TankRenameService.InvalidException>(() => s.Svc.RenameAsync("CT1", "선창 2", null));
        await Assert.ThrowsAsync<TankRenameService.InvalidException>(() => s.Svc.RenameAsync("CT1", "CT1", null));
        await Assert.ThrowsAsync<TankRenameService.InvalidException>(() => s.Svc.RenameAsync("CT1", "HULL-L4", null));   // 층 표시와 겹침
        await Assert.ThrowsAsync<TankRenameService.NotFoundException>(() => s.Svc.RenameAsync("NOPE", "CT9", null));
        await Assert.ThrowsAsync<TankRenameService.ConflictException>(() => s.Svc.RenameAsync("CT1", "CT2", null));      // 이미 있는 선창
        var ex = await Assert.ThrowsAsync<TankRenameService.ConflictException>(() => s.Svc.RenameAsync("CT1", "CT9", null));
        Assert.Contains("진행 중인 run", ex.Message);

        s.Db.ChangeTracker.Clear();
        Assert.Equal("CT1", (await s.Db.InspectionAreas.SingleAsync()).TankId);
        Assert.Equal(4, await s.Db.Maps.CountAsync(m => m.TankId == "CT1"));
        Assert.False(await s.Db.AuditLogs.AnyAsync(a => a.Action == "TANK_RENAME"));
    }

    [Theory]
    [InlineData("CT1", null)]
    [InlineData("HN8250-CT2", null)]
    [InlineData("tank_02", null)]
    [InlineData("", "비어")]
    [InlineData("-CT1", "영문·숫자로 시작")]
    [InlineData("CT 1", "영문·숫자로 시작")]
    [InlineData("CT1/L1", "영문·숫자로 시작")]
    [InlineData("A-L2", "-L숫자")]
    [InlineData("123456789012345678901234567890123", "1~32자")]
    public void IdProblem_AllowsLettersDigitsUnderscoreHyphen(string id, string? expected)
    {
        var p = TankRenameService.IdProblem(id);
        if (expected is null) Assert.Null(p); else Assert.Contains(expected, p);
    }

    [Theory]
    [InlineData("CT1-L4", 4)]
    [InlineData("HN8250-CT2-L12", 12)]
    [InlineData("L3", 3)]
    [InlineData("l2", 2)]
    [InlineData("1", 1)]
    [InlineData("CT1", null)]
    [InlineData("CT1-L0", null)]
    [InlineData("CT1-LX", null)]
    [InlineData("", null)]
    public void ReportedMap_LevelOf_ReadsTheFloorNumber(string mapId, int? expected) =>
        Assert.Equal(expected, ReportedMap.LevelOf(mapId));

    [Fact]
    public async Task ReportedMap_IsMatchedByFloorNumber_WhenTheRobotStillReportsTheOldTankName()
    {
        var s = await Seed();
        await s.Svc.RenameAsync("CT1", "CT9", null);
        var db = s.Db;

        Assert.Equal("CT9-L4", await ReportedMap.ResolveAsync(db, "CT9-L4"));   // 등록된 맵은 그대로
        Assert.Equal("CT9-L4", await ReportedMap.ResolveAsync(db, "CT1-L4"));   // 옛 이름 → 층 번호로
        Assert.Equal("CT9-L2", await ReportedMap.ResolveAsync(db, "L2"));       // 층만 보고
        Assert.Equal("CT1-L7", await ReportedMap.ResolveAsync(db, "CT1-L7"));   // 그 층 맵이 없으면 보고값 그대로(층 불일치로 드러난다)
        Assert.Equal("어딘가", await ReportedMap.ResolveAsync(db, "어딘가"));
        Assert.Null(await ReportedMap.ResolveAsync(db, null));

        // 같은 층 번호의 맵이 둘(선창 둘)이면 정할 수 없다 — 보고값 그대로
        db.Maps.Add(new MapEntity { MapId = "CT2-L4", TankId = "CT2", Level = 4, Name = "CT2 L4" });
        await db.SaveChangesAsync();
        Assert.Equal("CT1-L4", await ReportedMap.ResolveAsync(db, "CT1-L4"));
        Assert.Equal("CT9-L2", await ReportedMap.ResolveAsync(db, "CT1-L2"));
    }
}
