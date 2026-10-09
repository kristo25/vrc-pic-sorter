using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.App.Services;

/// <summary>Manual recovery shares the scanner boundary with watcher and relocation work.</summary>
public sealed class RecoveryActions(ScanCoordinator scanner, JsonStateStore store, FileRouter router)
{
    public Task RetryAsync(CancellationToken cancellationToken = default) => scanner.RunExclusiveAsync(async () =>
    {
        await ArchiveRelocation.RecoverAsync(store, cancellationToken);
        ArchiveRelocation.EnsureResolved(await store.LoadAsync(cancellationToken));
        await router.RecoverPendingOperationsAsync(cancellationToken);
    }, cancellationToken);

    public async Task<int> DismissAsync(CancellationToken cancellationToken = default)
    {
        var dismissed = 0;
        await scanner.RunExclusiveAsync(async () =>
        {
            dismissed = await router.DismissNeedsAttentionOperationsAsync(cancellationToken);
        }, cancellationToken);
        return dismissed;
    }
}
