namespace HD.Acs.UI.Abstractions;

/// <summary>
/// UI 스레드 마샬링 추상화. SignalR 수신 콜백(스레드풀)을 UI 스레드로 넘겨 VM의 ObservableCollection 갱신을 안전하게 한다.
/// 구현: Avalonia=Dispatcher.UIThread.Post (헤드가 추가되면 같은 방식으로 주입한다).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>UI 스레드 큐에 비동기로 넣는다(호출자는 기다리지 않음).</summary>
    void Post(Action action);
}
