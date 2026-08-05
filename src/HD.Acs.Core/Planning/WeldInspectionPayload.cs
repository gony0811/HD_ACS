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
/// startWeldInspection payload 빌드·검증 [PHASE2 WP-3, SPEC §4.1/§4.2].
/// 도면 좌표 + 유효 T_W_D → 맵(월드) 좌표 position 조립. 순수 함수(테스트 가능).
/// </summary>
public static class WeldInspectionPayload
{
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

    /// <summary>도면 좌표 → 월드 position(JsonObject, §4.1 키명). x,y는 T_W_D 적용·z 통과. [SPEC v2: 법선 제거]</summary>
    public static JsonObject BuildPosition(DrawingTransform t, WeldDrawingData d)
    {
        var (sx, sy) = t.DrawingToMap(d.SeamStart[0], d.SeamStart[1]);
        var (ex, ey) = t.DrawingToMap(d.SeamEnd[0], d.SeamEnd[1]);

        return new JsonObject
        {
            ["seamStartW"] = Vec(sx, sy, d.SeamStart[2]),
            ["seamEndW"] = Vec(ex, ey, d.SeamEnd[2]),
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = d.Tank,
                ["level"] = d.Level,
                ["wall_code"] = d.WallCode,
                ["x"] = d.SeamStart[0],
                ["y"] = d.SeamStart[1],
                ["z"] = d.SeamStart[2],
            }
        };
    }

    /// <summary>actionParameters 객체 {jobRef, position, params} 조립(스키마 검증·발행 공용).</summary>
    public static JsonObject BuildActionParameters(string jobRef, JsonObject position, JsonNode? paramsNode) =>
        new()
        {
            ["jobRef"] = jobRef,
            ["position"] = position.DeepClone(),
            ["params"] = paramsNode?.DeepClone() ?? new JsonObject(),
        };

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
