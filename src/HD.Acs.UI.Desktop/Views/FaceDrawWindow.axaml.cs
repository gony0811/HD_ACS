using Avalonia.Controls;
using Avalonia.Interactivity;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 면 드로잉 도구 창 — 전개도에서 면을 클릭하면 그 면 크기(mm)로 열린다. DataContext=FaceDrawingViewModel.
/// 툴바(타입/모드/간격/편집/되돌리기/레이어)+캔버스(FaceDrawCanvas)+HUD/직렬화.
/// </summary>
public partial class FaceDrawWindow : Window
{
    private FaceDrawingViewModel? _vm;

    public FaceDrawWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as FaceDrawingViewModel);
    }

    public FaceDrawWindow(FaceDrawingViewModel vm) : this() => DataContext = vm;

    private void Bind(FaceDrawingViewModel? vm)
    {
        if (_vm is not null) _vm.ShapesChanged -= OnShapes;
        _vm = vm;
        if (_vm is not null)
        {
            _vm.ShapesChanged += OnShapes;
            Title = $"면 드로잉 — {_vm.FaceCode} ({_vm.Face.W:0} × {_vm.Face.H:0} mm)";
        }
        RefreshJson();
    }

    private void OnShapes(object? sender, System.EventArgs e) => RefreshJson();

    private void Fit_Click(object? sender, RoutedEventArgs e) => Canvas.ZoomToFit();

    private void JsonToggle_Click(object? sender, RoutedEventArgs e)
    {
        JsonPanel.IsVisible = JsonToggle.IsChecked == true;
        if (JsonPanel.IsVisible) RefreshJson();
    }

    private void RefreshJson()
    {
        if (JsonPanel?.IsVisible == true && _vm is not null) JsonText.Text = _vm.SerializeJson();
    }
}
