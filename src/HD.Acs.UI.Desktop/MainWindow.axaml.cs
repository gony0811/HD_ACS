using Avalonia.Controls;

namespace HD.Acs.UI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // macOS는 시스템 메뉴바(NativeMenu)가 파일 메뉴를 제공하므로 창 내 Menu는 숨긴다(중복 방지).
        if (OperatingSystem.IsMacOS()) FileMenu.IsVisible = false;
    }
}
