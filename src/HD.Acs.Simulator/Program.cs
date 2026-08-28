using System.Text.Json;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// VDA 5050 로봇(HD_AMR) 시뮬레이터 — NAMUGA ACS.AMR.Simulator 패턴 승계.
// Order 수신 → 노드 순회 + 액션 실행을 시뮬레이션하며 state를 발행한다.
//
// [WP-4 확장 — SPEC_PHASE2_ACS.md §5]
//  1. startWeldInspection actionParameters를 param_schema(§4.1) 필수 필드 기준으로 검증.
//     위반 시 콘솔 출력 + actionStatus=FAILED, resultDescription="FAIL;reason=PARAM(<필드,...>)".
//  2. 앵커 공유 시뮬레이션: 직전 액션과 anchorGroupId 동일 + 사이에 주행 없음
//     → 정렬 스킵(⑤~⑦, 단축 실행). 아니면 정렬 포함(①~⑧).
//     ── 관측 계약: FINISHED 시 resultDescription="OK;anchor=FULL|SHARED;jobRef=<...>"
//        (테스트 드라이버 HD.Acs.SimTest가 이 문자열로 앵커 동작을 검증한다.)
//     앵커 무효화: 주행 발생 / 직전 액션 FAILED / 새 Order 수신(재시도 포함).
//  3. 실패 주입: 환경변수 SIM_FAIL_ACTION_IDS(콤마 구분 actionId)에 해당하면
//     FAILED, resultDescription="FAIL;reason=INJECTED".
//
// 사용: dotnet run [brokerHost] [manufacturer] [serialNumber] [mapId]

var broker = args.ElementAtOrDefault(0) ?? "localhost";
var robot = new RobotRef("AMR-01", args.ElementAtOrDefault(1) ?? "HHI", args.ElementAtOrDefault(2) ?? "AMR-01");
var mapId = args.ElementAtOrDefault(3) ?? "CT1-L1";
var json = new JsonSerializerOptions { WriteIndented = false };

// 실패 주입 대상 actionId 집합 [WP-4 §5.3]
var failActionIds = (Environment.GetEnvironmentVariable("SIM_FAIL_ACTION_IDS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// jobRef 기반 실패 주입 — actionId가 배차 시점 발급(GUID)이라 사전 지정 불가한 ACS E2E용
var failJobRefs = (Environment.GetEnvironmentVariable("SIM_FAIL_JOB_REFS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// 주행 실패 주입 — 처음 N개 Order를 "노드 미도달 + 전 액션 FAILED + drivingFailed" 처리 (AMR 회신 §3.1 재현)
var driveFailMax = int.TryParse(Environment.GetEnvironmentVariable("SIM_DRIVE_FAIL_MAX"), out var df) ? df : 0;
var driveFailCount = 0;

// 실행 시간 (ms) — 테스트 고속화를 위해 환경변수로 조절 가능
var travelMs = int.TryParse(Environment.GetEnvironmentVariable("SIM_TRAVEL_MS"), out var t1) ? t1 : 800;
var fullMs   = int.TryParse(Environment.GetEnvironmentVariable("SIM_FULL_MS"),   out var t2) ? t2 : 1200;
var sharedMs = int.TryParse(Environment.GetEnvironmentVariable("SIM_SHARED_MS"), out var t3) ? t3 : 400;

var client = new MqttFactory().CreateMqttClient();
var connTopic = Vda5050Topics.Connection(robot);
var stateTopic = Vda5050Topics.State(robot);

var options = new MqttClientOptionsBuilder()
    .WithTcpServer(broker)
    .WithClientId($"sim-{robot.SerialNumber}")
    // Last Will: 비정상 종료 시 CONNECTIONBROKEN (두절 감지의 근거 [ADR-002])
    .WithWillTopic(connTopic)
    .WithWillPayload(JsonSerializer.Serialize(new Vda5050Connection
        { ConnectionState = "CONNECTIONBROKEN", Manufacturer = robot.Manufacturer, SerialNumber = robot.SerialNumber }))
    .WithWillRetain(true)
    .Build();

var state = new Vda5050State
{
    Manufacturer = robot.Manufacturer, SerialNumber = robot.SerialNumber,
    AgvPosition = new AgvPosition { MapId = mapId, PositionInitialized = true },
    BatteryState = new BatteryState { BatteryCharge = 95 },
    SafetyState = new SafetyState(),   // 표준 필수 필드 발행 [SPEC §6.3 N4]
};
var headerId = 0;
Vda5050Order? currentOrder = null;
var gate = new SemaphoreSlim(1, 1);

// ── 앵커 캐시 상태 [WP-4 §5.2] ──
string? currentAnchorGroup = null;   // 유효한 앵커의 그룹 ID (null = 앵커 없음)
var movedSinceLastAction = true;     // 직전 액션 이후 주행 발생 여부

client.ApplicationMessageReceivedAsync += async e =>
{
    var payload = e.ApplicationMessage.ConvertPayloadToString();
    if (e.ApplicationMessage.Topic.EndsWith("/order"))
    {
        currentOrder = JsonSerializer.Deserialize<Vda5050Order>(payload);
        Console.WriteLine($"[SIM] Order 수신: {currentOrder?.OrderId} (nodes={currentOrder?.Nodes.Count})");
        _ = Task.Run(() => ExecuteOrderAsync(currentOrder!));
    }
    else if (e.ApplicationMessage.Topic.EndsWith("/instantActions"))
    {
        var ia = JsonSerializer.Deserialize<Vda5050InstantActions>(payload);
        foreach (var a in ia!.Actions)
        {
            Console.WriteLine($"[SIM] instantAction: {a.ActionType}");
            if (a.ActionType == "initPosition")
            {
                // 재측위: mapId + x/y/theta 반영 (수동 층 변경 UI가 좌표까지 보냄 [Q9])
                state.AgvPosition!.MapId = ParamStr(a.ActionParameters, "mapId") ?? state.AgvPosition.MapId;
                state.AgvPosition.X = ParamNum(a.ActionParameters, "x") ?? state.AgvPosition.X;
                state.AgvPosition.Y = ParamNum(a.ActionParameters, "y") ?? state.AgvPosition.Y;
                state.AgvPosition.Theta = ParamNum(a.ActionParameters, "theta") ?? state.AgvPosition.Theta;
                state.AgvPosition.PositionInitialized = true;
                Console.WriteLine($"[SIM] 재측위 완료 → mapId={state.AgvPosition.MapId} " +
                                  $"pos=({state.AgvPosition.X:F3},{state.AgvPosition.Y:F3})");
                await PublishStateAsync();
            }
            if (a.ActionType == "emergencyStop")
                Console.WriteLine("[SIM] !! 비상정지 (기능적 정지) !!");
        }
    }
};

await client.ConnectAsync(options);
await client.SubscribeAsync(Vda5050Topics.Order(robot), MqttQualityOfServiceLevel.AtLeastOnce);
await client.SubscribeAsync(Vda5050Topics.InstantActions(robot), MqttQualityOfServiceLevel.AtLeastOnce);

await PublishAsync(connTopic, new Vda5050Connection
    { ConnectionState = "ONLINE", Manufacturer = robot.Manufacturer, SerialNumber = robot.SerialNumber }, retain: true);
Console.WriteLine($"[SIM] {robot.SerialNumber} ONLINE (map={mapId}, broker={broker}, " +
                  $"failInject={failActionIds.Count}건)");

// 주기 state 보고 (2초)
while (true)
{
    await PublishStateAsync();
    await Task.Delay(2000);
}

async Task ExecuteOrderAsync(Vda5050Order order)
{
    await gate.WaitAsync();
    try
    {
        // 새 Order 수신(재시도 포함) → 앵커 무효화 [WP-4 §5.2]
        currentAnchorGroup = null;
        movedSinceLastAction = true;

        state.OrderId = order.OrderId;
        state.OrderUpdateId = order.OrderUpdateId;
        state.Errors.Clear();
        state.ActionStates = order.Nodes.SelectMany(n => n.Actions)
            .Select(a => new ActionState { ActionId = a.ActionId, ActionType = a.ActionType, ActionStatus = "WAITING" })
            .ToList();

        // ── 주행 실패 주입: 이동/도달 없이 전 액션 FAILED + drivingFailed 보고 [AMR 회신 §3.1] ──
        if (driveFailCount < driveFailMax)
        {
            driveFailCount++;
            await Task.Delay(travelMs);
            foreach (var a in state.ActionStates)
            { a.ActionStatus = "FAILED"; a.ResultDescription = "FAIL;reason=DRIVE"; }
            state.Errors.Add(new VdaError
            { ErrorType = "drivingFailed", ErrorLevel = "WARNING", ErrorDescription = $"주행 실패(주입 {driveFailCount}/{driveFailMax}) order={order.OrderId}" });
            Console.WriteLine($"[SIM] 주행 실패(주입): order={order.OrderId} — 미도달, 전 액션 FAILED");
            await PublishStateAsync();
            return;
        }

        foreach (var node in order.Nodes.OrderBy(n => n.SequenceId))
        {
            await Task.Delay(travelMs);   // 이동 시뮬레이션
            state.LastNodeId = node.NodeId;
            state.LastNodeSequenceId = node.SequenceId;
            movedSinceLastAction = true;  // 주행 발생 → 앵커 재사용 불가
            if (node.NodePosition != null)
            {
                state.AgvPosition!.X = node.NodePosition.X;
                state.AgvPosition.Y = node.NodePosition.Y;
                state.AgvPosition.MapId = node.NodePosition.MapId;
            }
            Console.WriteLine($"[SIM] 노드 도착: {node.NodeId}");

            foreach (var action in node.Actions)
                await ExecuteActionAsync(action);
            await PublishStateAsync();
        }
        Console.WriteLine($"[SIM] Order 완료: {order.OrderId}");
    }
    finally { gate.Release(); }
}

async Task ExecuteActionAsync(VdaAction action)
{
    var actionState = state.ActionStates.First(s => s.ActionId == action.ActionId);
    actionState.ActionStatus = "RUNNING";
    await PublishStateAsync();

    // ── 1. 실패 주입 [WP-4 §5.3] ──
    if (failActionIds.Contains(action.ActionId)
        || (failJobRefs.Count > 0 && failJobRefs.Contains(
                action.ActionParameters.FirstOrDefault(p => p.Key == "jobRef")?.Value?.ToString() ?? "")))
    {
        await Task.Delay(sharedMs);
        Fail(actionState, "INJECTED");
        Console.WriteLine($"[SIM]   액션 실패(주입): {action.ActionId}");
        await PublishStateAsync();
        return;
    }

    if (action.ActionType == "startWeldInspection")
    {
        // ── 2. 파라미터 검증 [WP-4 §5.1 / SPEC §4.1] ──
        var (ok, violations, jobRef, anchorGroupId, seqInGroup) = WeldInspectionParams.Validate(action);
        if (!ok)
        {
            Console.WriteLine($"[SIM]   파라미터 위반: {string.Join(", ", violations)}");
            state.Errors.Add(new VdaError
            {
                ErrorType = "paramValidation", ErrorLevel = "WARNING",
                ErrorDescription = $"{action.ActionId}: {string.Join(",", violations)}"
            });
            Fail(actionState, $"PARAM({string.Join(",", violations)})");
            await PublishStateAsync();
            return;
        }

        // ── 3. 앵커 공유 판정 [WP-4 §5.2] ──
        var shared = currentAnchorGroup != null
                     && currentAnchorGroup == anchorGroupId
                     && !movedSinceLastAction;
        var label = shared ? "정렬 공유(⑤~⑦)" : "정렬 포함(①~⑧)";
        Console.WriteLine($"[SIM]   검사 시작: {jobRef} · {anchorGroupId} #{seqInGroup} · {label}");
        await Task.Delay(shared ? sharedMs : fullMs);

        currentAnchorGroup = anchorGroupId;   // 성공 → 앵커 유효
        movedSinceLastAction = false;
        actionState.ActionStatus = "FINISHED";
        actionState.ResultDescription = $"OK;anchor={(shared ? "SHARED" : "FULL")};jobRef={jobRef}";
        Console.WriteLine($"[SIM]   액션 완료: {action.ActionType} ({(shared ? "SHARED" : "FULL")})");
    }
    else
    {
        // 기타 액션 — 기존 동작 유지 (촬영 성공 응답 [ADR-004])
        await Task.Delay(fullMs);
        actionState.ActionStatus = "FINISHED";
        actionState.ResultDescription = "OK";
        Console.WriteLine($"[SIM]   액션 완료: {action.ActionType}");
    }
    await PublishStateAsync();
}

void Fail(ActionState actionState, string reason)
{
    actionState.ActionStatus = "FAILED";
    actionState.ResultDescription = $"FAIL;reason={reason}";
    currentAnchorGroup = null;   // 실패 → 앵커 무효화 (다음 seam은 재정렬)
}

async Task PublishStateAsync()
{
    state.HeaderId = ++headerId;
    state.Timestamp = Vda5050Header.NowIso();   // 밀리초+Z [SPEC §3 N2]
    await PublishAsync(stateTopic, state);
}

// initPosition actionParameters 안전 파싱 — Value는 object(역직렬화 시 JsonElement)일 수 있음
static string? ParamStr(IEnumerable<ActionParameter> ps, string key)
{
    var v = ps.FirstOrDefault(p => p.Key == key)?.Value;
    return v switch
    {
        null => null,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(),
        _ => v.ToString()
    };
}

static double? ParamNum(IEnumerable<ActionParameter> ps, string key)
{
    var v = ps.FirstOrDefault(p => p.Key == key)?.Value;
    return v switch
    {
        null => null,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        JsonElement je when je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), out var d) => d,
        double d => d,
        _ => double.TryParse(v.ToString(), out var d) ? d : null
    };
}

async Task PublishAsync<T>(string topic, T payload, bool retain = false)
{
    var msg = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(JsonSerializer.Serialize(payload, json))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .WithRetainFlag(retain)
        .Build();
    await client.PublishAsync(msg);
}

/// <summary>
/// startWeldInspection actionParameters 검증기 [SPEC_PHASE2_ACS.md §4.1 param_schema 필수 필드].
/// ActionParameter.Value가 object(JsonElement)든 JSON 문자열 폴백이든 모두 수용한다.
/// </summary>
static class WeldInspectionParams
{
    public static (bool Ok, List<string> Violations, string JobRef, string AnchorGroupId, int SeqInGroup)
        Validate(VdaAction action)
    {
        var violations = new List<string>();
        var jobRef = GetString(action, "jobRef");
        if (string.IsNullOrEmpty(jobRef)) violations.Add("jobRef");

        var position = GetObject(action, "position");
        if (position == null) violations.Add("position");
        else
        {
            foreach (var key in new[] { "seamStartW", "seamEndW" })   // [SPEC v2: wallNormalW 제거]
                if (!IsVec3(position.Value, key)) violations.Add($"position.{key}");
            if (!position.Value.TryGetProperty("drawingPos", out var dp) || dp.ValueKind != JsonValueKind.Object)
                violations.Add("position.drawingPos");
            else
                foreach (var key in new[] { "u", "v" })   // 벽면-로컬 좌표 [VDA5050_INTERFACE_SPEC §8.2]
                    if (!dp.TryGetProperty(key, out var uv) || uv.ValueKind != JsonValueKind.Number)
                        violations.Add($"position.drawingPos.{key}");
        }

        var anchorGroupId = ""; var seqInGroup = 0;
        var prms = GetObject(action, "params");
        if (prms == null) violations.Add("params");
        else
        {
            foreach (var key in new[] { "seamType", "sectionDxfId", "inspectionProfileId", "standoffMm" })
                if (!prms.Value.TryGetProperty(key, out _)) violations.Add($"params.{key}");
            if (prms.Value.TryGetProperty("anchorGroupId", out var ag) && ag.ValueKind == JsonValueKind.String)
                anchorGroupId = ag.GetString() ?? "";
            else violations.Add("params.anchorGroupId");
            if (prms.Value.TryGetProperty("seqInGroup", out var sq) && sq.TryGetInt32(out var sqv) && sqv >= 1)
                seqInGroup = sqv;
            else violations.Add("params.seqInGroup");
        }
        return (violations.Count == 0, violations, jobRef ?? "", anchorGroupId, seqInGroup);
    }

    static string? GetString(VdaAction a, string key)
    {
        var v = a.ActionParameters.FirstOrDefault(p => p.Key == key)?.Value;
        return v switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            string s => s,
            _ => v.ToString()
        };
    }

    /// <summary>object 또는 JSON 문자열 폴백(Acs:Vda:StringifyActionParams) 모두 파싱.</summary>
    static JsonElement? GetObject(VdaAction a, string key)
    {
        var v = a.ActionParameters.FirstOrDefault(p => p.Key == key)?.Value;
        try
        {
            return v switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } je => je,
                JsonElement { ValueKind: JsonValueKind.String } js when js.GetString() is { } s
                    => JsonSerializer.Deserialize<JsonElement>(s),
                string s => JsonSerializer.Deserialize<JsonElement>(s),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    static bool IsVec3(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var arr)
           && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 3;
}
