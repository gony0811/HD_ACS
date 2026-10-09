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
    // seamType enum = LINE·CROSS3_R0/R90/R180/R270·CROSS4·CORNER2·CORNER3 (§8.2/§8.5.1, 개정 2026-10-07 —
    //   CROSS3 회전 4종 확장, 면 자세 무관). 입력의 legacy 별칭(CROSS→CROSS4·CORNER→CORNER3·bare CROSS3→CROSS3_R0)은
    //   AreaTaskRules.Normalize 가 등록/수정 시점에 canonical 로 바꾸므로, 발행 payload 에는 canonical 값만 실린다.
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
            "seamType":            { "enum": ["LINE", "CROSS3_R0", "CROSS3_R90", "CROSS3_R180", "CROSS3_R270", "CROSS4", "CORNER2", "CORNER3"] },
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

    // batterySwapMove param_schema (JSON Schema draft-07) — HD_AMR 배터리관리 §6 / VDA5050_INTERFACE_SPEC §8.
    // 운영자 수동 트리거 — 현재 층(mapId) 배터리 교체 장소(ref.node, node_type=BATTERY_SWAP)로 단일 노드 이동.
    // reason: LOW(저전력 알람 수신 후) / CRITICAL(위험 알람) / MANUAL(사전 예방·점검).
    public const string BatterySwapMoveParamSchema = """
    {
      "type": "object",
      "required": ["targetNodeId", "mapId"],
      "properties": {
        "targetNodeId": { "type": "string" },
        "mapId":        { "type": "string" },
        "reason":       { "enum": ["LOW", "CRITICAL", "MANUAL"] }
      }
    }
    """;

    // moveToSeamStart param_schema (JSON Schema draft-07) — VDA5050_INTERFACE_SPEC §8 (코봇 seam 시작점 이동 시험).
    // position 서브스키마는 startWeldInspection과 동일(seamStartW/seamEndW/drawingPos). params 없음(촬영 안 함).
    public const string MoveToSeamStartParamSchema = """
    {
      "type": "object",
      "required": ["jobRef", "position"],
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
        }
      }
    }
    """;

    /// <summary>
    /// action_catalog 의 커스텀 액션 행을 canonical param_schema 로 맞춘다(없으면 insert).
    /// 멱등 — 저장된 값과 의미상 동일하면 write 하지 않는다(jsonb 공백 재포맷 무시 위해 정규화 비교).
    /// </summary>
    public static async Task EnsureAsync(AcsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await UpsertAsync(db, logger, "startWeldInspection", StartWeldInspectionParamSchema, "단일 용접라인 구간 자동 검사 [WP-3]", ct);
        await UpsertAsync(db, logger, "moveToSeamStart", MoveToSeamStartParamSchema, "코봇툴 seam 시작점 이동 시험(촬영 없음) [VDA §8]", ct);
        await UpsertAsync(db, logger, "batterySwapMove", BatterySwapMoveParamSchema, "현재 층 배터리 교체 장소로 이동(핫스왑 전제) [HD_AMR 배터리관리 §6]", ct);
    }

    private static async Task UpsertAsync(AcsDbContext db, ILogger logger, string actionType, string schema, string description, CancellationToken ct)
    {
        var row = await db.ActionCatalog.FirstOrDefaultAsync(x => x.ActionType == actionType, ct);
        if (row is null)
        {
            db.ActionCatalog.Add(new ActionCatalogEntity
            {
                ActionType = actionType, Scope = "NODE", BlockingType = "HARD", ParamSchema = schema, Description = description,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("action_catalog 기동 시드 삽입: {ActionType}", actionType);
            return;
        }
        if (!SameJson(row.ParamSchema, schema))
        {
            row.ParamSchema = schema;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("action_catalog 기동 시드 갱신: {ActionType} param_schema", actionType);
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
