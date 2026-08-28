using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Views;

/// <summary>운영 모드 — 좌(로봇·층 진행·수동 층) · 중앙(3D/전개도 공간 히어로 + 미션 컨트롤) · 우(알람 피드).</summary>
public partial class OperationView : UserControl
{
    public OperationView() => InitializeComponent();

    /// <summary>등록 요소 목록 항목에 마우스 진입 → 평면도의 해당 오브젝트 노랑 하이라이트.</summary>
    private void OnElementRowEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TankViewModel.PlanElementRow row
            && DataContext is ShellViewModel shell)
            shell.Tank.SetHighlight(row.Id);
    }

    private void OnElementRowLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is ShellViewModel shell) shell.Tank.SetHighlight(null);
    }
}
