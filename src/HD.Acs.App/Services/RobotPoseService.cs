using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>로봇 보고 위치를 도면 좌표로 — 계획 화면에서 "현재 AMR 위치를 정차점으로" 쓸 때의 정본 변환.</summary>
public sealed record RobotDrawingPose(
    string RobotId, string MapId, int Level,
    double X, double Y, double? Theta,          // 도면 [m]·[rad]
    double MapX, double MapY, double? MapTheta, // AMR 보고 원본
    string? ConnectionState, DateTimeOffset? ReportedAt);

/// <summary>
/// 로봇 최신 보고 위치(robot_context)에 그 층의 유효 T_W_D 역변환을 적용한다.
/// 배차(도면→맵)와 같은 <see cref="WeldInspectionPayload.ResolveTransform"/> 를 거쳐 맵버전이 다르면 거부한다 —
/// 이 값을 정차점으로 저장하면 배차가 다시 같은 T_W_D로 맵 좌표를 만들므로 왕복이 일치한다.
/// </summary>
public sealed class RobotPoseService(AcsDbContext db)
{
    public sealed class NotFoundException(string m) : Exception(m);
    public sealed class ConflictException(string m) : Exception(m);

    public async Task<RobotDrawingPose> GetDrawingPoseAsync(string robotId, CancellationToken ct = default)
    {
        var ctx = await db.RobotContexts.AsNoTracking().FirstOrDefaultAsync(c => c.RobotId == robotId, ct)
            ?? throw new NotFoundException($"로봇 '{robotId}' 보고 기록이 없습니다.");
        if (ctx.ReportedMapId is not { } mapId)
            throw new ConflictException("로봇 보고 층(mapId)이 없습니다 — 로봇 연결 상태를 확인하세요.");
        if (ctx.ReportedX is not double mx || ctx.ReportedY is not double my)
            throw new ConflictException("로봇 보고 위치가 없습니다 — 위치 추정(측위) 상태를 확인하세요.");

        var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct)
            ?? throw new NotFoundException($"map '{mapId}' 없음");
        var cal = await db.MapCalibrations.AsNoTracking().Where(c => c.MapId == mapId)
            .OrderByDescending(c => c.MapVersion).FirstOrDefaultAsync(ct);
        DrawingTransform t = WeldInspectionPayload.ResolveTransform(   // 무효면 CalibrationInvalidException
            map.Version, cal?.MapVersion, cal?.Tx ?? 0, cal?.Ty ?? 0, cal?.YawRad ?? 0);

        var (x, y) = t.MapToDrawing(mx, my);
        double? theta = ctx.ReportedTheta is double th ? t.MapYawToDrawing(th) : null;
        return new RobotDrawingPose(robotId, mapId, map.Level, x, y, theta, mx, my, ctx.ReportedTheta,
            ctx.ConnectionState, ctx.ReportedAt);
    }
}
