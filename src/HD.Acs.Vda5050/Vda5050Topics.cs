namespace HD.Acs.Vda5050;

public sealed record RobotRef(string RobotId, string Manufacturer, string SerialNumber);

/// <summary>VDA 5050 MQTT 토픽: {prefix}/{majorVersion}/{manufacturer}/{serialNumber}/{channel}</summary>
public static class Vda5050Topics
{
    public const string DefaultPrefix = "uagv";
    public const string DefaultVersion = "v2";
    public static readonly RobotRef AcsIdentity = new("HD_ACS", "HD_ACS", "hd-acs-master");

    public static string Order(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/order";
    public static string InstantActions(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/instantActions";
    public static string State(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/state";
    public static string Connection(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/connection";

    /// <summary>
    /// 진단용 — 모든 identity의 connection 토픽. 구독 목록(ref.robot)에 없는 로봇이 브로커에
    /// 접속해 있으면 드러난다. connection은 retain이므로 구독 즉시 현재 접속 상태가 도착한다.
    /// state(2Hz)는 트래픽·중복 전달 때문에 와일드카드로 받지 않는다.
    /// </summary>
    public static string AnyConnection(string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/+/+/connection";

    /// <summary>ACS 프로세스 생존 신호 [VDA5050_INTERFACE_SPEC §7.2, N12].</summary>
    public static string AcsConnection(string prefix = DefaultPrefix, string ver = DefaultVersion)
        => Connection(AcsIdentity, prefix, ver);
}
