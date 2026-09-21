using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 기동 시드: ref.action_catalog 의 커스텀 액션 param_schema 를 앱 부팅 시 canonical 정의로 멱등 upsert.
///
/// 배경: 앱은 자동 마이그레이션을 하지 않으므로, 현장 이동식 서버(폐쇄망)처럼 DB에 직접 접근할 수 없는
/// 배포에서는 이 기동 시드가 param_schema 갱신의 **유일한 적용 경로**다(ref.map 기동 시드와 동일 취지).
/// param_schema 는 운영자 튜닝 대상이 아니라 ACS↔AMR **계약**이므로, 코드가 정본이며 부팅 때 맞춘다.
///
/// ⚠️ 유지보수: 아래 canonical JSON 은 <c>db/schema.sql</c> 의 startWeldInspection param_schema 및
/// <c>db/migrations/2026-09-21_param_schema_taskid_attempt.sql</c>(최신) 와 **동일 내용**이어야 한다.
/// VDA5050_INTERFACE_SPEC §8.2 개정 시 세 곳을 함께 갱신할 것.
/// </summary>
public static class ActionCatalogSeed
{
    // startWeldInspection param_schema (JSON Schema draft-07) — VDA5050_INTERFACE_SPEC §8.2.
    // seamType enum = LINE·CROSS3·CROSS4·CORNER2·CORNER3 (§8.5.1, 2026-09-15 — 카탈로그 1:1 5종).
    // params.taskId·attempt (개정 1.4, 2026-09-21) — 선택 필드(구버전 AMR 호환). attempt는 발행 시점 발급이라
    // 큐 전개 시 검증 payload에는 없다 → required에 넣지 않는다.
    public const string StartWeldInspectionParamSchema = """
    {
      "type": "object",
      "required": ["jobRef", "position", "params"],
      "properties": {
        "jobRef": { "type": "string" },
        "position": {
          "type": "object",
          "required": ["seamStartW", "seamEndW", "drawingPos"],
          "properties": {
            "seamStartW":  { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
            "seamEndW":    { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
            "drawingPos": {
              "type": "object",
              "required": ["tank", "level", "wall_code", "u", "v", "x", "y", "z"],
              "properties": {
                "tank": { "type": "string" }, "level": { "type": "integer" },
                "wall_code": { "type": "string" },
                "u": { "type": "number" }, "v": { "type": "number" },
                "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" }
              }
            }
          }
        },
        "params": {
          "type": "object",
          "required": ["seamType", "sectionDxfId", "inspectionProfileId", "standoffMm", "anchorGroupId", "seqInGroup"],
          "properties": {
            "seamType":            { "enum": ["LINE", "CROSS3", "CROSS4", "CORNER2", "CORNER3"] },
            "points":              { "type": "array" },
            "sectionDxfId":        { "type": "string" },
            "inspectionProfileId": { "type": "string" },
            "standoffMm":          { "type": "number" },
            "workingDistanceMm":   { "type": "number" },
            "anchorGroupId":       { "type": "string" },
            "seqInGroup":          { "type": "integer", "minimum": 1 },
            "taskId":              { "type": "string", "format": "uuid" },
            "attempt":             { "type": "integer", "minimum": 1, "maximum": 255 }
          }
        }
      }
    }
    """;

    /// <summary>
    /// action_catalog 의 startWeldInspection 행을 canonical param_schema 로 맞춘다(없으면 insert).
    /// 멱등 — 저장된 값과 의미상 동일하면 write 하지 않는다(jsonb 공백 재포맷 무시 위해 정규화 비교).
    /// </summary>
    public static async Task EnsureAsync(AcsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        const string actionType = "startWeldInspection";

        var row = await db.ActionCatalog.FirstOrDefaultAsync(x => x.ActionType == actionType, ct);
        if (row is null)
        {
            db.ActionCatalog.Add(new ActionCatalogEntity
            {
                ActionType = actionType,
                Scope = "NODE",
                BlockingType = "HARD",
                ParamSchema = StartWeldInspectionParamSchema,
                Description = "단일 용접라인 구간 자동 검사 [WP-3]",
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("action_catalog 기동 시드 삽입: {ActionType}", actionType);
            return;
        }

        if (!SameJson(row.ParamSchema, StartWeldInspectionParamSchema))
        {
            row.ParamSchema = StartWeldInspectionParamSchema;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("action_catalog 기동 시드 갱신: {ActionType} param_schema (VDA 사양서 개정 1.6 — taskId·attempt)", actionType);
        }
    }

    // jsonb 는 저장 시 공백뿐 아니라 **객체 키 순서까지 재정렬**한다(키 길이→사전순). 직렬화 문자열을 비교하면
    // 내용이 같아도 항상 "다름"이 되어 부팅마다 갱신이 일어난다(실DB E2E에서 발견 — 2026-09-21).
    // 그래서 구조적으로(키 순서 무관) 비교한다. 파싱 불가면 다름으로 보고 canonical 로 덮는다.
    internal static bool SameJson(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return string.IsNullOrWhiteSpace(a) == string.IsNullOrWhiteSpace(b);
        try { return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b)); }
        catch (JsonException) { return false; }
    }
}
