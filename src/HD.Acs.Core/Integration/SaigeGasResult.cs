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

    /// <summary>
    /// 세이지 규격 검사 [포밍가스 연동 규격 초안 v0.1 §2 필드 표·값 규칙] — 보내면 400 으로 거부될 본문을 미리 가려낸다
    /// (거부된 결과는 재전송되지 않으므로, 무엇이 틀렸는지 ACS 쪽에 남기려는 것). 문제 없으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> Violations(JsonObject body)
    {
        var v = new List<string>();
        static bool Str(JsonNode? n) => n is JsonValue x && x.TryGetValue<string>(out var s) && s.Length > 0;
        static bool Num(JsonNode? n) => n is JsonValue x && x.GetValueKind() == JsonValueKind.Number;
        static bool Int(JsonNode? n, out long value)
        {
            value = 0;
            return n is JsonValue x && x.GetValueKind() == JsonValueKind.Number && x.TryGetValue(out value);
        }
        static bool Verdict(JsonNode? n) => n is JsonValue x && x.TryGetValue<string>(out var s) && s is "OK" or "NG";

        if (!Str(body["tankId"])) v.Add("tankId 없음");
        if (!Str(body["taskId"])) v.Add("taskId 없음");
        if (!Int(body["round"], out var round) || round is not (1 or 2)) v.Add("round 는 1 또는 2 (필수)");
        if (!Int(body["startedAt"], out _)) v.Add("startedAt 없음");
        if (!Int(body["finishedAt"], out _)) v.Add("finishedAt 없음");
        if (body["threshold"] is not JsonObject th || !Num(th["value"]) || !Str(th["unit"])) v.Add("threshold.value·unit 없음");
        if (!Verdict(body["result"])) v.Add($"result 는 OK·NG 만 (받은 값 '{ResultOf(body)}')");
        if (body["points"] is not JsonArray points || points.Count == 0) { v.Add("points 없음"); return v; }
        foreach (var p in points)
        {
            if (p is not JsonObject point) { v.Add("points 항목이 객체가 아님"); continue; }
            string at = Int(point["pointSeq"], out var seq) && seq >= 1 ? $"point {seq}" : "point ?";
            if (at == "point ?") v.Add("pointSeq 는 1 이상 정수");
            if (!Verdict(point["result"])) v.Add($"{at}: result 는 OK·NG 만");
            if (point["scans"] is not JsonArray scans || scans.Count is < 1 or > 3) { v.Add($"{at}: scans 는 1~3건"); continue; }
            foreach (var sc in scans)
            {
                if (sc is not JsonObject scan) { v.Add($"{at}: scans 항목이 객체가 아님"); continue; }
                if (!Int(scan["retry"], out var retry) || retry is < 0 or > 2) v.Add($"{at}: retry 는 0~2");
                if (!Int(scan["u"], out _) || !Int(scan["v"], out _)) v.Add($"{at}: u·v 는 정수(mm)");
                if (!Num(scan["value"])) v.Add($"{at}: value 없음");
                if (!Verdict(scan["result"])) v.Add($"{at}: scan result 는 OK·NG 만");
                if (!Int(scan["measuredAt"], out _)) v.Add($"{at}: measuredAt 없음");
            }
            if (v.Count >= 10) break;
        }
        return v;
    }
}
