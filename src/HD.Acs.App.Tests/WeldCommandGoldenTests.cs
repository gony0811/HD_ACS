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
/// 용접검사 지령 고정본 — 지령 만들기를 검사 종류별 구현으로 나눈 뒤에도 큐에 저장되는 액션(JSON)이
/// 나누기 전과 글자 그대로 같아야 한다. 이 문자열은 분리 전 코드로도 같은 값이 나오는 것을 확인하고 고정했다.
/// 값이 바뀌면 HD_AMR 로 나가는 지령이 바뀐 것이다 — VDA 사양서와 함께 고칠 것.
/// </summary>
public class WeldCommandGoldenTests
{
    private const string Golden =
        """[{"actionType":"startWeldInspection","taskId":"3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47","jobRef":"JOB-CT1-L1-F-F-01-1","position":{"seamStartW":[31.85,15.3,1.64],"seamEndW":[31.85,15.3,2],"drawingPos":{"tank":"CT1","level":1,"wall_code":"F","u":3,"v":1.64,"x":22.5,"y":3,"z":1.64,"uEnd":3,"vEnd":2}},"params":{"seamType":"LINE","sectionDxfId":"DXF-1","inspectionProfileId":"PROF-1","standoffMm":400,"workingDistanceMm":400,"anchorGroupId":"CT1-L1-F-F-01","seqInGroup":1,"liftStrokeMm":150,"taskId":"3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47"}},{"actionType":"startWeldInspection","taskId":"3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48","jobRef":"JOB-CT1-L1-F-F-01-2","position":{"seamStartW":[31.85,15,1.8],"seamEndW":[31.85,15,1.8],"drawingPos":{"tank":"CT1","level":1,"wall_code":"F","u":3.3,"v":1.8,"x":22.5,"y":2.7,"z":1.8,"uEnd":3.3,"vEnd":1.8}},"params":{"seamType":"CROSS4","sectionDxfId":"DXF-1","inspectionProfileId":"PROF-1","standoffMm":400,"workingDistanceMm":400,"anchorGroupId":"CT1-L1-F-F-01","seqInGroup":2,"liftStrokeMm":150,"taskId":"3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48","points":[[99.99999999999964,0],[0,100.00000000000009],[-100.00000000000009,0],[0,-99.99999999999987]]}}]""";

    [Fact]
    public async Task QueuedWeldActions_AreUnchanged()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = 9.35, Ty = 12.3, YawRad = 0 });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.8, StationLiftMm = 150,
            Tasks =
            {
                new AreaTaskEntity
                {
                    TaskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47"), Seq = 1, SeamType = "LINE",
                    StartU = 3.0, StartV = 1.64, EndU = 3.0, EndV = 2.0, SectionDxfId = "DXF-1", ProfileId = "PROF-1",
                },
                new AreaTaskEntity
                {
                    TaskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48"), Seq = 2, SeamType = "CROSS4",
                    StartU = 3.3, StartV = 1.8, EndU = 3.3, EndV = 1.8, SectionDxfId = "DXF-1", ProfileId = "PROF-1",
                    Points = "[[3.4,1.8],[3.3,1.9],[3.2,1.8],[3.3,1.7]]",
                },
            },
        });
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);

        var runId = await missions.StartRunAsync(sc.ScenarioId, "AMR-01");

        var wi = await db.WorkItems.AsNoTracking().SingleAsync(w => w.RunId == runId);
        Assert.Equal(Golden, wi.Actions);
    }
}
