using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>캘리브레이션 계산의 모델 선택(강체 / affine)과 저장 — 현장 CT1-L3 기준점 5개.</summary>
public class CalibrationAffineTests
{
    private static readonly (double Dx, double Dy, double Mx, double My)[] Field =
    {
        (-9.000, 6.840, 7.263, 16.549),
        (-0.360, 6.840, 15.919, 16.562),
        (11.160, -6.800, 27.598, 2.852),
        (6.840, -6.800, 23.261, 2.782),
        (-7.380, -6.850, 8.820, 2.596),
    };

    private static AcsDbContext NewDb(int points = 5)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Maps.Add(new MapEntity { MapId = "CT1-L3", TankId = "CT1", Level = 3, Name = "CT1-L3", Version = 1 });
        foreach (var p in Field.Take(points))
            db.MapCalibrationPoints.Add(new MapCalibrationPointEntity
            {
                Id = Guid.NewGuid(), MapId = "CT1-L3", MapVersion = 1,
                DrawingXM = p.Dx, DrawingYM = p.Dy, MapX = p.Mx, MapY = p.My
            });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Affine_IsSaved_AndReportsComparisonWithRigid()
    {
        using var db = NewDb();
        var svc = new CalibrationPointService(db);

        var r = await svc.SolveAsync("CT1-L3", "affine", rmsWarnM: 0.05);

        Assert.Equal("affine", r.Model);
        Assert.Equal(0.040, r.RmsM, 3);
        Assert.Equal(0.133, r.RigidRmsM, 3);
        Assert.NotNull(r.LooRmsM);
        Assert.Contains("기준점이 적으면", r.Warning);   // 5점 — 6점 이상 권장 안내

        var cal = await db.MapCalibrations.AsNoTracking().SingleAsync();
        Assert.NotNull(cal.A11);
        Assert.Equal(r.T.Affine!.A22, cal.A22!.Value, 12);
        Assert.Equal(5, cal.PointCount);
    }

    [Fact]
    public async Task ModelOmitted_KeepsStoredModel_AndRigidClearsAffine()
    {
        using var db = NewDb();
        var svc = new CalibrationPointService(db);

        Assert.Equal("rigid", (await svc.SolveAsync("CT1-L3", null, 0.05)).Model);   // 저장된 것 없음 → 강체
        await svc.SolveAsync("CT1-L3", "affine", 0.05);
        Assert.Equal("affine", (await svc.SolveAsync("CT1-L3", null, 0.05)).Model);  // 저장된 모델 유지

        var back = await svc.SolveAsync("CT1-L3", "rigid", 0.05);
        Assert.Equal("rigid", back.Model);
        Assert.Equal(0.133, back.RmsM, 3);
        var cal = await db.MapCalibrations.AsNoTracking().SingleAsync();
        Assert.Null(cal.A11); Assert.Null(cal.A12); Assert.Null(cal.A21); Assert.Null(cal.A22);
    }

    [Fact]
    public async Task Affine_WithThreePoints_IsRejected_AndNothingIsSaved()
    {
        using var db = NewDb(points: 3);
        var svc = new CalibrationPointService(db);

        var ex = await Assert.ThrowsAsync<CalibrationPointService.ValidationException>(
            () => svc.SolveAsync("CT1-L3", "affine", 0.05));
        Assert.Contains("최소 4개", ex.Message);
        Assert.Empty(db.MapCalibrations);

        await Assert.ThrowsAsync<CalibrationPointService.ValidationException>(() => svc.SolveAsync("CT1-L3", "bilinear", 0.05));
        await Assert.ThrowsAsync<CalibrationPointService.NotFoundException>(() => svc.SolveAsync("CT1-L9", "rigid", 0.05));
    }

    [Fact]
    public async Task RobotDrawingPose_UsesStoredAffine()
    {
        using var db = NewDb();
        await new CalibrationPointService(db).SolveAsync("CT1-L3", "affine", 0.05);
        db.Robots.Add(new RobotEntity { RobotId = "AMR-01", Manufacturer = "HD", SerialNumber = "AMR-01" });
        db.RobotContexts.Add(new RobotContextEntity
        {
            RobotId = "AMR-01", ReportedMapId = "CT1-L3", ReportedX = 8.820, ReportedY = 2.596, ReportedTheta = 0
        });
        db.SaveChanges();

        var pose = await new RobotPoseService(db).GetDrawingPoseAsync("AMR-01");

        // 줄자로 잰 도면 좌표 (−7.38, −6.85) 와 3 cm 안에서 맞는다(강체로는 0.2 m 어긋났던 점)
        Assert.InRange(Math.Sqrt(Math.Pow(pose.X + 7.38, 2) + Math.Pow(pose.Y + 6.85, 2)), 0, 0.035);
    }
}
