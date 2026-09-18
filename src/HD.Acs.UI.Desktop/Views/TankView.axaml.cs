using Avalonia.Controls;
using Avalonia.Interactivity;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

public partial class TankView : UserControl
{
    public TankView() => InitializeComponent();

    private void ZoomExtents_Click(object? sender, RoutedEventArgs e) => Tank3D.ZoomExtents();

    /// <summary>전개도 면 셀 클릭 → 그 면 크기로 드로잉 도구 창을 연다(운영 모드 → 전개도 → 면 → 독립 뷰).</summary>
    private void FaceCell_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TankViewModel.FacePlot fp } || DataContext is not TankViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;

        // 면 치수가 없으면(서버 미연결/프로젝트 미로드) 조용히 넘어가지 말고 원인을 알린다.
        if (vm.CreateFaceDrawing(fp.Code) is not { } draw)
        {
            if (owner is not null)
                _ = new MessageDialog($"면 '{fp.Code}'의 치수를 불러오지 못했습니다.\n프로젝트/선창 지오메트리를 먼저 불러온 뒤 다시 시도하세요.",
                    "드로잉 도구", yesNo: false).ShowDialog<bool>(owner);
            return;
        }

        var win = new FaceDrawWindow(draw);
        if (owner is not null) win.Show(owner); else win.Show();
        win.Activate();   // 메인 창 뒤로 열려 "안 열린 것처럼" 보이는 문제 방지 — 전면으로
    }
}
