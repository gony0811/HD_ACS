using System.Text.Json;
using HD.Acs.Vda5050;
using HD.Acs.Vda5050.Messages;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// VDA 5050 로봇(HD_AMR) 시뮬레이터 — NAMUGA ACS.AMR.Simulator 패턴 승계.
// Order 수신 → 노드 순회 + 액션 실행을 시뮬레이션하며 state를 발행한다.
// 사용: dotnet run [brokerHost] [manufacturer] [serialNumber] [mapId]

var broker = args.ElementAtOrDefault(0) ?? "localhost";
var robot = new RobotRef("AMR-01", args.ElementAtOrDefault(1) ?? "HHI", args.ElementAtOrDefault(2) ?? "AMR-01");
var mapId = args.ElementAtOrDefault(3) ?? "CT1-L1";
var json = new JsonSerializerOptions { WriteIndented = false };

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
    BatteryState = new BatteryState { BatteryCharge = 95 }
};
var headerId = 0;
Vda5050Order? currentOrder = null;
var gate = new SemaphoreSlim(1, 1);

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
                var newMap = a.ActionParameters.First(p => p.Key == "mapId").Value?.ToString() ?? mapId;
                state.AgvPosition!.MapId = newMap;
                Console.WriteLine($"[SIM] 재측위 완료 → mapId={newMap} (수동 층 전환 [Q9])");
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
Console.WriteLine($"[SIM] {robot.SerialNumber} ONLINE (map={mapId}, broker={broker})");

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
        state.OrderId = order.OrderId;
        state.OrderUpdateId = order.OrderUpdateId;
        state.ActionStates = order.Nodes.SelectMany(n => n.Actions)
            .Select(a => new ActionState { ActionId = a.ActionId, ActionType = a.ActionType, ActionStatus = "WAITING" })
            .ToList();

        foreach (var node in order.Nodes.OrderBy(n => n.SequenceId))
        {
            await Task.Delay(1500);   // 이동 시뮬레이션
            state.LastNodeId = node.NodeId;
            state.LastNodeSequenceId = node.SequenceId;
            if (node.NodePosition != null)
            {
                state.AgvPosition!.X = node.NodePosition.X;
                state.AgvPosition.Y = node.NodePosition.Y;
                state.AgvPosition.MapId = node.NodePosition.MapId;
            }
            Console.WriteLine($"[SIM] 노드 도착: {node.NodeId}");

            foreach (var action in node.Actions)   // 검사 액션 실행 시뮬레이션
            {
                var actionState = state.ActionStates.First(s => s.ActionId == action.ActionId);
                actionState.ActionStatus = "RUNNING";
                await PublishStateAsync();
                await Task.Delay(1000);
                actionState.ActionStatus = "FINISHED";   // 촬영 성공 응답 [ADR-004]
                Console.WriteLine($"[SIM]   액션 완료: {action.ActionType}");
                await PublishStateAsync();
            }
            await PublishStateAsync();
        }
        Console.WriteLine($"[SIM] Order 완료: {order.OrderId}");
    }
    finally { gate.Release(); }
}

async Task PublishStateAsync()
{
    state.HeaderId = ++headerId;
    state.Timestamp = DateTimeOffset.UtcNow.ToString("O");
    await PublishAsync(stateTopic, state);
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
