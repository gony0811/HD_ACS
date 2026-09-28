using Avalonia.Controls;
using Avalonia.Interactivity;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>설정 창 — 로봇 타입 기본 정보 선택·기입·저장. DataContext = SettingsViewModel(공유 싱글턴).</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel vm) : this() => DataContext = vm;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
