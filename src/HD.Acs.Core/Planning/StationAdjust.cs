using System.Text.Json.Nodes;

namespace HD.Acs.Core.Planning;

/// <summary>
/// 정차 온보드 보정 허용 범위 [VDA §4.4.2 / N23 초안]. AMR이 리치(용접선 도달)를 위해 nodePosition에서
/// 옮겨 서도 되는 한계를 정차 기준 프레임으로 준다 — 전진 = 노드 theta(벽 정면) 방향, 횡 = 그 직각(좌우 대칭).
/// <para>
/// 전진(벽 쪽)과 후진(벽에서 멀어짐)을 나눈다 — 벽 쪽 보정은 차체-벽 여유를 직접 깎으므로 기본 0이고,
/// 현장 실패(2026-09-28)는 법선 거리 부족이라 필요한 쪽은 후진이다.
/// </para>
/// <para>
/// <c>allowedDeviationXY/Theta</c>(도착 판정 허용 오차, §4.2)와는 별개다. 그 필드를 키우면 주행 도착 판정까지
/// 느슨해지므로 의미를 겹치지 않는다. VDA 노드에 커스텀 필드 자리가 없어 각 검사 액션 <c>params.stationAdjust</c>에 같은 값을 싣는다.
/// </para>
/// </summary>
public sealed record StationAdjustLimits(double MaxForwardMm, double MaxBackwardMm, double MaxLateralMm, double MaxThetaDeg)
{
    /// <summary>
    /// 이 정차에 실을 <c>params.stationAdjust</c> 객체. 꺼짐(<paramref name="limits"/> null)이거나
    /// 정차각이 없으면(바닥·천장 등 벽 정면 프레임 없음) null — 필드를 싣지 않아 AMR은 종전대로 정차점에 선다.
    /// </summary>
    public static JsonObject? ToParam(StationAdjustLimits? limits, double? stationTheta)
    {
        if (limits is null || stationTheta is null) return null;
        return new JsonObject
        {
            ["maxForwardMm"] = limits.MaxForwardMm,
            ["maxBackwardMm"] = limits.MaxBackwardMm,
            ["maxLateralMm"] = limits.MaxLateralMm,
            ["maxThetaDeg"] = limits.MaxThetaDeg,
        };
    }

    /// <summary>음수·NaN 거부. 전부 0이면 보정 금지와 같으므로 꺼짐(null)으로 정규화.</summary>
    public static StationAdjustLimits? Normalize(bool enabled,
        double maxForwardMm, double maxBackwardMm, double maxLateralMm, double maxThetaDeg)
    {
        if (!enabled) return null;
        foreach (var (name, v) in new[]
                 {
                     ("MaxForwardMm", maxForwardMm), ("MaxBackwardMm", maxBackwardMm),
                     ("MaxLateralMm", maxLateralMm), ("MaxThetaDeg", maxThetaDeg),
                 })
            if (double.IsNaN(v) || v < 0)
                throw new ArgumentOutOfRangeException(name, v, $"정차 보정 한계 {name}는 0 이상이어야 합니다.");
        if (maxForwardMm == 0 && maxBackwardMm == 0 && maxLateralMm == 0 && maxThetaDeg == 0) return null;
        return new StationAdjustLimits(maxForwardMm, maxBackwardMm, maxLateralMm, maxThetaDeg);
    }
}
