using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Services;

public sealed class WatcherLifecycleCoordinatorTests
{
    [Fact]
    public async Task StopDuringSettingsRestartPreventsReactivation()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var stops = 0;
        var running = false;
        var coordinator = new WatcherLifecycleCoordinator(
            () => { running = true; starts++; return Task.CompletedTask; },
            async () =>
            {
                running = false;
                if (++stops == 1)
                {
                    stopping.TrySetResult();
                    await release.Task;
                }
            });
        await coordinator.StartAsync();
        var restart = coordinator.RestartAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running);
        Assert.True(coordinator.IsRequested); // The temporary restart gap still means "Stop watching".
        var stop = coordinator.StopAsync();
        Assert.False(coordinator.IsRequested);
        release.SetResult();
        await Task.WhenAll(restart, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task NewStartAfterStopWaitsForTheOldStopToComplete()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = false;
        var coordinator = new WatcherLifecycleCoordinator(
            () => { running = true; return Task.CompletedTask; },
            async () => { stopping.SetResult(); await release.Task; running = false; });
        await coordinator.StartAsync();
        var stop = coordinator.StopAsync();
        await stopping.Task;
        var start = coordinator.StartAsync();
        release.SetResult();
        await Task.WhenAll(stop, start).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(running);
        Assert.True(coordinator.IsRequested);
    }
}
