using System.Windows;
using HD.Acs.UI.Abstractions;

namespace HD.Acs.UI.Services;

/// <summary>IDialogService의 WPF 구현 — MessageBox 래핑(동기 API를 Task로 감쌈).</summary>
public sealed class WpfDialogService : IDialogService
{
    public Task ShowErrorAsync(string message, string caption)
    {
        MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message, string caption)
    {
        var r = MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(r == MessageBoxResult.Yes);
    }
}
