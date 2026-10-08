using System.Text.Json;
using HD.Acs.Core.Planning;

namespace HD.Acs.App.Services;

/// <summary>
/// 검사 작업(용접선 1구간) 등록·수정 공통 검증 [SPEC v3 §4 / VDA §8.5.1] — POST·PUT이 같은 규칙을 쓴다.
/// </summary>
public static class AreaTaskRules
{
    /// <summary>
    /// seamType = 용접라인 형태 카탈로그(VDA §8.2/§8.5.1, [협의 N13]) — canonical 8종:
    /// LINE(직선) · CROSS3_R0/R90/R180/R270(T자 3갈래 회전 4종·면 자세 무관, 2026-10-07 개정)
    /// · CROSS4(4갈래 十자 교차) · CORNER2(2면 코너) · CORNER3(3면 코너).
    /// POLYLINE 등 그 외 값은 거부(꺾인 용접선은 세그먼트별 LINE으로 등록 — 같은 영역=정렬 공유).
    /// ⚠️ 유지보수: 이 집합은 <c>ActionCatalogSeed</c>·<c>db/schema.sql</c> 의 param_schema seamType enum 과
    /// 동일해야 한다(VDA §8.2). 사양서 개정 시 세 곳 + UI <c>AreaPlanningViewModel.SeamTypes</c> 를 함께 갱신할 것.
    /// </summary>
    public static readonly string[] SeamTypes =
        { "LINE", "CROSS3_R0", "CROSS3_R90", "CROSS3_R180", "CROSS3_R270", "CROSS4", "CORNER2", "CORNER3" };

    /// <summary>
    /// 입력 seamType 을 저장용 canonical 값으로 정규화한다(저장은 항상 canonical — 발행 시 param_schema enum 통과용).
    /// null → LINE(POST 기본). 대문자화 + legacy 별칭 매핑(AMR 파서와 동일, VDA §8.5.1 전환 유예):
    /// CROSS→CROSS4 · CORNER→CORNER3 · bare CROSS3→CROSS3_R0. 그 밖의 값은 그대로(검증에서 걸러짐).
    /// </summary>
    public static string Normalize(string? seamType)
    {
        if (seamType is null) return "LINE";
        var s = seamType.Trim().ToUpperInvariant();
        return s switch
        {
            "CROSS"  => "CROSS4",
            "CORNER" => "CORNER3",
            "CROSS3" => "CROSS3_R0",
            _ => s,
        };
    }

    /// <summary>위반 사유(한국어) 또는 null(통과). seamType null = 검사 생략(POST는 기본 LINE, PUT은 기존값 유지).</summary>
    public static string? Validate(string? seamType, string areaCornersJson,
        double startU, double startV, double endU, double endV)
    {
        // legacy 별칭(CROSS·CORNER·bare CROSS3)·대소문자는 정규화 후 판정 — 저장도 Normalize 된 값으로 한다.
        if (seamType is not null && !SeamTypes.Contains(Normalize(seamType)))
            return $"seamType '{seamType}'은 지원하지 않습니다 — 허용: {string.Join("·", SeamTypes)}(VDA §8.2/§8.5.1). 꺾인 용접선은 세그먼트별 LINE으로 나눠 등록하세요.";
        if (!double.IsFinite(startU) || !double.IsFinite(startV) || !double.IsFinite(endU) || !double.IsFinite(endV))
            return "용접선 좌표는 유한한 숫자여야 합니다.";

        var poly = JsonSerializer.Deserialize<double[][]>(areaCornersJson) ?? Array.Empty<double[]>();
        if (!AreaGeometry.PointInPolygon(startU, startV, poly) || !AreaGeometry.PointInPolygon(endU, endV, poly))
            return "용접선 시작/끝점이 영역(사각형) 내부가 아닙니다.";
        return null;
    }

    /// <summary>
    /// CROSS3/CROSS4 교차 가지 끝점(면-로컬 u,v) 검증 [VDA §8.5.1]. 위반 사유 또는 null.
    /// null/빈 배열 = 미지정(허용 — AMR 교시 폴백). 지정 시: 타입 일치(CROSS3=3·CROSS4=4), 유한, 영역 내부.
    /// </summary>
    public static string? ValidatePoints(string? seamType, string areaCornersJson, double[][]? points)
    {
        if (points is null || points.Length == 0) return null;
        var st = Normalize(seamType);
        bool isCross3 = st.StartsWith("CROSS3", StringComparison.Ordinal);
        bool isCross4 = st == "CROSS4";
        if (!isCross3 && !isCross4)
            return $"points(교차 가지 끝점)는 CROSS3/CROSS4 에만 쓸 수 있습니다 (현재 '{seamType}').";
        int need = isCross4 ? 4 : 3;
        if (points.Length != need)
            return $"{st} 은 가지 끝점 {need}개가 필요합니다 (현재 {points.Length}개). CROSS3=[줄기·통과·통과], CROSS4=가지 4개.";
        var poly = JsonSerializer.Deserialize<double[][]>(areaCornersJson) ?? Array.Empty<double[]>();
        foreach (var p in points)
        {
            if (p is not { Length: >= 2 } || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))
                return "가지 끝점 좌표는 유한한 [u,v] 여야 합니다.";
            if (!AreaGeometry.PointInPolygon(p[0], p[1], poly))
                return "가지 끝점이 영역(사각형) 내부가 아닙니다.";
        }
        return null;
    }
}
