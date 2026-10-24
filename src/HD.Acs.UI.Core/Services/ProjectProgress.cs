namespace HD.Acs.UI.Services;

/// <summary>
/// 프로젝트 열기/저장 진행 보고. 대형 선창(영역 1천여 개·작업 7천여 개)은 열기가 REST 수천 건이라 수 분이 걸리므로
/// 운영자가 "끝났다"고 오해해 반쯤 적재된 DB를 저장하지 않도록 단계와 건수를 화면에 보인다.
/// </summary>
/// <param name="Phase">현재 단계 이름(예: "영역·작업 등록").</param>
/// <param name="Done">이 단계에서 끝난 건수.</param>
/// <param name="Total">이 단계 전체 건수(0 = 건수 없음 — 진행바는 불확정 표시).</param>
public sealed record ProjectProgress(string Phase, int Done, int Total)
{
    /// <summary>0~100. Total이 0이면 null(불확정).</summary>
    public double? Percent => Total > 0 ? Math.Clamp(100.0 * Done / Total, 0, 100) : null;

    public override string ToString() => Total > 0 ? $"{Phase} {Done:N0}/{Total:N0}" : Phase;
}
