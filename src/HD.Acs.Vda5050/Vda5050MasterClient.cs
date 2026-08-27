using System.Text.Json;
using HD.Acs.Vda5050.Messages;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HD.Acs.Vda5050;

/// <summary>
/// VDA 5050 마스터 컨트롤 MQTT 클라이언트 [ADR-001].
/// HD_ACS의 유일한 로봇측 인터페이스 — order/instantActions 발행, state/connection 구독.
/// </summary>
public sealed class Vda5050MasterClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IMqttClient _client;
    private readonly string _host;
    private readonly int _port;
    private readonly Dictionary<string, RobotRef> _robotsByTopicKey = new();
    private readonly Dictionary<string, int> _headerIdByTopic = new();   // 토픽별 단조 증가 [SPEC §3 N1]

    public event Func<RobotRef, Vda5050State, Task>? StateReceived;
    public event Func<RobotRef, Vda5050Connection, Task>? ConnectionReceived;

    public Vda5050MasterClient(string host, int port = 1883)
    {
        _host = host;
        _port = port;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_host, _port)
            .WithClientId("hd-acs-master")
            .WithCleanSession()
            .Build();
        await _client.ConnectAsync(options, ct);
    }

    /// <summary>로봇 등록: state/connection 토픽 구독 (fleet-ready — 로봇별 호출 [ADR-003])</summary>
    public async Task RegisterRobotAsync(RobotRef robot, CancellationToken ct = default)
    {
        _robotsByTopicKey[$"{robot.Manufacturer}/{robot.SerialNumber}"] = robot;
        await _client.SubscribeAsync(Vda5050Topics.State(robot), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await _client.SubscribeAsync(Vda5050Topics.Connection(robot), MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    public Task PublishOrderAsync(RobotRef robot, Vda5050Order order, CancellationToken ct = default)
    {
        var topic = Vda5050Topics.Order(robot);
        Stamp(order, robot, topic);
        return PublishAsync(topic, order, ct);
    }

    public Task PublishInstantActionsAsync(RobotRef robot, Vda5050InstantActions actions, CancellationToken ct = default)
    {
        var topic = Vda5050Topics.InstantActions(robot);
        Stamp(actions, robot, topic);
        return PublishAsync(topic, actions, ct);
    }

    /// <summary>비상정지 — 기능적 정지이며 안전 규격 정지가 아님 [ADR-007]</summary>
    public Task EmergencyStopAsync(RobotRef robot, CancellationToken ct = default)
        => PublishInstantActionsAsync(robot, new Vda5050InstantActions
        {
            Actions = { new VdaAction { ActionType = "emergencyStop", ActionId = Guid.NewGuid().ToString(), BlockingType = "HARD" } }
        }, ct);

    /// <summary>수동 층 변경 후 재측위 지원 [Q9] — 새 mapId + 초기 포즈 전달</summary>
    public Task InitPositionAsync(RobotRef robot, string mapId, double x, double y, double theta, CancellationToken ct = default)
        => PublishInstantActionsAsync(robot, new Vda5050InstantActions
        {
            Actions =
            {
                new VdaAction
                {
                    ActionType = "initPosition", ActionId = Guid.NewGuid().ToString(), BlockingType = "HARD",
                    ActionParameters =
                    {
                        new ActionParameter { Key = "mapId", Value = mapId },
                        new ActionParameter { Key = "x", Value = x },
                        new ActionParameter { Key = "y", Value = y },
                        new ActionParameter { Key = "theta", Value = theta },
                    }
                }
            }
        }, ct);

    private void Stamp(Vda5050Header msg, RobotRef robot, string topic)
    {
        lock (_headerIdByTopic)
        {
            _headerIdByTopic.TryGetValue(topic, out var id);
            msg.HeaderId = ++id;
            _headerIdByTopic[topic] = id;
        }
        msg.Timestamp = Vda5050Header.NowIso();   // 밀리초+Z [SPEC §3 N2]
        msg.Manufacturer = robot.Manufacturer;
        msg.SerialNumber = robot.SerialNumber;
    }

    private async Task PublishAsync<T>(string topic, T payload, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload, Json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg, ct);
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var parts = e.ApplicationMessage.Topic.Split('/');
        if (parts.Length < 5) return;
        var key = $"{parts[2]}/{parts[3]}";
        if (!_robotsByTopicKey.TryGetValue(key, out var robot)) return;

        var payload = e.ApplicationMessage.ConvertPayloadToString();
        var channel = parts[4];

        try
        {
            switch (channel)
            {
                case "state":
                    var state = JsonSerializer.Deserialize<Vda5050State>(payload);
                    if (state != null && StateReceived != null) await StateReceived(robot, state);
                    break;
                case "connection":
                    var conn = JsonSerializer.Deserialize<Vda5050Connection>(payload);
                    if (conn != null && ConnectionReceived != null) await ConnectionReceived(robot, conn);
                    break;
            }
        }
        catch (JsonException)
        {
            // TODO: 스키마 위반 메시지 알람 발행 (alarm.alarm)
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync();
        _client.Dispose();
    }
}
