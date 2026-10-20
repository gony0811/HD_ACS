namespace HD.Acs.App.Services;

/// <summary>
/// HD_AMR 장치 통신 HEAVY 알람 errorType 카탈로그 [docs/HD_AMR_장치통신_HEAVY알람_사양.md §4.1].
/// 판정·래치·RESET은 AMR 로컬 — ACS는 errors[]에 있는 동안 배차 불가로 두고, 사라지면 배차를 재개한다.
/// </summary>
public static class DeviceFaults
{
    /// <summary>알람 중 Order 거부(WARNING) — 기존 orderId 유지, ACS는 거부 정차를 PENDING으로 되돌린다(사용자 결정).</summary>
    public const string OrderRejectedType = "orderRejectedDeviceFault";

    /// <summary>errorType → (device 참조값, 한글 장치명). 6종, 모두 FATAL.</summary>
    public static readonly IReadOnlyDictionary<string, (string Device, string Name)> LinkLostTypes =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["visionLinkLost"] = ("vision", "검사 비전카메라"),
            ["depthCameraLinkLost"] = ("depthCamera", "Depth 카메라"),
            ["laserSensorLinkLost"] = ("laserSensor", "3점 레이저"),
            ["ioModuleLinkLost"] = ("ioModule", "I/O 모듈"),
            ["cobotLinkLost"] = ("cobot", "코봇"),
            ["amrLinkLost"] = ("amr", "AMR"),
        };

    public static bool IsLinkLost(string? errorType) => errorType is not null && LinkLostTypes.ContainsKey(errorType);

    /// <summary>errorType의 device 참조값 (미등록이면 null).</summary>
    public static string? DeviceOf(string errorType) =>
        LinkLostTypes.TryGetValue(errorType, out var d) ? d.Device : null;

    /// <summary>errorType의 한글 장치명 (미등록이면 errorType 그대로).</summary>
    public static string NameOf(string errorType) =>
        LinkLostTypes.TryGetValue(errorType, out var d) ? d.Name : errorType;
}
