namespace HD.Acs.UI.Abstractions;

/// <summary>
/// 단순 메시지 대화상자 추상화(오류 안내·예/아니오 확인). VM이 MessageBox 등 프레임워크 API를 직접 부르지 않게 한다.
/// 파일 대화상자·새 프로젝트 팝업은 IProjectDialogService가 별도로 담당.
/// </summary>
public interface IDialogService
{
    /// <summary>오류 메시지를 모달로 표시한다.</summary>
    Task ShowErrorAsync(string message, string caption);

    /// <summary>예/아니오 확인. 사용자가 '예'를 택하면 true.</summary>
    Task<bool> ConfirmAsync(string message, string caption);
}
