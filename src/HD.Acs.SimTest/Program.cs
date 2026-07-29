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
//  S1 golden-anchor : ST03(1 TASK) → ST04(2 TASK 동일 anchorGroup)
//                     기대: A1=FULL, A2=FULL, A3=SHARED, 모두 FINISHED
//  S2 invalid-param : wallNormalW·anchorGroupId 누락 → FAILED reason=PARAM(...)
//  S3 fail-injection: SIM_FAIL_ACTION_IDS 대상 → FAILED reason=INJECTED
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
var stateLock = new object();

client.ApplicationMessageReceivedAsync += e =>
{
    var st = JsonSerializer.Deserialize<Vda5050State>(e.ApplicationMessage.ConvertPayloadToString());
    if (st == null) return Task.CompletedTask;
    lock (stateLock)
        foreach (var a in st.ActionStates)
            actionStates[a.ActionId] = (a.ActionStatus, a.ResultDescription);
    return Task.CompletedTask;
};

await client.ConnectAsync(new MqttClientOptionsBuilder()
    .WithTcpServer(broker).WithClientId("simtest-master").WithCleanSession().Build());
await client.SubscribeAsync(Vda5050Topics.State(robot), MqttQualityOfServiceLevel.AtLeastOnce);
Console.WriteLine($"[TEST] 브로커 연결: {broker} · 대상 로봇 {robot.Manufacturer}/{robot.SerialNumber}");

var failures = new List<string>();

// ═══ S1: golden-anchor — 1노드 1TASK + 1노드 2TASK(앵커 공유) ═══
{
    var a1 = Guid.NewGuid().ToString();
    var a2 = Guid.NewGuid().ToString();
    var a3 = Guid.NewGuid().ToString();
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S1", OrderUpdateId = 0,
        Nodes =
        {
            Node("N-ST03", 0, 10.10, 5.117, 1.571,
                Inspection(a1, "JOB-CT1-L2-W03-S06-1", "CT1-L2-W03-ST03", 1)),
            Node("N-ST04", 2, 12.482, 5.117, 1.571,
                Inspection(a2, "JOB-CT1-L2-W03-S07-1", "CT1-L2-W03-ST04", 1),
                Inspection(a3, "JOB-CT1-L2-W03-S07-2", "CT1-L2-W03-ST04", 2)),
        },
        Edges = { new OrderEdge { EdgeId = "E1", SequenceId = 1, StartNodeId = "N-ST03", EndNodeId = "N-ST04" } }
    };
    var r = await RunScenarioAsync("S1 golden-anchor", order, new[] { a1, a2, a3 });
    Expect(r, a1, "FINISHED", "anchor=FULL",   "S1: A1(단독 TASK)은 정렬 포함이어야 함");
    Expect(r, a2, "FINISHED", "anchor=FULL",   "S1: A2(그룹 첫 TASK)는 정렬 포함이어야 함");
    Expect(r, a3, "FINISHED", "anchor=SHARED", "S1: A3(그룹 두번째)는 정렬 공유여야 함");
}

// ═══ S2: invalid-param — 필수 필드 누락 → PARAM 실패 ═══
{
    var a4 = Guid.NewGuid().ToString();
    var bad = new VdaAction
    {
        ActionType = "startWeldInspection", ActionId = a4, BlockingType = "HARD",
        ActionParameters =
        {
            new ActionParameter { Key = "jobRef", Value = "JOB-BAD-1" },
            // position: wallNormalW 누락
            new ActionParameter { Key = "position", Value = new {
                seamStartW = new[] { 12.51, 5.98, 1.42 },
                seamEndW   = new[] { 13.31, 5.98, 1.42 },
                drawingPos = new { tank = "CT1", level = 2, wall_code = "W03", x = 3.12, y = 0.0, z = 1.42 } } },
            // params: anchorGroupId 누락
            new ActionParameter { Key = "params", Value = new {
                seamType = "LINE", sectionDxfId = "DXF-CORR-T12",
                inspectionProfileId = "INSPECT-STD-01", standoffMm = 400, seqInGroup = 1 } },
        }
    };
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S2", OrderUpdateId = 0,
        Nodes = { Node("N-ST05", 0, 14.0, 5.117, 1.571, bad) }
    };
    var r = await RunScenarioAsync("S2 invalid-param", order, new[] { a4 });
    Expect(r, a4, "FAILED", "reason=PARAM", "S2: 필수 필드 누락은 PARAM 실패여야 함");
    ExpectContains(r, a4, "wallNormalW",   "S2: 위반 목록에 position.wallNormalW 포함");
    ExpectContains(r, a4, "anchorGroupId", "S2: 위반 목록에 params.anchorGroupId 포함");
}

// ═══ S3: fail-injection — SIM_FAIL_ACTION_IDS 대상 FAILED ═══
{
    var order = new Vda5050Order
    {
        OrderId = "SIMTEST-S3", OrderUpdateId = 0,
        Nodes = { Node("N-ST06", 0, 16.0, 5.117, 1.571,
            Inspection(FailActionId, "JOB-CT1-L2-W03-S08-1", "CT1-L2-W03-ST06", 1)) }
    };
    var r = await RunScenarioAsync("S3 fail-injection", order, new[] { FailActionId });
    Expect(r, FailActionId, "FAILED", "reason=INJECTED",
        "S3: 주입 대상은 INJECTED 실패여야 함 (시뮬레이터를 SIM_FAIL_ACTION_IDS로 기동했는지 확인)");
}

// ═══ 결과 ═══
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("[TEST] ✅ 전체 시나리오 PASS (S1 앵커 공유 · S2 파라미터 검증 · S3 실패 주입)");
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

/// <summary>골든 fixture(SPEC 부록 A) 형태의 유효 startWeldInspection 액션.</summary>
static VdaAction Inspection(string actionId, string jobRef, string anchorGroupId, int seqInGroup) => new()
{
    ActionType = "startWeldInspection", ActionId = actionId, BlockingType = "HARD",
    ActionParameters =
    {
        new ActionParameter { Key = "jobRef", Value = jobRef },
        new ActionParameter { Key = "position", Value = new {
            seamStartW  = new[] { 12.510, 5.980, 1.420 },
            seamEndW    = new[] { 13.310, 5.980, 1.420 },
            wallNormalW = new[] { 0.0, -1.0, 0.0 },
            drawingPos  = new { tank = "CT1", level = 2, wall_code = "W03", x = 3.120, y = 0.0, z = 1.420 } } },
        new ActionParameter { Key = "params", Value = new {
            seamType = "LINE", sectionDxfId = "DXF-CORR-T12",
            inspectionProfileId = "INSPECT-STD-01",
            standoffMm = 400, workingDistanceMm = 400,
            anchorGroupId, seqInGroup } },
    }
};
