using HD.Acs.UI.Models;

namespace HD.Acs.UI.ViewModels;

/// <summary>로봇 현재 위치 ↔ 계획 정차점 비교 결과(도면 프레임 — T_W_D는 강체라 거리는 맵 프레임과 같다).</summary>
/// <param name="AlongWallM">면 u축 방향 성분(+ = 면 u 방향). 수평 u축 없는 면(B/T)은 null.</param>
/// <param name="WallDistanceM">로봇 중심~벽면 수평 거리(계획 = StandoffM). 수평 법선 없는 면은 null.</param>
/// <param name="HeadingErrorRad">로봇 heading − 계획 yaw ((−π, π]). 어느 쪽이든 없으면 null.</param>
public sealed record StationDeviationResult(
    PlannedStationDto Station, bool IsCurrentTask,
    double DistanceM, double? AlongWallM, double? WallDistanceM, double? HeadingErrorRad,
    bool WithinTolerance);

/// <summary>
/// 운영 화면 로봇 상태 카드의 "계획 정차점까지 거리" 계산(순수 함수). 대상 정차점 선택:
/// 진행 중 작업(배차된 영역)의 정차점이 로봇과 같은 층이면 그것, 아니면 같은 층 계획 정차점 중 가장 가까운 것.
/// 도착 판정은 서버 허용 오차(AllowedDevXy/Theta)와 같은 기준.
/// </summary>
public static class StationDeviation
{
    public static StationDeviationResult? Evaluate(
        double robotX, double robotY, double? robotYaw, string? robotMapId,
        PlannedStationsDto? plan, Guid? currentAreaId)
    {
        if (plan is null || robotMapId is null) return null;
        var onFloor = plan.Stations.Where(s => s.MapId == robotMapId).ToList();
        if (onFloor.Count == 0) return null;

        var current = currentAreaId is { } id ? onFloor.FirstOrDefault(s => s.AreaId == id) : null;
        var target = current ?? onFloor.MinBy(s => Dist2(s, robotX, robotY))!;

        double dx = robotX - target.X, dy = robotY - target.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double? along = target.WallU is { Length: >= 2 } u ? dx * u[0] + dy * u[1] : null;
        double? wallDist = target.WallNormal is { Length: >= 2 } n ? target.StandoffM + dx * n[0] + dy * n[1] : null;
        double? dYaw = robotYaw is double ry && target.Yaw is double ty ? Normalize(ry - ty) : null;

        bool ok = dist <= plan.AllowedDevXy && (dYaw is null || Math.Abs(dYaw.Value) <= plan.AllowedDevTheta);
        return new StationDeviationResult(target, current is not null, dist, along, wallDist, dYaw, ok);
    }

    /// <summary>도면 수평 방향 벡터 → 선박 방향 이름(도면 +X=선수, −X=선미, +Y=좌현, −Y=우현).</summary>
    public static string ShipSide(double dx, double dy) =>
        Math.Abs(dy) >= Math.Abs(dx) ? (dy < 0 ? "우현" : "좌현") : (dx > 0 ? "선수" : "선미");

    /// <summary>카드 표시용 상세 문구 — "벽 따라 우현 쪽 1.56 m · 벽까지 4.97 m (계획 1.80) · 방향 차 −0.8°".</summary>
    public static string Describe(StationDeviationResult r)
    {
        var parts = new List<string>();
        if (r.AlongWallM is double a && r.Station.WallU is { Length: >= 2 } u)
            parts.Add(Math.Abs(a) < 0.005
                ? "벽 따라 0.00 m"
                : $"벽 따라 {ShipSide(Math.Sign(a) * u[0], Math.Sign(a) * u[1])} 쪽 {Math.Abs(a):F2} m");
        if (r.WallDistanceM is double w)
            parts.Add($"벽까지 {w:F2} m (계획 {r.Station.StandoffM:F2})");
        if (r.HeadingErrorRad is double h)
            parts.Add($"방향 차 {h * 180.0 / Math.PI:+0.0;-0.0;0.0}°");
        return string.Join(" · ", parts);
    }

    private static double Dist2(PlannedStationDto s, double x, double y) =>
        (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);

    private static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        else if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }
}
