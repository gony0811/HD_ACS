using System.Globalization;
using System.Text.RegularExpressions;

namespace HD.Acs.Core.Planning;

/// <summary>
/// AMR `stationReachShortfall` 보고 해석 [VDA §6.4 / N23 A안]. errorDescription 형식:
/// <c>{actionId}: normalMm=800 requiredMm=1120 extraBackwardMm=340 adjustedBackwardMm=0</c>.
/// extra 는 AMR 이 <b>실제로 선 자리</b> 기준이므로 이미 물러난 양(adjusted)을 더해야 원래 정차점 기준 총량이 된다.
/// extra = −1 은 수평 후진으로 고칠 수 없는 면(바닥·천장 — 텔레스코픽 높이·정차 위치 재검토 대상).
/// </summary>
public sealed record StationReachShortfall(
    string ActionId, double NormalMm, double RequiredMm, double ExtraBackwardMm, double AdjustedBackwardMm)
{
    public const string ErrorType = "stationReachShortfall";

    /// <summary>후진으로 고칠 수 있는가.</summary>
    public bool Fixable => ExtraBackwardMm >= 0;

    /// <summary>원래 정차점 기준 필요 총 후진량 [mm]. 고칠 수 없으면 null.</summary>
    public double? TotalBackwardMm => Fixable ? ExtraBackwardMm + AdjustedBackwardMm : null;

    private static readonly Regex Kv = new(@"(\w+)=(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// 해석. actionId 는 errorReferences(<paramref name="actionIdRef"/>) 우선, 없으면 설명 앞머리 "{actionId}:".
    /// 필수 키(normalMm·requiredMm·extraBackwardMm)가 빠지면 null — 알람은 원문으로라도 남긴다(호출측).
    /// </summary>
    public static StationReachShortfall? TryParse(string? description, string? actionIdRef)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var colon = description.IndexOf(':');
        var actionId = actionIdRef ?? (colon > 0 ? description[..colon].Trim() : null);
        if (string.IsNullOrEmpty(actionId)) return null;

        var kv = Kv.Matches(colon > 0 ? description[(colon + 1)..] : description)
            .ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
        if (!kv.TryGetValue("normalMm", out var normal) || !kv.TryGetValue("requiredMm", out var required)
            || !kv.TryGetValue("extraBackwardMm", out var extra))
            return null;
        return new StationReachShortfall(actionId, normal, required, extra, kv.GetValueOrDefault("adjustedBackwardMm"));
    }
}
