using System.Text.Json;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// HD.Acs.SimTest — WP-4 시뮬레이터 검증 드라이버 [SPEC_PHASE2_ACS.md §5, §7 E2E]
// ACS 서버·DB 없이 마스터 역할을 흉내내어 골든 fixture Order를 발행하고,
// 시뮬레이터의 state(actionStates.resultDescription 계약)를 자동 검증한다.
//
// 시나리오:
//  S1 valid-weld    : 유효 검사 액션(수평 H·수직 V) → 모두 FINISHED, resultDescription OK;wall=...;orient=...
//  S2 invalid-param : seamStart 누락 + orientation 오류(H|V 아님) → FAILED reason=PARAM(...)
//  S3 fail-injection: SIM_FAIL_ACTION_IDS 대상 → FAILED reason=INJECTED
//  S4 conn-lifecycle: 구독 즉시 retain된 connection=ONLINE 수신 (마스터 재접속 회수)
//  S5 disconnect     : 제어 drop → Last Will CONNECTIONBROKEN 관측 → 자동 재접속 ONLINE [ADR-002]
//  S6 reconnect-sync : Order 진행 중 두절 → 재접속 후 전 액션 FINISHED + orderId 보존(재동기화)
//
// 사용: dotnet run -- [brokerHost] [manufacturer] [serialNumber]
//      (S3는 시뮬레이터가 SIM_FAIL_ACTION_IDS=<FailActionId>로 기동된 경우에만 PASS)

var broker = args.ElementAtOrDefault(0) ?? "localhost";
var robot = new RobotRef("AMR-01", args.ElementAtOrDefault(1) ?? "HHI", args.ElementAtOrDefault(2) ?? "AMR-01");
const string MapId = "CT1-L2";
const string FailActionId = "00000000-0000-4000-8000-00000000fa11"; // run_simtest.sh 와 공유하는 상수

var json = new JsonSerializerOptions { WriteIndented = false };
var client = new MqttFactory().CreateMqttClient();

// 수신한 최신 actionState (actionId → (status, resultDescription))
var actionStates = new Dictionary<string, (string Status, string? Result)>();
var latestState = (OrderId: "", OrderUpdateId: 0);   // 최신 state 헤더 (재동기화 검증용)
var connObserved = new List<string>();               // 관측된 connectionState 순서 (S4/S5)
var stateLock = new object();
var controlTopic = $"acs-sim/control/{robot.Manufacturer}/{robot.SerialNumber}";

client.ApplicationMessageReceivedAsync += e =>
{
    var topic = e.ApplicationMessage.Topic;
    var payload = e.ApplicationMessage.ConvertPayloadToString();
    if (topic.EndsWith("/connection"))
    {
        var c = JsonSerializer.Deserialize<Vda5050Connection>(payload);
        if (c != null) lock (stateLock) connObserved.Add(c.ConnectionState);
        return Task.CompletedTask;
    }
    var st = JsonSerializer.Deserialize<Vda5050State>(payload);
    if (st == null) return Task.CompletedTask;
    lock (stateLock)
    {
        latestState = (st.OrderId, st.OrderUpdateId);
        foreach (var a in st.ActionStates)
            actionStates[a.ActionId] = (a.ActionStatus, a.ResultDescription);
    }
    return Task.CompletedTask;
};

await client.ConnectAsync(new MqttClientOptionsBuilder()
    .WithTcpServer(broker).WithClientId("simtest-master").WithCleanSession().Build());
await client.SubscribeAsync(Vda5050Topics.State(robot), MqttQualityOfServiceLevel.AtLeastOnce);
await client.SubscribeAsync(Vda5050Topics.Connection(robot), MqttQualityOfServiceLevel.AtLeastOnce);
Console.WriteLine($"[TEST] 브로커 연결: {broker} · 대상 로봇 {robot.Manufacturer}/{robot.SerialNumber}");

var failures = new List<string>();

// ═══ S1: valid-weld — 유효 검사 액션(수평·수직) 모두 FINISHED ═══
{
    var a1 = Guid.NewGuid().ToString();
    var a2 = Guid.NewGuid().ToString();
    var a3 = Guid.NewGuid().ToString();
    var hStart = new[] { 12.510, 5.980, 1.420 }; var hEnd = new[] { 13.310, 5.980, 1.420 };   // 수평(Δz=0)
    var vStart = new[] { 12.510, 5.980, 1.000 }; var vEnd = new[] { 12.510, 5.980, 2.500 };   // 수직(Δz 큼)
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S1", OrderUpdateId = 0,
        Nodes =
        {
            Node("N-ST03", 0, 10.10, 5.117, 1.571,
                Inspection(a1, "SM", hStart, hEnd, "H")),
            Node("N-ST04", 2, 12.482, 5.117, 1.571,
                Inspection(a2, "SM", hStart, hEnd, "H"),
                Inspection(a3, "PM", vStart, vEnd, "V")),
        },
        Edges = { new OrderEdge { EdgeId = "E1", SequenceId = 1, StartNodeId = "N-ST03", EndNodeId = "N-ST04" } }
    };
    var r = await RunScenarioAsync("S1 valid-weld", order, new[] { a1, a2, a3 });
    Expect(r, a1, "FINISHED", "wall=SM",   "S1: A1 유효 검사(수평)는 FINISHED");
    Expect(r, a2, "FINISHED", "wall=SM",   "S1: A2 유효 검사(수평)는 FINISHED");
    Expect(r, a3, "FINISHED", "orient=V",  "S1: A3 유효 검사(수직)는 FINISHED·orient=V");
}

// ═══ S2: invalid-param — 필수 필드 누락/잘못된 값 → PARAM 실패 ═══
{
    var a4 = Guid.NewGuid().ToString();
    var bad = new VdaAction
    {
        ActionType = "startWeldInspection", ActionId = a4, BlockingType = "HARD",
        ActionParameters =
        {
            new ActionParameter { Key = "wallId", Value = "SM" },
            // seamStart 누락
            new ActionParameter { Key = "seamEnd", Value = new[] { 13.31, 5.98, 1.42 } },
            new ActionParameter { Key = "orientation", Value = "X" },   // 잘못된 값(H|V 아님)
            new ActionParameter { Key = "patternType", Value = "LINEAR" },
        }
    };
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S2", OrderUpdateId = 0,
        Nodes = { Node("N-ST05", 0, 14.0, 5.117, 1.571, bad) }
    };
    var r = await RunScenarioAsync("S2 invalid-param", order, new[] { a4 });
    Expect(r, a4, "FAILED", "reason=PARAM", "S2: 필수 필드 누락/오류는 PARAM 실패여야 함");
    ExpectContains(r, a4, "seamStart",   "S2: 위반 목록에 seamStart 포함(누락)");
    ExpectContains(r, a4, "orientation", "S2: 위반 목록에 orientation 포함(H|V 아님)");
}

// ═══ S3: fail-injection — SIM_FAIL_ACTION_IDS 대상 FAILED ═══
{
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S3", OrderUpdateId = 0,
        Nodes = { Node("N-ST06", 0, 16.0, 5.117, 1.571,
            Inspection(FailActionId, "SM", new[] { 12.510, 5.980, 1.420 }, new[] { 13.310, 5.980, 1.420 })) }
    };
    var r = await RunScenarioAsync("S3 fail-injection", order, new[] { FailActionId });
    Expect(r, FailActionId, "FAILED", "reason=INJECTED",
        "S3: 주입 대상은 INJECTED 실패여야 함 (시뮬레이터를 SIM_FAIL_ACTION_IDS로 기동했는지 확인)");
}

// ═══ S4: conn-lifecycle — 구독 즉시 retain된 ONLINE 회수 ═══
{
    Console.WriteLine("\n[TEST] ▶ S4 conn-lifecycle (retain된 connection 회수)");
    var online = await WaitConnAsync(s => s == "ONLINE", TimeSpan.FromSeconds(5));
    if (!online) failures.Add("S4: 구독 직후 retain된 connection=ONLINE 을 회수하지 못함");
    else Console.WriteLine("[TEST]   connection=ONLINE 회수 확인");
}

// ═══ S5: disconnect — 급단절 Last Will → CONNECTIONBROKEN → 자동 재접속 ONLINE ═══
{
    Console.WriteLine("\n[TEST] ▶ S5 disconnect (제어 drop → CONNECTIONBROKEN → 재접속)");
    lock (stateLock) connObserved.Clear();
    await PublishControlAsync(new { cmd = "drop", downMs = 1500 });

    var broken = await WaitConnAsync(s => s == "CONNECTIONBROKEN", TimeSpan.FromSeconds(8));
    if (!broken) failures.Add("S5: 급단절 시 Last Will(CONNECTIONBROKEN) 을 관측하지 못함");
    else Console.WriteLine("[TEST]   CONNECTIONBROKEN 관측 (두절 감지)");

    var back = await WaitConnAsync(s => s == "ONLINE", TimeSpan.FromSeconds(10));
    if (!back) failures.Add("S5: 재접속 후 ONLINE 복귀를 관측하지 못함");
    else Console.WriteLine("[TEST]   재접속 ONLINE 복귀 확인");
}

// ═══ S6: reconnect-sync — Order 진행 중 두절 → 재접속 후 전 액션 FINISHED + orderId 보존 ═══
{
    Console.WriteLine("\n[TEST] ▶ S6 reconnect-sync (진행 중 두절 → 재동기화)");
    var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString()).ToArray();
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S6", OrderUpdateId = 0,
        Nodes =
        {
            Node("N6-1", 0, 10.0, 5.117, 1.571, Inspection(ids[0], "SM", new[] { 10.0, 5.98, 1.42 }, new[] { 10.8, 5.98, 1.42 })),
            Node("N6-2", 2, 11.0, 5.117, 1.571, Inspection(ids[1], "SM", new[] { 11.0, 5.98, 1.42 }, new[] { 11.8, 5.98, 1.42 })),
            Node("N6-3", 4, 12.0, 5.117, 1.571, Inspection(ids[2], "SM", new[] { 12.0, 5.98, 1.42 }, new[] { 12.8, 5.98, 1.42 })),
            Node("N6-4", 6, 13.0, 5.117, 1.571, Inspection(ids[3], "SM", new[] { 13.0, 5.98, 1.42 }, new[] { 13.8, 5.98, 1.42 })),
        },
        Edges =
        {
            new OrderEdge { EdgeId = "E6-1", SequenceId = 1, StartNodeId = "N6-1", EndNodeId = "N6-2" },
            new OrderEdge { EdgeId = "E6-2", SequenceId = 3, StartNodeId = "N6-2", EndNodeId = "N6-3" },
            new OrderEdge { EdgeId = "E6-3", SequenceId = 5, StartNodeId = "N6-3", EndNodeId = "N6-4" },
        }
    };
    lock (stateLock) { foreach (var id in ids) actionStates.Remove(id); connObserved.Clear(); }

    order.HeaderId = 1; order.Timestamp = DateTimeOffset.UtcNow.ToString("O");
    order.Manufacturer = robot.Manufacturer; order.SerialNumber = robot.SerialNumber;
    await client.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic(Vda5050Topics.Order(robot))
        .WithPayload(JsonSerializer.Serialize(order, json))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

    // 진행 중반부에 두절 주입
    await Task.Delay(400);
    await PublishControlAsync(new { cmd = "drop", downMs = 1200 });
    Console.WriteLine("[TEST]   진행 중 두절 주입 (downMs=1200)");

    // 재접속 후 전 액션이 FINISHED 로 수렴하는지
    var deadline = DateTime.UtcNow.AddSeconds(30);
    var done = false;
    while (DateTime.UtcNow < deadline)
    {
        lock (stateLock)
            if (ids.All(id => actionStates.TryGetValue(id, out var s) && s.Status == "FINISHED"))
            { done = true; break; }
        await Task.Delay(200);
    }
    lock (stateLock)
    {
        foreach (var id in ids)
            Console.WriteLine($"[TEST]   {id[..8]}… → " +
                (actionStates.TryGetValue(id, out var s) ? s.Status : "TIMEOUT"));
        if (!done) failures.Add("S6: 재접속 후 전 액션이 FINISHED 로 수렴하지 못함 (재동기화 실패)");
        if (latestState.OrderId != "SIMTEST-S6")
            failures.Add($"S6: 재동기화 state의 orderId 불일치 — 기대 SIMTEST-S6, 실제 {latestState.OrderId}");
        var sawBroken = connObserved.Contains("CONNECTIONBROKEN");
        if (!sawBroken) failures.Add("S6: 진행 중 두절(CONNECTIONBROKEN) 관측 실패");
        if (done && sawBroken && latestState.OrderId == "SIMTEST-S6")
            Console.WriteLine("[TEST]   두절 후 재접속·전 액션 FINISHED·orderId 보존 확인");
    }
}

// ═══ 결과 ═══
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("[TEST] ✅ 전체 시나리오 PASS (S1 유효검사 · S2 파라미터 · S3 실패주입 · "
                      + "S4 conn회수 · S5 두절/재접속 · S6 재동기화)");
    await client.DisconnectAsync();
    return 0;
}
Console.WriteLine($"[TEST] ❌ 실패 {failures.Count}건:");
foreach (var f in failures) Console.WriteLine("  - " + f);
await client.DisconnectAsync();
return 1;

// ─── 헬퍼 ───

async Task<Dictionary<string, (string Status, string? Result)>> RunScenarioAsync(
    string name, Vda5050Order order, string[] ids)
{
    Console.WriteLine($"\n[TEST] ▶ {name} 발행 (actions={ids.Length})");
    lock (stateLock) foreach (var id in ids) actionStates.Remove(id);

    order.HeaderId = 1; order.Timestamp = DateTimeOffset.UtcNow.ToString("O");
    order.Manufacturer = robot.Manufacturer; order.SerialNumber = robot.SerialNumber;
    await client.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic(Vda5050Topics.Order(robot))
        .WithPayload(JsonSerializer.Serialize(order, json))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

    var deadline = DateTime.UtcNow.AddSeconds(40);
    while (DateTime.UtcNow < deadline)
    {
        lock (stateLock)
            if (ids.All(id => actionStates.TryGetValue(id, out var s)
                              && s.Status is "FINISHED" or "FAILED"))
                break;
        await Task.Delay(200);
    }
    lock (stateLock)
    {
        var snapshot = ids.ToDictionary(id => id,
            id => actionStates.TryGetValue(id, out var s) ? s : ("TIMEOUT", (string?)null));
        foreach (var id in ids)
            Console.WriteLine($"[TEST]   {id[..8]}… → {snapshot[id].Item1} ({snapshot[id].Item2 ?? "-"})");
        return snapshot;
    }
}

// 하네스 제어 채널로 명령 발행 (VDA 5050 외부)
async Task PublishControlAsync(object cmd)
{
    await client.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic(controlTopic)
        .WithPayload(JsonSerializer.Serialize(cmd, json))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
}

// connObserved 순서에서 조건을 만족하는 connectionState 가 나타날 때까지 대기
async Task<bool> WaitConnAsync(Func<string, bool> match, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        lock (stateLock)
            if (connObserved.Any(match)) return true;
        await Task.Delay(100);
    }
    return false;
}

void Expect(Dictionary<string, (string Status, string? Result)> r, string id,
    string status, string resultContains, string message)
{
    var (s, res) = r[id];
    if (s != status || res?.Contains(resultContains) != true)
        failures.Add($"{message} — 실제: {s} ({res ?? "-"})");
}

void ExpectContains(Dictionary<string, (string Status, string? Result)> r, string id,
    string fragment, string message)
{
    if (r[id].Result?.Contains(fragment) != true)
        failures.Add($"{message} — 실제: {r[id].Result ?? "-"}");
}

static OrderNode Node(string nodeId, int seq, double x, double y, double theta, params VdaAction[] actions)
{
    var n = new OrderNode
    {
        NodeId = nodeId, SequenceId = seq, Released = true,
        NodePosition = new NodePosition
        {
            X = x, Y = y, Theta = theta, MapId = MapId,
            AllowedDeviationXY = 0.08, AllowedDeviationTheta = 0.07
        }
    };
    n.Actions.AddRange(actions);
    return n;
}

/// <summary>유효 startWeldInspection 액션 [VDA5050_INTERFACE §6 계약: wallId·seamStart/End·orientation·patternType].</summary>
static VdaAction Inspection(string actionId, string wallId, double[] seamStart, double[] seamEnd,
    string orientation = "H", string patternType = "LINEAR") => new()
{
    ActionType = "startWeldInspection", ActionId = actionId, BlockingType = "HARD",
    ActionParameters =
    {
        new ActionParameter { Key = "wallId", Value = wallId },
        new ActionParameter { Key = "seamStart", Value = seamStart },
        new ActionParameter { Key = "seamEnd", Value = seamEnd },
        new ActionParameter { Key = "orientation", Value = orientation },
        new ActionParameter { Key = "patternType", Value = patternType },
    }
};
