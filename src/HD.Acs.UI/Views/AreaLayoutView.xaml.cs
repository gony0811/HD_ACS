using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HD.Acs.UI.Views;

public partial class AreaLayoutView : UserControl
{
    private const double MinScale = 0.1, MaxScale = 8.0, Step = 1.2, CanvasSize = 600.0;
    private double _scale = 1.0;

    public AreaLayoutView()
    {
        InitializeComponent();
        // 레이아웃 완료 후 전체 맞춤
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Fit), DispatcherPriority.Loaded);
    }

    private void SetScale(double s)
    {
        _scale = Math.Clamp(s, MinScale, MaxScale);
        Zoom.ScaleX = Zoom.ScaleY = _scale;
        ZoomLabel.Text = $"{_scale * 100:0}%";
    }

    private void Fit()
    {
        double vw = Scroll.ViewportWidth, vh = Scroll.ViewportHeight;
        if (vw < 1 || vh < 1) return;
        SetScale(Math.Min(vw, vh) / CanvasSize * 0.98);   // 정사각 캔버스
        Scroll.ScrollToHorizontalOffset(0);
        Scroll.ScrollToVerticalOffset(0);
    }

    // 캔버스 클릭 → 면-로컬 (u,v)로 역투영해 영역 코너 순서대로 지정
    private void PlotCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.AreaPlanningViewModel vm)
        {
            var p = e.GetPosition(PlotCanvas);   // 스케일 전 캔버스 좌표(0..600)
            vm.CanvasClick(p.X, p.Y);
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetScale(_scale * Step);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetScale(_scale / Step);
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    // 마우스 휠: 커서 아래 지점을 고정한 채 확대/축소
    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pCanvas = e.GetPosition(PlotCanvas);   // 스케일 전 캔버스 좌표(0..600)
        SetScale(_scale * (e.Delta > 0 ? Step : 1.0 / Step));
        Scroll.UpdateLayout();
        var pView = e.GetPosition(Scroll);         // 뷰포트 내 커서 위치
        Scroll.ScrollToHorizontalOffset(pCanvas.X * _scale - pView.X);
        Scroll.ScrollToVerticalOffset(pCanvas.Y * _scale - pView.Y);
        e.Handled = true;
    }
}
