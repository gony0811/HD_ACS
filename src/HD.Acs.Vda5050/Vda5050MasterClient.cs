using System.Text.Json;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, RobotRef> _robotsByTopicKey = new();
    private readonly Dictionary<string, int> _headerIdByTopic = new();   // 토픽별 단조 증가 [SPEC §3 N1]
    private readonly object _disconnectSync = new();
    private TaskCompletionSource _disconnected = NewDisconnectSignal();

    // 진단 로그 범람 방지 — 같은 (사유, 토픽)은 이 간격 안에서 1회만 보고한다(state는 2Hz).
    private static readonly TimeSpan DiagnosticThrottle = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDiagnosticAt = new();
    private readonly ConcurrentDictionary<string, byte> _firstMessageSeen = new();

    public event Func<RobotRef, Vda5050State, Task>? StateReceived;
    public event Func<RobotRef, Vda5050Connection, Task>? ConnectionReceived;

    /// <summary>
    /// 수신 경로 진단(미구독 identity·토픽 형식·파싱 실패·최초 수신).
    /// 이 프로젝트는 로깅 패키지에 의존하지 않으므로 호스트(VdaBridgeService)가 ILogger로 연결한다.
    /// </summary>
    public event Action<Vda5050Diagnostic>? DiagnosticRaised;

    public Vda5050MasterClient(string host, int port = 1883)
    {
        _host = host;
        _port = port;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += _ =>
        {
            lock (_disconnectSync)
                _disconnected.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        lock (_disconnectSync)
            _disconnected = NewDisconnectSignal();

        // 세션마다 진단 상태를 초기화 — 재접속 후에도 최초 수신이 다시 기록되어 수신 복구가 로그로 확인된다.
        _firstMessageSeen.Clear();
        _lastDiagnosticAt.Clear();

        var will = CreateAcsConnection("CONNECTIONBROKEN");
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_host, _port)
            .WithClientId("hd-acs-master")
            .WithCleanSession()
            .WithWillTopic(Vda5050Topics.AcsConnection())
            .WithWillPayload(JsonSerializer.Serialize(will, Json))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain()
            .Build();
        await _client.ConnectAsync(options, ct);
        await PublishAcsConnectionAsync("ONLINE", ct);
    }

    /// <summary>현재 MQTT 연결이 끊길 때까지 대기 — 브리지의 런타임 재접속 루프에서 사용.</summary>
    public Task WaitForDisconnectAsync(CancellationToken ct = default)
    {
        Task signal;
        lock (_disconnectSync)
            signal = _disconnected.Task;
        return signal.WaitAsync(ct);
    }

    /// <summary>로봇 등록: state/connection 토픽 구독 (fleet-ready — 로봇별 호출 [ADR-003])</summary>
    public async Task RegisterRobotAsync(RobotRef robot, CancellationToken ct = default)
    {
        _robotsByTopicKey[$"{robot.Manufacturer}/{robot.SerialNumber}"] = robot;
        await _client.SubscribeAsync(Vda5050Topics.State(robot), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await _client.SubscribeAsync(Vda5050Topics.Connection(robot), MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    /// <summary>
    /// 진단용 와일드카드 구독 — ref.robot에 없는 identity로 접속한 로봇을 드러낸다.
    /// 개별 로봇은 정확한 토픽만 구독하므로, identity가 어긋나면 종전에는 아무것도 수신되지 않아
    /// "브로커는 붙었는데 상태가 없다"의 원인 파악이 불가능했다.
    /// connection만 구독한다(retain·저빈도) — state(2Hz)는 중복 전달 위험이 있어 제외.
    /// 반드시 RegisterRobotAsync 뒤에 호출할 것: 등록 전이면 정상 로봇의 retained connection이
    /// 미구독으로 오인 보고된다.
    /// </summary>
    public Task SubscribeUnknownRobotDiscoveryAsync(CancellationToken ct = default)
        => _client.SubscribeAsync(Vda5050Topics.AnyConnection(), MqttQualityOfServiceLevel.AtMostOnce, ct);

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

    /// <summary>ACS 생존 상태 발행. ONLINE/OFFLINE은 항상 retained QoS 1 [SPEC §7.2, N12].</summary>
    public Task PublishAcsConnectionAsync(string connectionState, CancellationToken ct = default)
        => PublishAsync(Vda5050Topics.AcsConnection(), CreateAcsConnection(connectionState), ct, retain: true);

    /// <summary>비상정지 — 기능적 정지이며 안전 규격 정지가 아님 [ADR-007]</summary>
    public Task EmergencyStopAsync(RobotRef robot, CancellationToken ct = default)
        => PublishInstantActionsAsync(robot, new Vda5050InstantActions
        {
            Actions = { new VdaAction { ActionType = "emergencyStop", ActionId = Guid.NewGuid().ToString(), BlockingType = "HARD" } }
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

    private Vda5050Connection CreateAcsConnection(string connectionState)
    {
        var message = new Vda5050Connection { ConnectionState = connectionState };
        Stamp(message, Vda5050Topics.AcsIdentity, Vda5050Topics.AcsConnection());
        return message;
    }

    private async Task PublishAsync<T>(string topic, T payload, CancellationToken ct, bool retain = false)
    {
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload, Json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (retain) builder.WithRetainFlag();
        var msg = builder.Build();
        await _client.PublishAsync(msg, ct);
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var parts = topic.Split('/');
        if (parts.Length < 5)
        {
            Report(Vda5050DiagnosticKind.MalformedTopic, topic,
                "토픽 요소가 5개 미만 — {prefix}/{majorVersion}/{manufacturer}/{serialNumber}/{channel} 형식이어야 한다");
            return;
        }

        var key = $"{parts[2]}/{parts[3]}";
        var channel = parts[4];
        if (!_robotsByTopicKey.TryGetValue(key, out var robot))
        {
            // ACS 자신의 생존 신호는 와일드카드 구독에 걸리는 정상 메시지다.
            if (key != $"{Vda5050Topics.AcsIdentity.Manufacturer}/{Vda5050Topics.AcsIdentity.SerialNumber}")
                Report(Vda5050DiagnosticKind.UnknownRobot, topic,
                    $"구독하지 않은 identity '{key}'가 발행 중 — 구독 중: [{string.Join(", ", _robotsByTopicKey.Keys)}]. "
                    + "ref.robot의 manufacturer/serial_number가 로봇 발행 토픽과 일치하는지, is_active=true인지 확인 필요");
            return;
        }

        var payload = e.ApplicationMessage.ConvertPayloadToString();

        try
        {
            switch (channel)
            {
                case "state":
                    var state = JsonSerializer.Deserialize<Vda5050State>(payload);
                    if (state == null) break;
                    ReportFirstMessage(key, channel, topic, Describe(state));
                    if (StateReceived != null) await StateReceived(robot, state);
                    break;
                case "connection":
                    var conn = JsonSerializer.Deserialize<Vda5050Connection>(payload);
                    if (conn == null) break;
                    ReportFirstMessage(key, channel, topic, $"connectionState='{conn.ConnectionState}'");
                    if (ConnectionReceived != null) await ConnectionReceived(robot, conn);
                    break;
            }
        }
        catch (JsonException ex)
        {
            // 종전에는 무음 폐기였다 — 형식 위반 state가 전량 유실되어도 흔적이 없었다.
            Report(Vda5050DiagnosticKind.PayloadInvalid, topic,
                $"역직렬화 실패로 메시지 폐기 — 페이로드: {Head(payload)}", ex);
            // TODO: 스키마 위반 메시지 알람 발행 (alarm.alarm)
        }
    }

    /// <summary>최초 수신 1회만 — 수신 경로가 실제로 살아있는지, 어떤 필드가 비어 오는지 확인용.</summary>
    private void ReportFirstMessage(string key, string channel, string topic, string detail)
    {
        if (_firstMessageSeen.TryAdd($"{channel}|{key}", 0))
            DiagnosticRaised?.Invoke(new Vda5050Diagnostic(
                Vda5050DiagnosticKind.FirstMessage, topic, $"{channel} 최초 수신 — {detail}"));
    }

    /// <summary>UI 표시에 직결되는 필드의 유무를 한 줄로 — 위치가 '-'로 남는 원인을 즉시 가른다.</summary>
    private static string Describe(Vda5050State state)
    {
        var pos = state.AgvPosition is { } p
            ? $"mapId='{p.MapId}' x={p.X:F2} y={p.Y:F2} theta={p.Theta:F3} initialized={p.PositionInitialized}"
            : "없음 (UI 위치가 '-'로 남는다)";
        var battery = state.BatteryState is { } b ? $"{b.BatteryCharge:F0}%" : "없음";
        return $"orderId='{state.OrderId}' agvPosition={pos}, battery={battery}, errors={state.Errors.Count}";
    }

    private void Report(Vda5050DiagnosticKind kind, string topic, string detail, Exception? error = null)
    {
        var throttleKey = $"{kind}|{topic}";
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDiagnosticAt.GetOrAdd(throttleKey, DateTimeOffset.MinValue) < DiagnosticThrottle) return;
        _lastDiagnosticAt[throttleKey] = now;
        DiagnosticRaised?.Invoke(new Vda5050Diagnostic(kind, topic, detail, error));
    }

    private static string Head(string payload, int max = 400)
    {
        var flat = payload.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…(이하 생략)";
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
        {
            try
            {
                await PublishAcsConnectionAsync("OFFLINE", ct);
            }
            finally
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync(cancellationToken: ct);
            }
        }
    }

    private static TaskCompletionSource NewDisconnectSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client.Dispose();
    }
}
