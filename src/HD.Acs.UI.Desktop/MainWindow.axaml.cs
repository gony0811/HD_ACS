using Avalonia.Controls;
using Avalonia.Interactivity;
using HD.Acs.UI.Desktop.Views;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop;

public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        // 창 내 파일 메뉴는 전 플랫폼에서 항상 표시한다(WPF 헤드와 동일 위치·운영자 습관 유지).
        // macOS의 시스템 메뉴바(NativeMenu, ⌘ 단축키)는 보조 경로 — 과거 mac에서 창 내 Menu를 숨겼다가
        // "파일 메뉴가 사라졌다"는 혼란이 있어 되돌림. 두 메뉴는 같은 명령에 바인딩되어 있다.
    }

    /// <summary>설정 ▸ 로봇 정보 — 별도의 창(SettingsWindow)으로 이동. 이미 열려 있으면 앞으로 가져온다.</summary>
    private void RobotSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

        if (_settingsWindow is { } w && w.IsVisible)
        {
            w.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(shell.Settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
        _settingsWindow.Activate();
    }
}
