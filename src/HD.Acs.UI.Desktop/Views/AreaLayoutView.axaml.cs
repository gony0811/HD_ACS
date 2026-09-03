using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// (u,v) 전개도 캔버스 — 줌(버튼·휠)·맞춤·픽 모드 클릭(좌=코너 지정, 우=해제). WPF AreaLayoutView.xaml.cs 대응.
/// 줌은 LayoutTransformControl의 ScaleTransform으로(레이아웃 크기까지 반영되어 스크롤 범위가 함께 커짐).
/// </summary>
public partial class AreaLayoutView : UserControl
{
    private const double MinScale = 0.1, MaxScale = 8.0, Step = 1.2, CanvasSize = 600.0;
    private double _scale = 1.0;
    private readonly ScaleTransform _zoom = new(1, 1);

    public AreaLayoutView()
    {
        InitializeComponent();
        ZoomHost.LayoutTransform = _zoom;
        // 휠은 ScrollViewer가 소비하기 전에 가로채야 하므로 터널링으로 등록
        Scroll.AddHandler(PointerWheelChangedEvent, Scroll_PointerWheelChanged, RoutingStrategies.Tunnel);
        // 레이아웃 완료 후 전체 맞춤
        Loaded += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Loaded);
    }

    private void SetScale(double s)
    {
        _scale = Math.Clamp(s, MinScale, MaxScale);
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        ZoomLabel.Text = $"{_scale * 100:0}%";
    }

    private void Fit()
    {
        double vw = Scroll.Viewport.Width, vh = Scroll.Viewport.Height;
        if (vw < 1 || vh < 1) return;
        SetScale(Math.Min(vw, vh) / CanvasSize * 0.98);   // 정사각 캔버스
        Scroll.Offset = new Vector(0, 0);
    }

    // 좌클릭 → (픽 모드일 때만) 면-로컬 (u,v)로 역투영해 영역 코너 순서대로 지정 / 우클릭 → 픽 모드 해제
    private void PlotCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm) return;
        var props = e.GetCurrentPoint(PlotCanvas).Properties;
        if (props.IsLeftButtonPressed)
        {
            var p = e.GetPosition(PlotCanvas);   // 스케일 전 캔버스 좌표(0..600)
            vm.CanvasClick(p.X, p.Y);            // VM 내부에서 PickMode가 아니면 무시
        }
        else if (props.IsRightButtonPressed && vm.PickMode)
        {
            vm.PickMode = false;
            e.Handled = true;   // 컨텍스트 동작 차단
        }
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetScale(_scale * Step);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetScale(_scale / Step);
    private void Fit_Click(object? sender, RoutedEventArgs e) => Fit();

    // 마우스 휠: 커서 아래 지점을 고정한 채 확대/축소
    private void Scroll_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pCanvas = e.GetPosition(PlotCanvas);   // 스케일 전 캔버스 좌표(0..600)
        SetScale(_scale * (e.Delta.Y > 0 ? Step : 1.0 / Step));
        Scroll.UpdateLayout();
        var pView = e.GetPosition(Scroll);         // 뷰포트 내 커서 위치
        Scroll.Offset = new Vector(pCanvas.X * _scale - pView.X, pCanvas.Y * _scale - pView.Y);
        e.Handled = true;
    }
}
