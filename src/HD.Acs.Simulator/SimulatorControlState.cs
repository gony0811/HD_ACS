using System.Collections.Concurrent;

internal sealed record SimulatorStateUpdate(
    string MapId, double BatteryPct, double X, double Y, double Theta);

internal sealed record SimulatorStateSnapshot(
    string MapId, double BatteryPct, double X, double Y, double Theta, string? Timestamp);

/// <summary>UI 스레드와 MQTT 시뮬레이터 루프 사이의 단방향 명령 큐.</summary>
internal sealed class SimulatorControlState
{
    private readonly ConcurrentQueue<SimulatorStateUpdate> _updates = new();
    private readonly SemaphoreSlim _updateSignal = new(0);
    private readonly CancellationTokenSource _stop = new();

    public SimulatorControlState(string mapId, double batteryPct, double x, double y, double theta)
        => Initial = new SimulatorStateSnapshot(mapId, batteryPct, x, y, theta, null);

    public SimulatorStateSnapshot Initial { get; }
    public CancellationToken CancellationToken => _stop.Token;
    public event EventHandler<SimulatorStateSnapshot>? StatePublished;

    public void RequestUpdate(SimulatorStateUpdate update)
    {
        _updates.Enqueue(update);
        _updateSignal.Release();
    }

    public bool TryTakeUpdate(out SimulatorStateUpdate update) => _updates.TryDequeue(out update!);

    public async Task WaitForUpdateAsync(TimeSpan timeout)
    {
        try { await _updateSignal.WaitAsync(timeout, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void ReportPublished(SimulatorStateSnapshot snapshot)
        => StatePublished?.Invoke(this, snapshot);

    public void Stop()
    {
        if (!_stop.IsCancellationRequested)
            _stop.Cancel();
    }
}
