using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Services;

public sealed class RecoveryActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingRecoveryActionsReloadAfterLiveJournalCompletion(bool cancel)
    {
        using var directory = new TestDirectory();
        using var runtime = new AppRuntime(directory.GetPath("state"), allowStartupRegistration: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new JournalEntry { Id = Guid.NewGuid(), SourcePath = directory.GetPath("already-recycled.png"), Phase = JournalPhase.NeedsAttention };
        var live = runtime.Scanner.RunExclusiveAsync(async () =>
        {
            await runtime.StateStore.UpdateAsync(s => { s.OperationJournal.Add(journal); return true; });
            entered.SetResult();
            await release.Task;
            await runtime.StateStore.UpdateAsync(s => { Assert.Single(s.OperationJournal); s.OperationJournal.Clear(); return true; });
        });
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var actions = new RecoveryActions(runtime.Scanner, runtime.StateStore, runtime.Router);
        var retry = actions.RetryAsync(cancellation.Token);
        var dismiss = actions.DismissAsync(cancellation.Token);
        try
        {
            Assert.False(retry.IsCompleted);
            Assert.False(dismiss.IsCompleted);
            Assert.Single((await runtime.StateStore.LoadAsync()).OperationJournal);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dismiss);
                Assert.Single((await runtime.StateStore.LoadAsync()).OperationJournal);
            }
        }
        finally { release.TrySetResult(); }
        await live.WaitAsync(TimeSpan.FromSeconds(10));
        if (!cancel)
        {
            await retry.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await dismiss.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await actions.RetryAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((await runtime.StateStore.LoadAsync()).OperationJournal);
    }
}
