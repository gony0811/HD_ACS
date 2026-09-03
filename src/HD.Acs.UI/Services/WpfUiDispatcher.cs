using System.Windows.Threading;
using HD.Acs.UI.Abstractions;

namespace HD.Acs.UI.Services;

/// <summary>IUiDispatcher의 WPF 구현 — Application.Current.Dispatcher(없으면 현재 스레드 Dispatcher)로 마샬링.</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher =
        System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public void Post(Action action) => _dispatcher.InvokeAsync(action);
}
