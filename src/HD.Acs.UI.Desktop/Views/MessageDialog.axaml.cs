using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>메시지 창 — yesNo=true면 [예/아니오], 아니면 [확인]. ShowDialog&lt;bool&gt; 결과: 예/확인=true.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog() : this("", "", false) { }

    public MessageDialog(string message, string caption, bool yesNo)
    {
        InitializeComponent();
        Title = caption;
        MessageText.Text = message;
        YesButton.IsVisible = yesNo;
        NoButton.IsVisible = yesNo;
        OkButton.IsVisible = !yesNo;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
