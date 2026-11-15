using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD.Acs.Core.Integration;

/// <summary>
/// 가스 측정 결과 본문 — 가스검사 AMR 이 검사 액션이 끝날 때 `resultDescription` 에 싣는 JSON 이고,
/// ACS 가 세이지로 올리는 본문이기도 하다(`POST /admin/v3/external/gas/results`).
/// 모양: `{tankId, taskId, round?, robotId, startedAt, finishedAt, threshold{value,unit}, result, points[{pointSeq,result,scans[…]}]}`
/// [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.2 — 세이지 포밍가스 연동 규격 초안 v0.1 §2 를 옮긴 것].
/// </summary>
public static class SaigeGasResult
{
    /// <summary>
    /// 액션 결과 문자열이 가스 결과 본문인지 — JSON 객체이고 `result`(문자열)와 `points`(배열)가 있어야 한다.
    /// 용접검사 결과("OK;anchor=…")나 실패 사유 문자열은 false.
    /// </summary>
    public static bool TryParse(string? resultDescription, out JsonObject body)
    {
        body = null!;
        if (string.IsNullOrWhiteSpace(resultDescription) || resultDescription.TrimStart()[0] != '{') return false;
        try
        {
            if (JsonNode.Parse(resultDescription) is not JsonObject o) return false;
            if (o["result"] is not JsonValue r || r.GetValueKind() != JsonValueKind.String) return false;
            if (o["points"] is not JsonArray) return false;
            body = o;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>판정 문자열(OK | NG | ERROR …). 없으면 빈 문자열.</summary>
    public static string ResultOf(JsonObject body) =>
        body["result"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static int? RoundOf(JsonObject body) =>
        body["round"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    public static string? TankOf(JsonObject body) =>
        body["tankId"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static int PointCountOf(JsonObject body) => (body["points"] as JsonArray)?.Count ?? 0;

    /// <summary>
    /// 세이지로 보낼 본문 — AMR 이 준 본문을 그대로 두고 식별 값만 ACS 기준으로 맞춘다.
    /// · taskId = ACS 가 그 액션에 발급한 값(AMR 이 되돌려 준 문자열이 아니라). 세이지 조회 API(`/api/tasks`)와 같은 표기
    ///   (<paramref name="taskIdLittleEndian"/> — 용접검사 촬영 경로 때문에 세이지 쪽 taskId 가 앞 세 묶음이 뒤집힌 표기다)로 보내야
    ///   세이지가 용접선과 결과를 잇는다.
    /// · tankId·round 는 본문에 없을 때만 채운다(AMR 값 우선).
    /// 원본은 바꾸지 않는다(복사본 반환).
    /// </summary>
    public static JsonObject ForSaige(JsonObject body, Guid? taskId, bool taskIdLittleEndian, string? tankId, int? round)
    {
        var copy = body.DeepClone().AsObject();
        if (taskId is Guid id && id != Guid.Empty)
            copy["taskId"] = SaigeTaskId.ToWire(id, taskIdLittleEndian).ToString();
        if (TankOf(copy) is null && !string.IsNullOrEmpty(tankId)) copy["tankId"] = tankId;
        if (RoundOf(copy) is null && round is int r) copy["round"] = r;
        return copy;
    }
}
