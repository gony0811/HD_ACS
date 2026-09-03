using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using HD.Acs.UI.Abstractions;
using HD.Acs.UI.Desktop.Views;

namespace HD.Acs.UI.Desktop.Services;

/// <summary>IDialogService의 Avalonia 구현 — 자체 MessageDialog 창(확인 / 예·아니오)을 메인 창 모달로 띄운다.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    internal static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task ShowErrorAsync(string message, string caption)
    {
        var owner = Owner;
        if (owner is null) return;   // 헤드리스/부트 이전 — 표시할 창 없음
        await new MessageDialog(message, caption, yesNo: false).ShowDialog<bool>(owner);
    }

    public async Task<bool> ConfirmAsync(string message, string caption)
    {
        var owner = Owner;
        if (owner is null) return false;   // 창이 없으면 안전 쪽(취소)
        return await new MessageDialog(message, caption, yesNo: true).ShowDialog<bool>(owner);
    }
}
