using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Integration;

/// <summary>
/// 선창 면 식별자 [SAIGE 연동 사양서 v2.6 §2.3 / 부록 A.1]. 값 체계 1~10 고정, 0 = 예약(미지정).
/// HD_ACS 내부/DB는 문자 코드(wall_code)를 유지하고 대외 JSON에서만 정수 wallId를 병기한다.
/// </summary>
public enum WallId : byte
{
    Unspecified = 0, Bottom = 1, Top = 2, PortMid = 3, StbdMid = 4,
    Forward = 5, Aft = 6, PortLower = 7, StbdLower = 8,
    PortUpper = 9, StbdUpper = 10
}

/// <summary>표면 형상 [부록 A.2] — 촬영 지점 단위 속성(면 단위 아님). ACS는 생성하지 않으며 정의만 공유한다.</summary>
public enum SurfaceType : byte { Flat = 0, Corner = 1, Corrugation = 2 }

/// <summary>wall_code(문자) ↔ wallId(정수) 매핑 [부록 A.1 정본].</summary>
public static class WallIds
{
    private static readonly Dictionary<string, WallId> ByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = WallId.Bottom, ["T"] = WallId.Top, ["PM"] = WallId.PortMid, ["SM"] = WallId.StbdMid,
        ["F"] = WallId.Forward, ["A"] = WallId.Aft, ["PL"] = WallId.PortLower, ["SL"] = WallId.StbdLower,
        ["PU"] = WallId.PortUpper, ["SU"] = WallId.StbdUpper,
    };

    /// <summary>wall_code → wallId 정수. 미등록 코드는 0(예약 — 미지정).</summary>
    public static int FromCode(string? wallCode) =>
        wallCode is not null && ByCode.TryGetValue(wallCode, out var id) ? (int)id : 0;
}

/// <summary>대외 단위 환산 [부록 A.4] — HD_ACS 내부 m(실수) → SAIGE 연동 mm(정수).</summary>
public static class SaigeUnits
{
    public static int ToMm(double meters) => (int)Math.Round(meters * 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>rad → deg, 소수 1자리, 0.0~359.9 [§9.1]. 반올림으로 360.0이 되면 0.0으로 접는다.</summary>
    public static double ToYawDeg(double rad)
    {
        double deg = rad * 180.0 / Math.PI % 360.0;
        if (deg < 0) deg += 360.0;
        deg = Math.Round(deg, 1, MidpointRounding.AwayFromZero);
        return deg >= 360.0 ? 0.0 : deg;
    }

    /// <summary>mapId "{tank}-L{n}" → 층 번호 n (1-based [§2.6]). 규약 밖이면 null.</summary>
    public static int? LevelFromMapId(string? mapId)
    {
        if (string.IsNullOrEmpty(mapId)) return null;
        int i = mapId.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(mapId.AsSpan(i + 2), out var n) && n >= 1 ? n : null;
    }
}

/// <summary>로봇 상태 4종 [§7.4].</summary>
public enum RobotHealthStatus { WORKING, IDLE, ERROR, DISCONNECTED }

/// <summary>로봇 상태 판정 입력 — 전부 ACS가 이미 보유한 값(VDA connection/state + run 상태).</summary>
public sealed record RobotHealthInput(
    string? ConnectionState,   // VDA connection: ONLINE | OFFLINE | CONNECTIONBROKEN | null(미수신)
    bool HasReportedErrors,    // state.errors 존재
    bool RunAborted,           // 로봇의 최근 run이 중단(ABORTED)된 채 잔여 작업 보유
    bool HasDispatchedWork);   // 발행되어 수행 중인 정차(work_item DISPATCHED) 존재

public static class RobotHealth
{
    /// <summary>
    /// 상태 판정 [§7.4]. 우선순위: 두절 > 오류 > 작업 중 > 대기.
    /// 작업 중 = 연결 정상 + 수행 중 작업 존재 — 발행된 정차는 주행 또는 검사 수행 중이므로 driving 플래그는 보지 않는다.
    /// </summary>
    public static RobotHealthStatus Derive(RobotHealthInput i)
    {
        if (!string.Equals(i.ConnectionState, "ONLINE", StringComparison.OrdinalIgnoreCase))
            return RobotHealthStatus.DISCONNECTED;
        if (i.HasReportedErrors || i.RunAborted) return RobotHealthStatus.ERROR;
        if (i.HasDispatchedWork) return RobotHealthStatus.WORKING;
        return RobotHealthStatus.IDLE;
    }

    /// <summary>
    /// 맵 프레임 보고 위치 → 도면 좌표 페이로드 [§9.1/§9.2]. x·y mm 정수, yaw deg 소수 1자리.
    /// theta 미보고면 yaw 0.0.
    /// </summary>
    public static (int XMm, int YMm, double YawDeg) ToDrawingPosition(
        DrawingTransform tWd, double mapX, double mapY, double? mapTheta)
    {
        var (dx, dy) = tWd.MapToDrawing(mapX, mapY);
        double yaw = mapTheta is double th ? SaigeUnits.ToYawDeg(tWd.MapYawToDrawing(th)) : 0.0;
        return (SaigeUnits.ToMm(dx), SaigeUnits.ToMm(dy), yaw);
    }
}

/// <summary>
/// TASK 최종 결과 판정 [§6.3/§7.3] — 고유 TASK 기준, 재시도는 중복 계산하지 않는다.
/// 재시도 단위는 정차(work_item=Area)이므로 "재시도 여유"는 소속 work_item 상태로 판단한다.
/// </summary>
public static class TaskOutcome
{
    public const string Success = "SUCCESS", Failed = "FAILED", Skipped = "SKIPPED";

    /// <param name="actionStatuses">이 TASK에 발행된 모든 액션(시도)의 상태.</param>
    /// <param name="workItemStatus">소속 work_item 상태 (PENDING|DISPATCHED|DONE|FAILED|SKIPPED).</param>
    /// <returns>종결이면 SUCCESS/FAILED/SKIPPED, 미종결이면 null.</returns>
    public static string? Classify(IEnumerable<string> actionStatuses, string workItemStatus)
    {
        bool anyFinished = false, anyFailed = false;
        foreach (var s in actionStatuses)
        {
            if (s == "FINISHED") anyFinished = true;
            else if (s == "FAILED") anyFailed = true;
        }
        if (anyFinished) return Success;                       // 시도 중 성공이 하나라도 있으면 성공
        bool exhausted = workItemStatus is "SKIPPED" or "FAILED";
        if (!exhausted) return null;                           // 재시도 여유 있음(또는 미발행) → 미종결
        return anyFailed ? Failed : Skipped;                   // 소진: 실패 이력 있으면 FAILED, 시도 기록조차 없으면 SKIPPED
    }
}

/// <summary>
/// TASK 시도 번호(attempt) 발급 규칙 [VDA §8.1 / SAIGE §2.5 / 비전 v3.2 §3.3] — ACS 발급.
/// taskId별 누적(1부터, run과 무관) → (taskId, attempt, captureSeq)가 재검사 run에서도 유일하다.
/// 로봇↔비전 프레임에서 UInt8 이므로 255에서 포화(saturate)한다 — 0으로 되감기면 "최신 시도 = 최댓값" 판정이 깨진다.
/// </summary>
public static class TaskAttempt
{
    public const int Max = byte.MaxValue;

    /// <param name="alreadyIssued">이 TASK에 지금까지 발행된 액션(시도) 수.</param>
    public static int Next(int alreadyIssued) => Math.Min(Max, Math.Max(0, alreadyIssued) + 1);
}
