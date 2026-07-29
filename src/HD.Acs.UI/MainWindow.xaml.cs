using System.Windows;

namespace HD.Acs.UI;

/// <summary>
/// 셸 윈도우. 실시간 연결·명령 로직은 모두 ViewModel(ShellViewModel) + Services(MonitoringClient/AcsApiClient)로 이관되어
/// code-behind는 InitializeComponent만 유지한다. DataContext(ShellViewModel)는 App 부트스트랩에서 주입한다.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
