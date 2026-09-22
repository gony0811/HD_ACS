using System.Text.Json;
using HD.Acs.Core.Planning;

namespace HD.Acs.App.Services;

/// <summary>
/// 검사 작업(용접선 1구간) 등록·수정 공통 검증 [SPEC v3 §4 / VDA §8.5.1] — POST·PUT이 같은 규칙을 쓴다.
/// </summary>
public static class AreaTaskRules
{
    /// <summary>
    /// seamType = 용접라인 형태 카탈로그(VDA §8.5.1, [협의 N13]) — 카탈로그와 1:1 5종:
    /// LINE(직선) · CROSS3(3갈래 교차) · CROSS4(4갈래 十자 교차) · CORNER2(2면 코너) · CORNER3(3면 코너).
    /// POLYLINE 등 그 외 값은 거부(꺾인 용접선은 세그먼트별 LINE으로 등록 — 같은 영역=정렬 공유).
    /// </summary>
    public static readonly string[] SeamTypes = { "LINE", "CROSS3", "CROSS4", "CORNER2", "CORNER3" };

    /// <summary>위반 사유(한국어) 또는 null(통과). seamType null = 검사 생략(POST는 기본 LINE, PUT은 기존값 유지).</summary>
    public static string? Validate(string? seamType, string areaCornersJson,
        double startU, double startV, double endU, double endV)
    {
        if (seamType is not null && !SeamTypes.Contains(seamType, StringComparer.OrdinalIgnoreCase))
            return $"seamType '{seamType}'은 지원하지 않습니다 — 허용: {string.Join("·", SeamTypes)}(VDA §8.5.1). 꺾인 용접선은 세그먼트별 LINE으로 나눠 등록하세요.";
        if (!double.IsFinite(startU) || !double.IsFinite(startV) || !double.IsFinite(endU) || !double.IsFinite(endV))
            return "용접선 좌표는 유한한 숫자여야 합니다.";

        var poly = JsonSerializer.Deserialize<double[][]>(areaCornersJson) ?? Array.Empty<double[]>();
        if (!AreaGeometry.PointInPolygon(startU, startV, poly) || !AreaGeometry.PointInPolygon(endU, endV, poly))
            return "용접선 시작/끝점이 영역(사각형) 내부가 아닙니다.";
        return null;
    }
}
