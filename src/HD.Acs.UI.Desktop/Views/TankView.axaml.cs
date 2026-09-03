using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HD.Acs.UI.Desktop.Views;

public partial class TankView : UserControl
{
    public TankView() => InitializeComponent();

    private void ZoomExtents_Click(object? sender, RoutedEventArgs e) => Tank3D.ZoomExtents();
}
