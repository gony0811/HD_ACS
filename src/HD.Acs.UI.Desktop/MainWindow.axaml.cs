using Avalonia.Controls;

namespace HD.Acs.UI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 창 내 파일 메뉴는 전 플랫폼에서 항상 표시한다(WPF 헤드와 동일 위치·운영자 습관 유지).
        // macOS의 시스템 메뉴바(NativeMenu, ⌘ 단축키)는 보조 경로 — 과거 mac에서 창 내 Menu를 숨겼다가
        // "파일 메뉴가 사라졌다"는 혼란이 있어 되돌림. 두 메뉴는 같은 명령에 바인딩되어 있다.
    }
}
