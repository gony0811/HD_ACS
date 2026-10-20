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
//  4. 통신 단절 일시정지 주입 [HD_AMR 통신단절 Fail-Safe §3/§5]: SIM_COMM_LOSS_PAUSE_ORDERS=N 이면 처음 N개 액션을
//     RUNNING 직후 PAUSED + state.paused=true + errors communicationLostPause 로 멈추고 stopPause/cancelOrder 대기.
//     (실제 MQTT 단절 판정은 시뮬레이터 범위 밖 — "복구 직후 PAUSE 보고" 시점부터 재현.)
//     SIM_RESUME_REJECT=1 이면 첫 stopPause를 resumeRejected로 거부.
//  5. 장치 HEAVY 알람 주입 [HD_AMR 장치통신 HEAVY알람 §3/§4]: SIM_DEVICE_FAULT_ORDERS=N 이면 처음 N개 Order에서
//     첫 액션 RUNNING 직후 cobotLinkLost(FATAL, errorReferences device=cobot) 발생 → 남은 액션 FAILED("aborted: … (HEAVY)").
//     알람 중 수신 Order는 폐기 + orderRejectedDeviceFault(orderId·actionId·device 참조, 기존 orderId 유지).
//     SIM_DEVICE_FAULT_RESET_SEC=S(기본 20): S초 후 "복구됨, RESET 대기"(Latched), 다시 S초 후 RESET(에러 제거).
//  3. 실패 주입: 환경변수 SIM_FAIL_ACTION_IDS(콤마 구분 actionId)에 해당하면
//     FAILED, resultDescription="FAIL;reason=INJECTED".
//
// 사용: dotnet run [brokerHost] [manufacturer] [serialNumber] [mapId]

var initialMapId = args.ElementAtOrDefault(3) ?? "CT1-L1";
var control = new SimulatorControlState(initialMapId, 95, 0, 0, 0);
var simulatorTask = Task.Run(async () =>
{
var broker = args.ElementAtOrDefault(0) ?? "localhost";
var robot = new RobotRef("AMR-01", args.ElementAtOrDefault(1) ?? "HHI", args.ElementAtOrDefault(2) ?? "AMR-01");
var mapId = initialMapId;
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

// Order 거부 주입 — 처음 N개 Order를 §4.5.2대로 거부: 폐기(state 필드 미갱신) + errors orderValidationError
var rejectMax = int.TryParse(Environment.GetEnvironmentVariable("SIM_REJECT_MAX"), out var rj) ? rj : 0;
var rejectCount = 0;

// 통신 단절 일시정지 주입 [통신단절 Fail-Safe] — 처음 N개 액션을 PAUSED로 멈추고 stopPause/cancelOrder 대기
var commLossMax = int.TryParse(Environment.GetEnvironmentVariable("SIM_COMM_LOSS_PAUSE_ORDERS"), out var cl) ? cl : 0;
var commLossCount = 0;
var resumeRejectPending = Environment.GetEnvironmentVariable("SIM_RESUME_REJECT") == "1";
TaskCompletionSource<string>? pauseWait = null;   // "resume" | "cancel"

// 장치 HEAVY 알람 주입 [장치통신 HEAVY알람] — 처음 N개 Order를 코봇 통신 두절로 중단, S초 후 복구·S초 후 RESET
var deviceFaultMax = int.TryParse(Environment.GetEnvironmentVariable("SIM_DEVICE_FAULT_ORDERS"), out var dfo) ? dfo : 0;
var deviceFaultCount = 0;
var deviceFaultResetSec = int.TryParse(Environment.GetEnvironmentVariable("SIM_DEVICE_FAULT_RESET_SEC"), out var dfr) ? dfr : 20;
const string DeviceFaultType = "cobotLinkLost";

// 상시 오류 주입 — 모든 state에 해당 errorType 포함 (예: localizationLost, emergencyStopActive — §6.4 분기 검증용)
var raiseErrorType = Environment.GetEnvironmentVariable("SIM_RAISE_ERROR");

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
    .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))   // 통신단절 Fail-Safe §2.2 — 로봇·관제 동일 5 s
    // Last Will: 비정상 종료 시 CONNECTIONBROKEN (두절 감지의 근거 [ADR-002])
    .WithWillTopic(connTopic)
    .WithWillPayload(JsonSerializer.Serialize(new Vda5050Connection
        { ConnectionState = "CONNECTIONBROKEN", Manufacturer = robot.Manufacturer, SerialNumber = robot.SerialNumber }))
    .WithWillRetain(true)
    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)   // 사양서 §2.4: Will QoS 1
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
        var incoming = JsonSerializer.Deserialize<Vda5050Order>(payload)!;

        // 장치 HEAVY 알람 중 Order 거부 [장치통신 HEAVY알람 §4.2] — 폐기, 기존 orderId 유지, orderRejectedDeviceFault
        if (state.Errors.Any(er => er.ErrorType == DeviceFaultType))
        {
            var refs = new List<ErrorReference> { new() { ReferenceKey = "orderId", ReferenceValue = incoming.OrderId } };
            if (incoming.Nodes.SelectMany(n => n.Actions).FirstOrDefault() is { } firstAction)
                refs.Add(new() { ReferenceKey = "actionId", ReferenceValue = firstAction.ActionId });
            refs.Add(new() { ReferenceKey = "device", ReferenceValue = "cobot" });
            state.Errors.RemoveAll(er => er.ErrorType == "orderRejectedDeviceFault");
            state.Errors.Add(new VdaError
            {
                ErrorType = "orderRejectedDeviceFault", ErrorLevel = "WARNING", ErrorReferences = refs,
                ErrorDescription = "장치 HEAVY 알람 — 코봇(끊김|복구됨·RESET 대기), 복구 후 RESET 필요",
            });
            Console.WriteLine($"[SIM] Order 거부(장치 HEAVY 알람): {incoming.OrderId} — 폐기, 기존 orderId 유지");
            _ = PublishStateAsync();
            return;
        }

        // Order 거부 주입 [§4.5.2]: 폐기(이전 orderId·actionStates 유지 보고) + orderValidationError(description에 orderId)
        if (rejectCount < rejectMax)
        {
            rejectCount++;
            state.Errors.RemoveAll(er => er.ErrorType == "orderValidationError");
            state.Errors.Add(new VdaError
            {
                ErrorType = "orderValidationError", ErrorLevel = "WARNING",
                ErrorDescription = $"Order 거부(주입 {rejectCount}/{rejectMax}): orderId={incoming.OrderId} 사유=validation",
            });
            Console.WriteLine($"[SIM] Order 거부(주입): {incoming.OrderId} — 폐기, errors 보고");
            _ = PublishStateAsync();
            return;
        }

        currentOrder = incoming;
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
            if (a.ActionType == "stopPause")
            {
                if (pauseWait is null) Console.WriteLine("[SIM] stopPause — 일시정지 상태 아님(무시)");
                else if (resumeRejectPending)
                {
                    resumeRejectPending = false;
                    state.Errors.RemoveAll(er => er.ErrorType == "resumeRejected");
                    state.Errors.Add(new VdaError { ErrorType = "resumeRejected", ErrorLevel = "WARNING",
                        ErrorDescription = "재개 거부(주입): 비상정지 해제 필요" });
                    Console.WriteLine("[SIM] stopPause 거부(주입) — resumeRejected 보고");
                    await PublishStateAsync();
                }
                else pauseWait.TrySetResult("resume");
            }
            if (a.ActionType == "cancelOrder")
            {
                if (pauseWait is null) Console.WriteLine("[SIM] cancelOrder — 일시정지 중 Order 없음(무시)");
                else pauseWait.TrySetResult("cancel");
            }
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

// 주기 state 보고 (2초). UI 변경 요청은 다음 주기를 기다리지 않고 즉시 반영·발행한다.
while (!control.CancellationToken.IsCancellationRequested)
{
    while (control.TryTakeUpdate(out var update))
    {
        state.AgvPosition!.MapId = update.MapId;
        state.AgvPosition.X = update.X;
        state.AgvPosition.Y = update.Y;
        state.AgvPosition.Theta = update.Theta;
        state.AgvPosition.PositionInitialized = true;
        state.BatteryState ??= new BatteryState();
        state.BatteryState.BatteryCharge = update.BatteryPct;
        Console.WriteLine($"[SIM] UI 상태 변경 → map={update.MapId}, " +
                          $"SLAM=({update.X:F3}, {update.Y:F3}, {update.Theta:F3} rad), " +
                          $"battery={update.BatteryPct:F1}%");
    }
    await PublishStateAsync();
    await control.WaitForUpdateAsync(TimeSpan.FromSeconds(2));
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
                // 정차각(theta)도 반영 — ACS 3D 뷰의 heading 화살표 검증용(실 AMR은 도착 자세를 보고)
                if (node.NodePosition.Theta is double th) state.AgvPosition.Theta = th;
            }
            Console.WriteLine($"[SIM] 노드 도착: {node.NodeId}");

            bool cancelled = false;
            foreach (var action in node.Actions)
                if (!await ExecuteActionAsync(action)) { cancelled = true; break; }
            await PublishStateAsync();
            if (cancelled) { Console.WriteLine($"[SIM] Order 중단됨: {order.OrderId}"); return; }
        }
        Console.WriteLine($"[SIM] Order 완료: {order.OrderId}");
    }
    finally { gate.Release(); }
}

/// <returns>false = cancelOrder 또는 장치 HEAVY 알람으로 Order 중단(이후 액션 실행 중단)</returns>
async Task<bool> ExecuteActionAsync(VdaAction action)
{
    var actionState = state.ActionStates.First(s => s.ActionId == action.ActionId);
    actionState.ActionStatus = "RUNNING";
    await PublishStateAsync();

    // ── 0. 통신 단절 일시정지 주입 [통신단절 Fail-Safe §3/§5] ──
    if (commLossCount < commLossMax)
    {
        commLossCount++;
        var t0 = DateTimeOffset.UtcNow;
        pauseWait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        actionState.ActionStatus = "PAUSED";
        state.Paused = true;
        state.Errors.RemoveAll(er => er.ErrorType == "communicationLostPause");
        state.Errors.Add(new VdaError { ErrorType = "communicationLostPause", ErrorLevel = "WARNING",
            ErrorDescription = $"PAUSE(주입 {commLossCount}/{commLossMax}) t0={t0:HH:mm:ss} 조건=A" });
        Console.WriteLine($"[SIM]   통신 단절 일시정지(주입): {action.ActionId} — stopPause/cancelOrder 대기");
        await PublishStateAsync();

        var outcome = await pauseWait.Task;
        pauseWait = null;
        state.Paused = false;
        state.Errors.RemoveAll(er => er.ErrorType is "communicationLostPause" or "resumeRejected");
        if (outcome == "cancel")
        {
            // cancelOrder: 미완료 액션은 FAILED(VDA 표준), communicationLostPause 클리어 [Fail-Safe §5]
            foreach (var a in state.ActionStates.Where(a => a.ActionStatus is not ("FINISHED" or "FAILED")))
            { a.ActionStatus = "FAILED"; a.ResultDescription = "FAIL;reason=CANCELLED"; }
            currentAnchorGroup = null;
            Console.WriteLine("[SIM]   cancelOrder 수신 — Order 폐기");
            await PublishStateAsync();
            return false;
        }
        actionState.ActionStatus = "RUNNING";
        Console.WriteLine($"[SIM]   stopPause 수신 — 재개(정지 직전 웨이포인트부터 재촬영), 정지 {(DateTimeOffset.UtcNow - t0).TotalSeconds:F0}s");
        await PublishStateAsync();
    }

    // ── 0b. 장치 HEAVY 알람 주입 [장치통신 HEAVY알람 §3.2] — 임무 즉시 중단, 남은 액션 FAILED ──
    if (deviceFaultCount < deviceFaultMax)
    {
        deviceFaultCount++;
        var t0 = DateTimeOffset.Now;
        const string reason = "상태 소켓(20004) 끊김";
        state.Errors.Add(new VdaError
        {
            ErrorType = DeviceFaultType, ErrorLevel = "FATAL",
            ErrorDescription = $"코봇 통신 두절 t0={t0:HH:mm:ss} — {reason}",
            ErrorReferences = new() { new() { ReferenceKey = "device", ReferenceValue = "cobot" } },
        });
        foreach (var a in state.ActionStates.Where(a => a.ActionStatus is not ("FINISHED" or "FAILED")))
        { a.ActionStatus = "FAILED"; a.ResultDescription = $"aborted: 코봇 통신 두절({reason}) (HEAVY)"; }
        currentAnchorGroup = null;
        Console.WriteLine($"[SIM]   장치 HEAVY 알람(주입 {deviceFaultCount}/{deviceFaultMax}): cobotLinkLost — 임무 중단, {deviceFaultResetSec}s 후 복구");
        await PublishStateAsync();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(deviceFaultResetSec));
            if (state.Errors.FirstOrDefault(er => er.ErrorType == DeviceFaultType) is { } latched)
                latched.ErrorDescription = $"코봇 통신 두절 t0={t0:HH:mm:ss} — 복구됨, RESET 대기";
            Console.WriteLine("[SIM]   코봇 통신 복구 — RESET 대기(Latched)");
            await PublishStateAsync();
            await Task.Delay(TimeSpan.FromSeconds(deviceFaultResetSec));
            state.Errors.RemoveAll(er => er.ErrorType == DeviceFaultType);
            Console.WriteLine("[SIM]   알람 RESET — cobotLinkLost 해제, Order 수락 재개");
            await PublishStateAsync();
        });
        return false;
    }

    // ── 1. 실패 주입 [WP-4 §5.3] ──
    if (failActionIds.Contains(action.ActionId)
        || (failJobRefs.Count > 0 && failJobRefs.Contains(
                action.ActionParameters.FirstOrDefault(p => p.Key == "jobRef")?.Value?.ToString() ?? "")))
    {
        await Task.Delay(sharedMs);
        Fail(actionState, "INJECTED");
        Console.WriteLine($"[SIM]   액션 실패(주입): {action.ActionId}");
        await PublishStateAsync();
        return true;
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
                ErrorType = "inspectionFailed", ErrorLevel = "WARNING",   // §6.4 확정 7종 코드 — 액션 단위 실패
                ErrorDescription = $"{action.ActionId}: {string.Join(",", violations)}"
            });
            Fail(actionState, $"PARAM({string.Join(",", violations)})");
            await PublishStateAsync();
            return true;
        }

        // ── 3. 앵커 공유 판정 [WP-4 §5.2] ──
        var shared = currentAnchorGroup != null
                     && currentAnchorGroup == anchorGroupId
                     && !movedSinceLastAction;
        var label = shared ? "정렬 공유(⑤~⑦)" : "정렬 포함(①~⑧)";
        var (simTaskId, simAttempt) = WeldInspectionParams.ReadTaskRef(action);
        Console.WriteLine($"[SIM]   검사 시작: {jobRef} · {anchorGroupId} #{seqInGroup} · {label} · task={simTaskId} attempt={simAttempt}");
        await Task.Delay(shared ? sharedMs : fullMs);

        currentAnchorGroup = anchorGroupId;   // 성공 → 앵커 유효
        movedSinceLastAction = false;
        actionState.ActionStatus = "FINISHED";
        actionState.ResultDescription = $"OK;anchor={(shared ? "SHARED" : "FULL")};jobRef={jobRef};attempt={simAttempt}";
        Console.WriteLine($"[SIM]   액션 완료: {action.ActionType} ({(shared ? "SHARED" : "FULL")})");
    }
    else if (action.ActionType == "moveToSeamStart")
    {
        // 코봇툴 seam 시작점 이동 시험 [VDA §8] — 주행 후 코봇을 seamStartW까지만 이동(촬영 없음).
        string seam = "?";
        try
        {
            var posParam = action.ActionParameters.FirstOrDefault(p => p.Key == "position");
            if (posParam?.Value is not null)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(posParam.Value));
                if (node?["seamStartW"]?.AsArray() is { Count: 3 } sw)
                    seam = $"({sw[0]},{sw[1]},{sw[2]})";
            }
        }
        catch { /* 로그용 — 파싱 실패 무해 */ }
        await Task.Delay(fullMs);
        actionState.ActionStatus = "FINISHED";
        actionState.ResultDescription = "OK;moveToSeamStart";
        Console.WriteLine($"[SIM]   코봇 seam 시작점 이동 시험 완료 → seamStartW={seam}");
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
    return true;
}

void Fail(ActionState actionState, string reason)
{
    actionState.ActionStatus = "FAILED";
    actionState.ResultDescription = $"FAIL;reason={reason}";
    currentAnchorGroup = null;   // 실패 → 앵커 무효화 (다음 seam은 재정렬)
}

async Task PublishStateAsync()
{
    // 상시 오류 주입 — 같은 유형 최신 1건 유지 규칙(§6.4) 준수
    if (!string.IsNullOrWhiteSpace(raiseErrorType) && !state.Errors.Any(er => er.ErrorType == raiseErrorType))
        state.Errors.Add(new VdaError
        { ErrorType = raiseErrorType, ErrorLevel = "WARNING", ErrorDescription = $"주입 오류({raiseErrorType})" });

    state.HeaderId = ++headerId;
    state.Timestamp = Vda5050Header.NowIso();   // 밀리초+Z [SPEC §3 N2]
    await PublishAsync(stateTopic, state);
    if (state.AgvPosition is { } position && state.BatteryState is { } battery)
        control.ReportPublished(new SimulatorStateSnapshot(
            position.MapId ?? "", battery.BatteryCharge,
            position.X, position.Y, position.Theta, state.Timestamp));
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
});

SimulatorUi.Run(control);
control.Stop();
try { await simulatorTask; }
catch (OperationCanceledException) { }

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
            // taskId·attempt [사양서 개정 1.4] — 선택 필드지만 실려 왔다면 형식이 맞아야 한다(조용한 오귀속 방지)
            if (prms.Value.TryGetProperty("taskId", out var tk) &&
                !(tk.ValueKind == JsonValueKind.String && Guid.TryParse(tk.GetString(), out _)))
                violations.Add("params.taskId");
            if (prms.Value.TryGetProperty("attempt", out var at) &&
                !(at.ValueKind == JsonValueKind.Number && at.TryGetInt32(out var atv) && atv is >= 1 and <= 255))
                violations.Add("params.attempt");
        }
        return (violations.Count == 0, violations, jobRef ?? "", anchorGroupId, seqInGroup);
    }

    /// <summary>params.taskId·attempt 추출(검증 통과 후 호출). 미탑재(구버전 ACS)면 "-" / 1 — 실제 AMR 폴백과 동일.</summary>
    public static (string TaskId, int Attempt) ReadTaskRef(VdaAction action)
    {
        var prms = GetObject(action, "params");
        string taskId = prms is { } p1 && p1.TryGetProperty("taskId", out var tk) ? tk.GetString() ?? "-" : "-";
        int attempt = prms is { } p2 && p2.TryGetProperty("attempt", out var at) && at.TryGetInt32(out var v) ? v : 1;
        return (taskId, attempt);
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
