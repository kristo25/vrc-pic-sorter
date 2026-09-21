namespace VrcPicSorter.App.Services;

/// <summary>Serializes restarts with explicit start/stop intent.</summary>
public sealed class WatcherLifecycleCoordinator(Func<Task> start, Func<Task> stop)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requested;

    public bool IsRequested => Volatile.Read(ref _requested) != 0;

    public Task StartAsync()
    {
        Volatile.Write(ref _requested, 1);
        return RunAsync(async () => { if (IsRequested) await start().ConfigureAwait(false); });
    }

    public Task StopAsync()
    {
        // Record intent before waiting: an in-flight settings restart must not undo Stop.
        Volatile.Write(ref _requested, 0);
        return RunAsync(stop);
    }

    public Task RestartAsync() => RunAsync(async () =>
    {
        if (!IsRequested) return;
        await stop().ConfigureAwait(false);
        if (IsRequested) await start().ConfigureAwait(false);
    });

    private async Task RunAsync(Func<Task> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
