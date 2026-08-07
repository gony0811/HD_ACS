namespace HD.Acs.UI.Services;

/// <summary>
/// 프로젝트 관련 View 상호작용(새 프로젝트 팝업 · 파일 열기/저장 대화상자)을 VM에서 분리하는 UI 서비스.
/// 구현은 WPF(Window · Microsoft.Win32 대화상자)에 의존하며, ShellViewModel은 이 인터페이스만 사용한다.
/// </summary>
public interface IProjectDialogService
{
    /// <summary>새 프로젝트 팝업(선창 3D 정의 폼)을 모달로 표시. 확인(선창 등록 성공) 시 true.</summary>
    bool ShowNewProject();

    /// <summary>저장 위치 선택 대화상자. 취소 시 null.</summary>
    string? PickSavePath(string? suggestedFileName = null);

    /// <summary>열기 파일 선택 대화상자. 취소 시 null.</summary>
    string? PickOpenPath();
}
