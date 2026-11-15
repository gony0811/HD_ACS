using System.Globalization;

namespace HD.Acs.UI.Services;

/// <summary>
/// 층의 **화면 표기** — 내부 층 번호(1부터: 영역의 level, 층 맵 ID `{선창}-L{n}`, 지령, 세이지 전송)는 그대로 두고
/// 화면에 보이는 이름만 정한다. 기본은 맨 아래층을 L0 으로 부른다(L0·L1·L2·L3).
/// 화면에 층을 적는 곳은 모두 이 클래스를 거친다 — 내부 번호로 `$"L{level}"` 을 직접 만들지 말 것.
/// 맵 ID(`CT1-L4`)·영역 이름(`L4-R01C09`)·서버 로그와 알람 문구는 내부 번호 그대로다.
/// </summary>
public static class LevelLabel
{
    /// <summary>맨 아래층(내부 층 번호 1)의 표시 번호. 0 = L0·L1·L2·L3, 1 = L1·L2·L3·L4. 설정 `Acs:LevelLabelBase`.</summary>
    public static int Base { get; set; } = 0;

    /// <summary>내부 층 번호(1부터) → 화면 표기.</summary>
    public static string Of(int level) => "L" + (level - 1 + Base).ToString(CultureInfo.InvariantCulture);

    /// <summary>층 맵 ID(`{선창}-L{n}`)의 내부 층 번호. 규약 밖이면 null.</summary>
    public static int? LevelOfMapId(string? mapId)
    {
        if (string.IsNullOrEmpty(mapId)) return null;
        int i = mapId.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(mapId.AsSpan(i + 2), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 ? n : null;
    }

    /// <summary>층 맵 ID → 화면 표기. 층을 읽을 수 없으면 맵 ID 그대로.</summary>
    public static string OfMapId(string? mapId) => LevelOfMapId(mapId) is int n ? Of(n) : mapId ?? "-";

    /// <summary>
    /// 화면 표기 + 맵 ID — `L3 (맵 CT1-L4)`. 표기와 맵 ID 의 숫자가 다를 수 있어, 로그·AMR 화면과 대조할 수 있게 둘 다 보인다.
    /// 표기가 내부 번호와 같으면(Base = 1) 맵 ID 만.
    /// </summary>
    public static string WithMapId(string? mapId) =>
        LevelOfMapId(mapId) is int n && Base != 1 ? $"{Of(n)} (맵 {mapId})" : mapId ?? "-";

    /// <summary>내부 층 키(`L1`·`L2`…) → 화면 표기. 읽을 수 없으면 그대로.</summary>
    public static string OfKey(string levelKey) =>
        levelKey.Length > 1 && levelKey[0] is 'L' or 'l' && int.TryParse(levelKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? Of(n) : levelKey;
}
