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

    /// <summary>계산 결과. Model = "rigid" | "affine". RigidRmsM 은 같은 점을 강체로 풀었을 때의 잔차(비교용).</summary>
    public sealed record SolveResult(string Model, DrawingTransform T, double RmsM, double MaxResidualM,
        int PointCount, double RigidRmsM, double? LooRmsM, double ScaleMin, double ScaleMax, string? Warning);

    /// <summary>
    /// 현재 맵버전 기준점으로 T_W_D 를 풀어 저장한다.
    /// model: "rigid"(회전·이동) / "affine"(축별 축척·전단 포함 — 맵이 실제보다 늘거나 줄어든 층) /
    /// null(이 맵에 저장된 모델 유지, 없으면 rigid). RMS 가 임계값을 넘어도 저장하고 경고를 돌려준다.
    /// </summary>
    public async Task<SolveResult> SolveAsync(string mapId, string? model, double rmsWarnM, string? userId = null,
        CancellationToken ct = default)
    {
        var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct)
            ?? throw new NotFoundException($"map '{mapId}' 없음");
        var points = await _db.MapCalibrationPoints.AsNoTracking()
            .Where(p => p.MapId == mapId && p.MapVersion == map.Version).ToListAsync(ct);
        if (points.Count < 2)
            throw new ValidationException("대응쌍이 2개 미만입니다. 최소 2점(권장 3점) 캡처 후 계산하세요.");

        var cal = await _db.MapCalibrations.FindAsync(new object[] { mapId, map.Version }, ct);
        string requested = (model ?? (cal?.A11 is not null ? "affine" : "rigid")).Trim().ToLowerInvariant();
        if (requested is not ("rigid" or "affine"))
            throw new ValidationException($"model 은 rigid 또는 affine 이어야 합니다(받은 값: '{model}').");

        var pairs = points
            .Select(p => (((double X, double Y))(p.DrawingXM, p.DrawingYM), ((double X, double Y))(p.MapX, p.MapY)))
            .ToList();
        var rigid = DrawingTransform.Solve(pairs);
        var fit = rigid;
        if (requested == "affine")
        {
            try { fit = DrawingTransform.SolveAffine(pairs); }
            catch (ArgumentException ex) { throw new ValidationException(ex.Message.Split(" (Parameter")[0]); }
        }
        var t = fit.T;
        double? loo = DrawingTransform.LeaveOneOutRms(pairs, t.IsAffine);
        var (sMin, sMax) = t.Scales();

        cal ??= _db.MapCalibrations.Add(new MapCalibrationEntity { MapId = mapId, MapVersion = map.Version }).Entity;
        cal.Tx = t.Tx; cal.Ty = t.Ty; cal.YawRad = t.YawRad;
        cal.A11 = t.Affine?.A11; cal.A12 = t.Affine?.A12; cal.A21 = t.Affine?.A21; cal.A22 = t.Affine?.A22;
        cal.RmsM = fit.RmsM; cal.PointCount = points.Count; cal.RegisteredAt = DateTimeOffset.UtcNow;
        if (userId is not null) cal.RegisteredBy = userId;
        await _db.SaveChangesAsync(ct);

        var warns = new List<string>();
        if (fit.RmsM > rmsWarnM)
            warns.Add($"등록 잔차 RMS {fit.RmsM:F4}m 가 임계값 {rmsWarnM:F3}m 를 초과합니다. 기준점 재캡처를 권장합니다.");
        if (t.IsAffine)
        {
            if (points.Count < DrawingTransform.AffineMinPairs + 2)
                warns.Add($"affine 은 기준점이 적으면 실제보다 잘 맞아 보입니다(현재 {points.Count}점) — 6점 이상을 길이·폭 양쪽으로 넓게 두세요.");
            if (loo is double l && l > Math.Max(rmsWarnM, 2 * fit.RmsM))
                warns.Add($"한 점씩 빼고 예측한 오차가 {l:F3}m 입니다 — 기준점이 없는 구역에서는 이 정도 어긋날 수 있습니다.");
            if (sMin < 0.97 || sMax > 1.03)
                warns.Add($"축척이 {sMin:F4}~{sMax:F4} 로 3% 넘게 벗어납니다 — 기준점 입력 오류나 맵 해상도를 확인하세요.");
        }
        return new SolveResult(t.IsAffine ? "affine" : "rigid", t, fit.RmsM, fit.MaxResidualM, points.Count,
            rigid.RmsM, loo, sMin, sMax, warns.Count == 0 ? null : string.Join(" ", warns));
    }
}
