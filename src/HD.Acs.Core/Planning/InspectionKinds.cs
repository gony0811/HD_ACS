namespace HD.Acs.Core.Planning;

/// <summary>
/// 검사 종류 — 선창(프로젝트) 단위 값. 같은 AMR·VDA 5050 을 쓰고 검사 장비만 다른 제품을 한 저장소에서 가른다.
/// 지금은 값을 저장·표시만 한다. 지령(액션)·결과 연동이 종류별로 갈리는 것은 다음 단계.
/// </summary>
public static class InspectionKinds
{
    public const string Weld = "WELD";   // 용접검사 (기본 — 이 값이 생기기 전 데이터는 모두 용접검사)
    public const string Gas = "GAS";     // 가스검사

    public static readonly IReadOnlyList<string> All = new[] { Weld, Gas };

    /// <summary>
    /// 입력값을 정본 표기로. null·빈값 = 지정 안 함(null 반환, true) — 호출자가 기존 값을 유지하거나 기본값을 쓴다.
    /// 모르는 값이면 false.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        kind = All.FirstOrDefault(k => string.Equals(k, raw.Trim(), StringComparison.OrdinalIgnoreCase));
        return kind is not null;
    }

    /// <summary>지령(액션) 생성이 구현된 종류인지 — 아니면 run 을 시작하지 않는다(다른 장비에 용접검사 지령이 나가는 것 방지).</summary>
    public static bool DispatchSupported(string? kind) => kind is null or Weld;
}
