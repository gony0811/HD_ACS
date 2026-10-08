using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>GET /api/scenarios/{id}/area-stations — 운영 화면 "계획 정차점까지 거리"의 기준 정차점.</summary>
public class PlannedStationTests
{
    private static AcsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static InspectionDispatcher Dispatcher(AcsDbContext db) =>
        new(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);

    private static async Task<(Guid Scenario, InspectionAreaEntity F, InspectionAreaEntity Pm)> Seed(AcsDbContext db, bool link)
    {
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = -0.0027 });

        InspectionAreaEntity Area(string wall, string name, double? standoff) => new()
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = wall, Level = 1, Name = name,
            Corners = "[[4.91,1.54],[5.11,1.54],[5.11,2.1],[4.91,2.1]]", StationStandoffM = standoff,
            Tasks = { new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 5.01, StartV = 1.64, EndU = 5.01, EndV = 2.0,
                SectionDxfId = "DXF-1", ProfileId = "PROF-1" } },
        };
        var f = Area("F", "F-01", 1.8);
        var pm = Area("PM", "PM-01", null);
        db.InspectionAreas.AddRange(f, pm);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        if (link) db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = sc.ScenarioId, AreaId = f.AreaId });
        await db.SaveChangesAsync();
        return (sc.ScenarioId, f, pm);
    }

    [Fact]
    public async Task Stations_MatchDispatchedStopExactly_AndFollowScenarioAreas()
    {
        var db = NewDb();
        var (scId, f, _) = await Seed(db, link: true);
        var d = Dispatcher(db);

        var plan = await d.GetPlannedStationsAsync(scId, default);
        var st = Assert.Single(plan!.Stations);   // 연결 영역만
        Assert.Equal((f.AreaId, "CT1-L1", 1.8, false), (st.AreaId, st.MapId, st.StandoffM, st.Manual));
        Assert.Equal((0.08, 0.07), (plan.AllowedDevXy, plan.AllowedDevTheta));
        // 면 F: 법선 −x(선미 향, 내부), u축 수평 단위벡터 존재
        Assert.Equal(-1, st.WallNormal![0], 6);
        Assert.NotNull(st.WallU);

        // 같은 시나리오로 큐를 전개하면 work_item 정차점(맵) = 계획 정차점(도면)에 T_W_D 적용한 값
        var run = new ScenarioRunEntity { RunId = Guid.NewGuid(), ScenarioId = scId, RobotId = "AMR-01" };
        db.ScenarioRuns.Add(run);
        await d.BuildQueueAsync(run, "CT1", default);
        await db.SaveChangesAsync();
        var wi = Assert.Single(await db.WorkItems.AsNoTracking().ToListAsync());
        var (mx, my) = new DrawingTransform(9.35, 12.3, -0.0027).DrawingToMap(st.X, st.Y);
        Assert.Equal(wi.X, mx, 9);
        Assert.Equal(wi.Y, my, 9);
        Assert.Equal(wi.Theta!.Value, new DrawingTransform(9.35, 12.3, -0.0027).DrawingYawToMap(st.Yaw!.Value), 9);
    }

    [Fact]
    public async Task UnlinkedScenario_UsesWholeTank_ManualOverrideWins_UnknownIsNull()
    {
        var db = NewDb();
        var (scId, f, pm) = await Seed(db, link: false);
        f.StationX = 1.0; f.StationY = -2.0; f.StationTheta = 0.5;
        await db.SaveChangesAsync();
        var d = Dispatcher(db);

        var plan = await d.GetPlannedStationsAsync(scId, default);
        Assert.Equal(2, plan!.Stations.Count);   // 연결 0건 = 선창 전체
        var fs = plan.Stations.Single(s => s.AreaId == f.AreaId);
        Assert.Equal((1.0, -2.0, 0.5, true), (fs.X, fs.Y, fs.Yaw!.Value, fs.Manual));
        Assert.Equal(0.8, plan.Stations.Single(s => s.AreaId == pm.AreaId).StandoffM);   // 기본 이격

        Assert.Null(await d.GetPlannedStationsAsync(Guid.NewGuid(), default));
    }
}
