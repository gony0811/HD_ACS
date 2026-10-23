using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 기준점 편집(수정·도면 좌표 회전) — 운영자가 앱에서 캘리브레이션 입력 오류를 고치는 경로.
/// 현장 사례(CT1-L4, 저장 yaw +1.13°)로 180° 회전이 사분면·방향을 되돌리는지까지 확인한다.
/// </summary>
public class CalibrationEditTests
{
    // 2026-10-23 현장값: 도면 좌표를 180° 돌아간 프레임으로 입력해 저장된 CT1-L4
    private static readonly (double Dx, double Dy, double Mx, double My)[] FieldPoints =
    {
        (-2.012, -0.013, 14.125, 10.288),
        (-6.868, 5.602, 9.312, 15.826),
        (-11.258, -4.568, 5.125, 5.545),
    };

    private static AcsDbContext NewDb(string mapId = "CT1-L4", int version = 1)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Maps.Add(new MapEntity { MapId = mapId, TankId = "CT1", Level = 4, Name = mapId, Version = version });
        foreach (var p in FieldPoints)
            db.MapCalibrationPoints.Add(new MapCalibrationPointEntity
            {
                Id = Guid.NewGuid(), MapId = mapId, MapVersion = version,
                DrawingXM = p.Dx, DrawingYM = p.Dy, MapX = p.Mx, MapY = p.My
            });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Update_KeepsPointId_AndConvertsMillimetres()
    {
        var db = NewDb();
        var svc = new CalibrationPointService(db);
        var target = db.MapCalibrationPoints.First();
        var id = target.Id;

        var updated = await svc.UpdateAsync("CT1-L4", id, 2012, 13, "mm", 14.125, 10.288, "op");

        Assert.Equal(id, updated.Id);                 // 삭제→재등록이 아니라 그 자리 수정
        Assert.Equal(2.012, updated.DrawingXM, 6);    // mm → m
        Assert.Equal(0.013, updated.DrawingYM, 6);
        Assert.Equal(14.125, updated.MapX, 6);        // 맵 좌표(현장 측정값)는 전달값 그대로
        Assert.Equal(3, db.MapCalibrationPoints.Count());
        Assert.Contains(db.AuditLogs, a => a.Action == "CALIBRATION_POINT_UPDATE" && a.Target == "CT1-L4");
    }

    [Fact]
    public async Task Update_Rejects_UnknownPoint_AndNonFiniteValues()
    {
        var db = NewDb();
        var svc = new CalibrationPointService(db);
        var id = db.MapCalibrationPoints.First().Id;

        await Assert.ThrowsAsync<CalibrationPointService.NotFoundException>(
            () => svc.UpdateAsync("CT1-L4", Guid.NewGuid(), 1, 1, "m", 1, 1, "op"));
        await Assert.ThrowsAsync<CalibrationPointService.ValidationException>(
            () => svc.UpdateAsync("CT1-L4", id, double.NaN, 1, "m", 1, 1, "op"));
    }

    [Fact]
    public async Task Rotate180_NegatesDrawingOnly_AndRestoresQuadrantAndHeading()
    {
        var db = NewDb();
        var svc = new CalibrationPointService(db);

        int n = await svc.RotateDrawingAsync("CT1-L4", 180, "op");

        Assert.Equal(3, n);
        var rows = db.MapCalibrationPoints.OrderBy(p => p.MapX).ToList();
        foreach (var r in rows)
        {
            var src = FieldPoints.Single(f => Math.Abs(f.Mx - r.MapX) < 1e-9);
            Assert.Equal(-src.Dx, r.DrawingXM, 9);   // 90° 배수는 정확히 부호만 반전(부동소수 먼지 없음)
            Assert.Equal(-src.Dy, r.DrawingYM, 9);
            Assert.Equal(src.My, r.MapY, 9);         // 맵 좌표 불변
        }
        Assert.Contains(db.AuditLogs, a => a.Action == "CALIBRATION_ROTATE");

        // 회전된 기준점으로 다시 풀면 t·RMS는 그대로, yaw만 180° 이동한다
        var pairs = rows.Select(p => (((double X, double Y))(p.DrawingXM, p.DrawingYM),
                                      ((double X, double Y))(p.MapX, p.MapY))).ToList();
        var (t, rms, _) = HD.Acs.Core.Geometry.DrawingTransform.Solve(pairs);
        Assert.Equal(16.239, t.Tx, 2);   // 화면 저장값 16.2387 · 10.3445 — 평행이동은 회전과 무관하게 불변
        Assert.Equal(10.345, t.Ty, 2);
        Assert.Equal(-3.1219, t.YawRad, 3);          // 저장값 +0.01964 → −3.12194
        Assert.Equal(0.0733, rms, 3);

        // 현장 로봇(SLAM 4.6536, 7.4363 · θ −1.5865)이 선수/좌현·좌현 방향으로 돌아오는지
        var (dx, dy) = t.MapToDrawing(4.6536, 7.4363);
        Assert.True(dx > 0 && dy > 0, $"선수/좌현이어야 함: ({dx:F2}, {dy:F2})");
        Assert.Equal(11.64, dx, 2);
        Assert.Equal(2.68, dy, 2);
        double headingDeg = t.MapYawToDrawing(-1.5865) * 180 / Math.PI;
        Assert.InRange(headingDeg, 45, 135);          // 좌현
    }

    [Fact]
    public async Task Rotate_Twice_ReturnsToOriginal_AndGuardsEmptyOrUnknownMap()
    {
        var db = NewDb();
        var svc = new CalibrationPointService(db);
        await svc.RotateDrawingAsync("CT1-L4", 180, "op");
        await svc.RotateDrawingAsync("CT1-L4", -180, "op");
        foreach (var r in db.MapCalibrationPoints.ToList())
        {
            var src = FieldPoints.Single(f => Math.Abs(f.Mx - r.MapX) < 1e-9);
            Assert.Equal(src.Dx, r.DrawingXM, 9);
            Assert.Equal(src.Dy, r.DrawingYM, 9);
        }

        await Assert.ThrowsAsync<CalibrationPointService.NotFoundException>(
            () => svc.RotateDrawingAsync("CT1-L9", 180, "op"));

        db.MapCalibrationPoints.RemoveRange(db.MapCalibrationPoints);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<CalibrationPointService.ValidationException>(
            () => svc.RotateDrawingAsync("CT1-L4", 180, "op"));
    }

    [Fact]
    public async Task Rotate_SkipsOtherMapVersions()
    {
        var db = NewDb();
        // 구 맵버전 기준점은 현재 캘리브레이션과 무관 — 건드리지 않는다
        db.MapCalibrationPoints.Add(new MapCalibrationPointEntity
        {
            Id = Guid.NewGuid(), MapId = "CT1-L4", MapVersion = 0,
            DrawingXM = 1, DrawingYM = 2, MapX = 3, MapY = 4
        });
        await db.SaveChangesAsync();

        int n = await new CalibrationPointService(db).RotateDrawingAsync("CT1-L4", 180, "op");

        Assert.Equal(3, n);
        var old = db.MapCalibrationPoints.Single(p => p.MapVersion == 0);
        Assert.Equal(1, old.DrawingXM, 9);
        Assert.Equal(2, old.DrawingYM, 9);
    }
}
