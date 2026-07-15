namespace HD.Acs.Vda5050;

public sealed record RobotRef(string RobotId, string Manufacturer, string SerialNumber);

/// <summary>VDA 5050 MQTT 토픽: {prefix}/{majorVersion}/{manufacturer}/{serialNumber}/{channel}</summary>
public static class Vda5050Topics
{
    public const string DefaultPrefix = "uagv";
    public const string DefaultVersion = "v2";

    public static string Order(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/order";
    public static string InstantActions(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/instantActions";
    public static string State(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/state";
    public static string Connection(RobotRef r, string prefix = DefaultPrefix, string ver = DefaultVersion)
        => $"{prefix}/{ver}/{r.Manufacturer}/{r.SerialNumber}/connection";
}
