using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>GET /api/robots/{id}/drawing-pose — 로봇 보고 위치를 보고 층 T_W_D로 도면 좌표화(현재 위치를 정차점으로).</summary>
public class RobotPoseTests
{
    private static readonly DrawingTransform T = new(16.0, 9.0, -3.13);

    private static AcsDbContext Seed(int calVersion = 1, string? mapId = "CT1-L2", double? mx = null, double? my = null)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Maps.Add(new MapEntity { MapId = "CT1-L2", TankId = "CT1", Level = 2, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L2", MapVersion = calVersion, Tx = T.Tx, Ty = T.Ty, YawRad = T.YawRad });
        var (x, y) = T.DrawingToMap(2.52, 7.04);
        db.RobotContexts.Add(new RobotContextEntity
        {
            RobotId = "AMR-01", ReportedMapId = mapId, ReportedX = mx ?? x, ReportedY = my ?? y,
            ReportedTheta = T.DrawingYawToMap(Math.PI / 2), ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task ReturnsDrawingPose_RoundTripOfDispatchTransform()
    {
        var p = await new RobotPoseService(Seed()).GetDrawingPoseAsync("AMR-01");
        Assert.Equal(("CT1-L2", 2), (p.MapId, p.Level));
        Assert.Equal(2.52, p.X, 9);
        Assert.Equal(7.04, p.Y, 9);
        Assert.Equal(Math.PI / 2, p.Theta!.Value, 9);
    }

    [Fact]
    public async Task Guards()
    {
        await Assert.ThrowsAsync<RobotPoseService.NotFoundException>(() => new RobotPoseService(Seed()).GetDrawingPoseAsync("AMR-99"));
        await Assert.ThrowsAsync<RobotPoseService.ConflictException>(() => new RobotPoseService(Seed(mapId: null)).GetDrawingPoseAsync("AMR-01"));
        await Assert.ThrowsAsync<CalibrationInvalidException>(() => new RobotPoseService(Seed(calVersion: 0)).GetDrawingPoseAsync("AMR-01"));
    }
}
