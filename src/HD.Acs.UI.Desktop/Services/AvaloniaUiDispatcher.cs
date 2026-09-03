using Avalonia.Threading;
using HD.Acs.UI.Abstractions;

namespace HD.Acs.UI.Desktop.Services;

/// <summary>IUiDispatcher의 Avalonia 구현 — UI 스레드 디스패처로 마샬링.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
