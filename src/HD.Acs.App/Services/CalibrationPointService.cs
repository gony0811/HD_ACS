using HD.Acs.Core.Geometry;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HD.Acs.App.Services;

/// <summary>
/// 저장된 캘리브레이션 기준점(대응쌍) 편집 — 수정·일괄 회전.
///
/// 배경: 종전에는 캡처·삭제뿐이라 "도면 좌표를 잘못된 프레임으로 입력"한 경우
/// 운영자가 전부 지우고 다시 넣는 수밖에 없었다(맵 좌표는 현장 측정값이라 재입력도 위험).
/// 여기서는 <b>맵 좌표는 그대로 두고 도면 좌표만</b> 고치는 경로를 제공한다.
/// 계산(solve)은 호출하지 않는다 — 운영자가 값을 확인한 뒤 명시적으로 다시 계산한다.
/// </summary>
public sealed class CalibrationPointService
{
    private readonly AcsDbContext _db;
    public CalibrationPointService(AcsDbContext db) => _db = db;

    public sealed class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
    }

    public sealed class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }

    /// <summary>기준점 1개 수정 — id 유지. 단위 mm면 도면 좌표를 m로 환산해 저장(캡처와 동일 규칙).</summary>
    public async Task<MapCalibrationPointEntity> UpdateAsync(string mapId, Guid pointId,
        double drawingX, double drawingY, string? unit, double mapX, double mapY, string userId,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(drawingX) || !double.IsFinite(drawingY) || !double.IsFinite(mapX) || !double.IsFinite(mapY))
            throw new ValidationException("좌표는 유한한 숫자여야 합니다.");

        var point = await _db.MapCalibrationPoints.FirstOrDefaultAsync(p => p.Id == pointId && p.MapId == mapId, ct)
            ?? throw new NotFoundException($"기준점 '{pointId}' 없음 (map '{mapId}')");

        double dx = unit == "mm" ? drawingX / 1000.0 : drawingX;
        double dy = unit == "mm" ? drawingY / 1000.0 : drawingY;
        var before = new { point.DrawingXM, point.DrawingYM, point.MapX, point.MapY };

        point.DrawingXM = dx; point.DrawingYM = dy; point.MapX = mapX; point.MapY = mapY;
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId, Action = "CALIBRATION_POINT_UPDATE", Target = mapId,
            Detail = JsonSerializer.Serialize(new { mapId, pointId, before, after = new { dx, dy, mapX, mapY } })
        });
        await _db.SaveChangesAsync(ct);
        return point;
    }

    /// <summary>
    /// 현재 맵버전 기준점 전부의 <b>도면 좌표만</b> 원점 기준 deg 회전(맵 좌표 불변).
    /// 도면 좌표를 통째로 돌아간 프레임으로 입력해 저장된 캘리브레이션의 재측정 없는 교정 경로다
    /// (가장 흔한 사례: 사분면·방향이 모두 정반대 = 180°). 되돌리기는 같은 각도의 반대 부호.
    /// 계산은 하지 않는다 — 회전 후 운영자가 값을 보고 다시 계산한다.
    /// </summary>
    public async Task<int> RotateDrawingAsync(string mapId, double deg, string userId, CancellationToken ct = default)
    {
        if (!double.IsFinite(deg)) throw new ValidationException("회전 각도는 유한한 숫자여야 합니다.");

        var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct)
            ?? throw new NotFoundException($"map '{mapId}' 없음");
        var points = await _db.MapCalibrationPoints
            .Where(p => p.MapId == mapId && p.MapVersion == map.Version).ToListAsync(ct);
        if (points.Count == 0)
            throw new ValidationException($"'{mapId}' 맵버전 {map.Version}에 기준점이 없습니다.");

        foreach (var p in points)
        {
            var (x, y) = DrawingTransform.RotateDrawing(p.DrawingXM, p.DrawingYM, deg);
            p.DrawingXM = x; p.DrawingYM = y;
        }
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId, Action = "CALIBRATION_ROTATE", Target = mapId,
            Detail = JsonSerializer.Serialize(new { mapId, map.Version, deg, pointCount = points.Count })
        });
        await _db.SaveChangesAsync(ct);
        return points.Count;
    }
}
