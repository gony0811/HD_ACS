namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 셸 최상위 작업 모드. 계획(시나리오·영역·작업 정의) / 운영(미션 실행·모니터링) / 이력(리포트)을
/// 분리해 사고 모드가 섞이지 않게 한다. 상단 모드 탭으로 전환한다.
/// </summary>
public enum AppMode
{
    Operation,
    Planning,
    History,
}
