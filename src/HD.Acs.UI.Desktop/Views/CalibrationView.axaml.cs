using Avalonia.Controls;
using Avalonia.Input;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

public partial class CalibrationView : UserControl
{
    public CalibrationView() => InitializeComponent();

    private void CalibrationCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not CalibrationViewModel vm) return;
        var point = e.GetCurrentPoint(CalibrationCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(CalibrationCanvas);
        vm.CanvasClick(p.X, p.Y);
        e.Handled = true;
    }
}
