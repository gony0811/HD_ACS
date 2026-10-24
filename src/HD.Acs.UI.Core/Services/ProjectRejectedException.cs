namespace HD.Acs.UI.Services;

/// <summary>
/// 서버가 프로젝트 일괄 가져오기를 검증 단계에서 거부(400) — **DB는 변경되지 않았다**.
/// (종전 단건 루프의 거부는 선창 재등록 뒤라 기존 영역이 지워진 상태였다 — 안내 문구가 달라야 한다.)
/// </summary>
public sealed class ProjectRejectedException(string message, IReadOnlyList<string> reasons) : Exception(message)
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
