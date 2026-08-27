using System.Text.Json.Nodes;
using HD.Acs.Core.Geometry;
using Json.Schema;

namespace HD.Acs.Core.Planning;

/// <summary>릴리즈 시점 도면 데이터(Task.Position에서 파싱). 각 벡터는 [x,y,z] (m). [SPEC v2: 법선 제거]</summary>
public sealed record WeldDrawingData(
    string Tank,
    int Level,
    string WallCode,
    double[] SeamStart,
    double[] SeamEnd);

/// <summary>유효 T_W_D 부재/맵버전 불일치 — 릴리즈 거부 [PHASE2 §2.5].</summary>
public sealed class CalibrationInvalidException(string message) : Exception(message);

/// <summary>발행 직전 param_schema 검증 실패 — 릴리즈 중단 [PHASE2 §4.2].</summary>
public sealed class WeldPayloadSchemaException(IReadOnlyList<string> violations)
    : Exception("startWeldInspection payload 스키마 위반: " + string.Join("; ", violations))
{
    public IReadOnlyList<string> Violations { get; } = violations;
}

/// <summary>
/// startWeldInspection actionParameters 빌드·검증 [VDA5050_INTERFACE §6].
/// 계약(간결): wallId(면 코드) · seamStart/seamEnd(맵 좌표 [x,y,z] m) · orientation(H|V) · patternType(디폴트 LINEAR).
/// 툴 자세·법선은 AMR이 면 티칭으로 결정하므로 ACS는 위치·방향·도면타입만 전달한다. 순수 함수(테스트 가능).
/// </summary>
public static class WeldInspectionPayload
{
    /// <summary>검사 도면 타입 기본값(선형).</summary>
    public const string PatternLinear = "LINEAR";

    /// <summary>맵버전 일치 검증 후 T_W_D 반환. calMapVersion==null 또는 !=mapVersion 이면 거부 [§2.5].</summary>
    public static DrawingTransform ResolveTransform(int mapVersion, int? calMapVersion,
        double tx, double ty, double yawRad)
    {
        if (calMapVersion is null)
            throw new CalibrationInvalidException($"유효 T_W_D 없음 (맵버전 {mapVersion}에 등록된 캘리브레이션 없음).");
        if (calMapVersion != mapVersion)
            throw new CalibrationInvalidException(
                $"T_W_D 맵버전 불일치: 캘리브레이션 v{calMapVersion} ≠ 맵 v{mapVersion} — 맵 재생성 후 재캘리브레이션 필요.");
        return new DrawingTransform(tx, ty, yawRad);
    }

    /// <summary>
    /// startWeldInspection actionParameters(flat) 조립 [§6 계약].
    /// seamStart/End = 도면 좌표에 T_W_D 적용(x,y)·z 통과한 맵 좌표. orientation은 seam 기하에서 유도(override 가능).
    /// </summary>
    public static JsonObject BuildActionParameters(DrawingTransform t, WeldDrawingData d,
        string patternType = PatternLinear, string? orientation = null)
    {
        var (sx, sy) = t.DrawingToMap(d.SeamStart[0], d.SeamStart[1]);
        var (ex, ey) = t.DrawingToMap(d.SeamEnd[0], d.SeamEnd[1]);
        var start = new[] { sx, sy, d.SeamStart[2] };
        var end = new[] { ex, ey, d.SeamEnd[2] };

        return new JsonObject
        {
            ["wallId"] = d.WallCode,
            ["seamStart"] = Vec(start[0], start[1], start[2]),
            ["seamEnd"] = Vec(end[0], end[1], end[2]),
            ["orientation"] = orientation ?? Orientation(start, end),
            ["patternType"] = patternType,
        };
    }

    /// <summary>seam 기하에서 수평/수직 유도 — 높이 변화(Δz)가 수평 변위보다 크면 "V", 아니면 "H"(프레임 무관).</summary>
    public static string Orientation(double[] start, double[] end)
    {
        double dx = end[0] - start[0], dy = end[1] - start[1], dz = end[2] - start[2];
        double dxy = Math.Sqrt(dx * dx + dy * dy);
        return Math.Abs(dz) > dxy ? "V" : "H";
    }

    /// <summary>param_schema(JSON Schema draft-07)로 검증. 위반 목록 반환(빈 목록=통과). 스키마 없으면 검증 생략.</summary>
    public static IReadOnlyList<string> ValidateSchema(string? schemaJson, JsonNode actionParameters)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return Array.Empty<string>();

        var schema = JsonSchema.FromText(schemaJson);
        var results = schema.Evaluate(actionParameters, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return Array.Empty<string>();

        var errors = new List<string>();
        Collect(results, errors);
        if (errors.Count == 0) errors.Add("스키마 검증 실패(상세 없음).");
        return errors;
    }

    private static void Collect(EvaluationResults r, List<string> errors)
    {
        if (r.HasErrors && r.Errors is not null)
            foreach (var e in r.Errors)
                errors.Add($"{r.InstanceLocation}: {e.Value}");
        foreach (var d in r.Details)
            Collect(d, errors);
    }

    private static JsonArray Vec(double x, double y, double z) =>
        new(JsonValue.Create(x), JsonValue.Create(y), JsonValue.Create(z));
}
