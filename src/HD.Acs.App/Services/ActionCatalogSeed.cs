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
/// <c>db/migrations/2026-09-22_params_task_id.sql</c>(최신) 와 **동일 내용**이어야 한다.
/// VDA5050_INTERFACE_SPEC §8.2 개정 시 세 곳을 함께 갱신할 것.
/// </summary>
public static class ActionCatalogSeed
{
    // startWeldInspection param_schema (JSON Schema draft-07) — VDA5050_INTERFACE_SPEC §8.2.
    // seamType enum = LINE·CROSS3·CROSS4·CORNER2·CORNER3 (§8.5.1, 2026-09-15 — 카탈로그 1:1 5종).
    // params.taskId = 계획 TASK 불변 키(uuid 문자열, 2026-09-22 ACS 선반영 — required 아님, N14).
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
            "taskId":              { "type": "string" },
            "seamType":            { "enum": ["LINE", "CROSS3", "CROSS4", "CORNER2", "CORNER3"] },
            "points":              { "type": "array" },
            "sectionDxfId":        { "type": "string" },
            "inspectionProfileId": { "type": "string" },
            "standoffMm":          { "type": "number" },
            "workingDistanceMm":   { "type": "number" },
            "anchorGroupId":       { "type": "string" },
            "seqInGroup":          { "type": "integer", "minimum": 1 }
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
        var canonical = Normalize(StartWeldInspectionParamSchema);

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

        if (Normalize(row.ParamSchema) != canonical)
        {
            row.ParamSchema = StartWeldInspectionParamSchema;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("action_catalog 기동 시드 갱신: {ActionType} param_schema (seamType 5값 + params.taskId)", actionType);
        }
    }

    // jsonb 저장 시 공백이 재포맷되므로, 문자열이 아닌 **의미상** 동일성으로 비교한다.
    private static string? Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json)?.ToJsonString(); }
        catch (JsonException) { return json; }   // 파싱 불가 → 원문 비교(항상 갱신 유도)
    }
}
