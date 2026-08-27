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
// [통신 프로토콜 E2E 확장 — 두절/재접속/재동기화, ADR-002]
//  4. 테스트 제어 채널: acs-sim/control/{manufacturer}/{serial} (VDA 5050 외부 — 하네스 전용).
//     {"cmd":"drop","downMs":1500} 수신 시 소켓을 급단절(Dispose)해
//     브로커가 Last Will(connection=CONNECTIONBROKEN, retain)을 발행하도록 유도한 뒤,
//     downMs 후 자동 재접속 → 재구독 → ONLINE(retain) + 현재 state(retain) 재발행.
//     - state는 retain 발행 → 마스터(ACS)가 재기동/재접속해도 진행 중 Order를 즉시 회수(재동기화).
//     - 급단절 중이던 Order 실행 태스크는 메모리에서 계속 진행하며 발행만 스킵 →
//       재접속 후 이어서 완료 state를 발행(연속성 = resume 관측 계약).
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

// 실행 시간 (ms) — 테스트 고속화를 위해 환경변수로 조절 가능
var travelMs = int.TryParse(Environment.GetEnvironmentVariable("SIM_TRAVEL_MS"), out var t1) ? t1 : 800;
var fullMs   = int.TryParse(Environment.GetEnvironmentVariable("SIM_FULL_MS"),   out var t2) ? t2 : 1200;
var sharedMs = int.TryParse(Environment.GetEnvironmentVariable("SIM_SHARED_MS"), out var t3) ? t3 : 400;

var factory = new MqttFactory();
var client = factory.CreateMqttClient();
var connected = false;                                   // 발행 게이트(급단절 창 동안 발행 스킵)
var connTopic = Vda5050Topics.Connection(robot);
var stateTopic = Vda5050Topics.State(robot);
// 하네스 전용 제어 채널 (VDA 5050 아님) — 두절/재접속 시나리오 오케스트레이션
var controlTopic = $"acs-sim/control/{robot.Manufacturer}/{robot.SerialNumber}";

MqttClientOptions BuildOptions() => new MqttClientOptionsBuilder()
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
    BatteryState = new BatteryState { BatteryCharge = 95 }
};
var headerId = 0;
Vda5050Order? currentOrder = null;
var gate = new SemaphoreSlim(1, 1);

// ── 앵커 캐시 상태 [WP-4 §5.2] ──
string? currentAnchorGroup = null;   // 유효한 앵커의 그룹 ID (null = 앵커 없음)
var movedSinceLastAction = true;     // 직전 액션 이후 주행 발생 여부

async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
{
    var payload = e.ApplicationMessage.ConvertPayloadToString();
    if (e.ApplicationMessage.Topic == controlTopic)
    {
        // 하네스 제어 (VDA 5050 아님) — 두절/재접속 오케스트레이션
        SimControl? cmd = null;
        try { cmd = JsonSerializer.Deserialize<SimControl>(payload); } catch (JsonException) { }
        if (cmd?.Cmd == "drop")
            // 수신 콜백에서 자기 자신을 Dispose하면 데드락 → 별도 태스크로 실행
            _ = Task.Run(() => DropAndReconnectAsync(cmd.DownMs > 0 ? cmd.DownMs : 1500));
        return;
    }
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
}

// (재)접속 + 채널 재구독 + ONLINE/state(retain) 재발행 — 최초 기동과 재접속에서 공용
async Task ConnectAndAnnounceAsync()
{
    client = factory.CreateMqttClient();
    client.ApplicationMessageReceivedAsync += OnMessageAsync;
    await client.ConnectAsync(BuildOptions());
    connected = true;
    await client.SubscribeAsync(Vda5050Topics.Order(robot), MqttQualityOfServiceLevel.AtLeastOnce);
    await client.SubscribeAsync(Vda5050Topics.InstantActions(robot), MqttQualityOfServiceLevel.AtLeastOnce);
    await client.SubscribeAsync(controlTopic, MqttQualityOfServiceLevel.AtLeastOnce);

    await PublishAsync(connTopic, new Vda5050Connection
        { ConnectionState = "ONLINE", Manufacturer = robot.Manufacturer, SerialNumber = robot.SerialNumber }, retain: true);
    await PublishStateAsync();   // 진행 중 Order 포함 현재 state를 retain 발행 → 마스터 재동기화 근거
}

// 급단절(Last Will 발화) → downMs 후 자동 재접속 [ADR-002]
async Task DropAndReconnectAsync(int downMs)
{
    connected = false;
    var stale = client;
    stale.Dispose();   // DISCONNECT 없이 소켓 급단절 → 브로커가 retain된 Last Will(CONNECTIONBROKEN) 발행
    Console.WriteLine($"[SIM] ✂ 두절 (급단절, {downMs}ms 후 재접속) — Last Will=CONNECTIONBROKEN 기대");
    await Task.Delay(downMs);
    try
    {
        await ConnectAndAnnounceAsync();
        Console.WriteLine($"[SIM] ↺ 재접속 완료 — ONLINE + state(order={state.OrderId}) 재동기화 발행");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SIM] 재접속 실패, 2초 후 재시도: {ex.Message}");
        await Task.Delay(2000);
        _ = Task.Run(() => DropAndReconnectAsync(0));
    }
}

await ConnectAndAnnounceAsync();
Console.WriteLine($"[SIM] {robot.SerialNumber} ONLINE (map={mapId}, broker={broker}, " +
                  $"failInject={failActionIds.Count}건, control={controlTopic})");

// 주기 state 보고 (2초) — 급단절 창에서는 PublishAsync가 스킵
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
    if (failActionIds.Contains(action.ActionId))
    {
        await Task.Delay(sharedMs);
        Fail(actionState, "INJECTED");
        Console.WriteLine($"[SIM]   액션 실패(주입): {action.ActionId}");
        await PublishStateAsync();
        return;
    }

    if (action.ActionType == "startWeldInspection")
    {
        // ── 2. 파라미터 검증 [VDA5050_INTERFACE §6 계약] ──
        var (ok, violations, wallId, orientation, patternType) = WeldInspectionParams.Validate(action);
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

        // ── 3. 검사 수행 (자세·시퀀스는 AMR 티칭 — 여기서는 wallId/orientation만 관측) ──
        Console.WriteLine($"[SIM]   검사 시작: wall={wallId} · {orientation} · {patternType}");
        await Task.Delay(fullMs);

        actionState.ActionStatus = "FINISHED";
        actionState.ResultDescription = $"OK;wall={wallId};orient={orientation};pattern={patternType}";
        Console.WriteLine($"[SIM]   액션 완료: {action.ActionType} ({wallId} {orientation})");
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
    state.Timestamp = DateTimeOffset.UtcNow.ToString("O");
    // state를 retain 발행 → 마스터(ACS) 재기동/재접속 시 마지막 상태를 즉시 회수(재동기화 [ADR-002])
    await PublishAsync(stateTopic, state, retain: true);
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
    if (!connected) return;   // 급단절 창 — 발행 스킵(진행 중 Order 태스크는 계속 진행)
    var msg = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(JsonSerializer.Serialize(payload, json))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .WithRetainFlag(retain)
        .Build();
    try { await client.PublishAsync(msg); }
    catch (Exception ex) { Console.WriteLine($"[SIM] 발행 실패(두절 추정): {ex.Message}"); }
}

/// <summary>하네스 제어 메시지 (VDA 5050 외부) — 두절/재접속 오케스트레이션.</summary>
sealed class SimControl
{
    [System.Text.Json.Serialization.JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("downMs")] public int DownMs { get; set; }
}

/// <summary>
/// startWeldInspection actionParameters 검증기 [SPEC_PHASE2_ACS.md §4.1 param_schema 필수 필드].
/// ActionParameter.Value가 object(JsonElement)든 JSON 문자열 폴백이든 모두 수용한다.
/// </summary>
static class WeldInspectionParams
{
    public static (bool Ok, List<string> Violations, string WallId, string Orientation, string PatternType)
        Validate(VdaAction action)
    {
        var violations = new List<string>();

        var wallId = GetString(action, "wallId");
        if (string.IsNullOrEmpty(wallId)) violations.Add("wallId");

        if (!IsVec3Param(action, "seamStart")) violations.Add("seamStart");
        if (!IsVec3Param(action, "seamEnd")) violations.Add("seamEnd");

        var orientation = GetString(action, "orientation");
        if (orientation is not ("H" or "V")) violations.Add("orientation");

        var patternType = GetString(action, "patternType");
        if (string.IsNullOrEmpty(patternType)) violations.Add("patternType");

        return (violations.Count == 0, violations, wallId ?? "", orientation ?? "", patternType ?? "");
    }

    /// <summary>top-level actionParameter 값이 3원소 숫자 배열인지(object/JsonElement/문자열 폴백 수용).</summary>
    static bool IsVec3Param(VdaAction a, string key)
    {
        var v = a.ActionParameters.FirstOrDefault(p => p.Key == key)?.Value;
        if (v is null) return false;
        try
        {
            JsonElement el = v switch
            {
                JsonElement je => je,
                string s => JsonSerializer.Deserialize<JsonElement>(s),
                _ => JsonSerializer.SerializeToElement(v),   // 익명 배열(new[]{...}) 등
            };
            return el.ValueKind == JsonValueKind.Array && el.GetArrayLength() == 3;
        }
        catch (JsonException) { return false; }
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
