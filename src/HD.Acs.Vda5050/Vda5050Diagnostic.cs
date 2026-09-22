namespace HD.Acs.Vda5050;

/// <summary>
/// 수신 경로 진단 종류. "MQTT는 붙었는데 로봇 상태가 안 보인다"의 원인을 로그만으로 가르기 위한 것 —
/// 종전에는 미구독 identity·파싱 실패가 아무 흔적 없이 폐기되어 현장에서 구분이 불가능했다.
/// </summary>
public enum Vda5050DiagnosticKind
{
    /// <summary>구독 목록에 없는 identity의 메시지 — ref.robot의 manufacturer/serial_number 불일치 의심.</summary>
    UnknownRobot,

    /// <summary>토픽 형식 불일치 — {prefix}/{ver}/{manufacturer}/{serial}/{channel} 아님.</summary>
    MalformedTopic,

    /// <summary>페이로드 역직렬화 실패 — 메시지 폐기(예: non-nullable 필드에 null, 숫자 자리에 문자열).</summary>
    PayloadInvalid,

    /// <summary>로봇·채널별 최초 수신 — 수신 경로가 살아있음을 확인하는 정상 로그.</summary>
    FirstMessage,
}

/// <summary>진단 이벤트 1건. 호스트가 ILogger로 옮겨 적는다(이 프로젝트는 로깅 패키지에 의존하지 않는다).</summary>
public sealed record Vda5050Diagnostic(
    Vda5050DiagnosticKind Kind, string Topic, string Detail, Exception? Error = null);
