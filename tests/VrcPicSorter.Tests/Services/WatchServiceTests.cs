using System.Diagnostics;
using SixLabors.ImageSharp;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Services;

public sealed class WatchServiceTests
{
    [Fact]
    public async Task MissingFoldersAreAttachedWhenTheyBecomeAvailable()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("late-source");
        var archive = directory.GetPath("late-output", "Emoji");
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var state = await store.LoadAsync();
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        var scanner = new ScanCoordinator(
            store,
            indexer,
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        using var watcher = new WatchService(
            scanner,
            indexer,
            debounce: TimeSpan.FromMilliseconds(10),
            retryInterval: TimeSpan.FromMilliseconds(25));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.ScanCompleted += (_, _) => completed.TrySetResult();

        watcher.Start(state.Settings.CategoryMappings, state.Settings.LegacyArchiveMappings);
        Assert.True(watcher.IsRunning);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watcher.IsRunning);
    }

    [Fact]
    public async Task DeletedWatchedFolderIsReattachedAfterRecreation()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var state = await store.LoadAsync();
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        var scanner = new ScanCoordinator(
            store,
            indexer,
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        using var watcher = new WatchService(
            scanner,
            indexer,
            debounce: TimeSpan.FromMilliseconds(10),
            retryInterval: TimeSpan.FromMilliseconds(25));
        var completionCount = 0;
        watcher.ScanCompleted += (_, _) => Interlocked.Increment(ref completionCount);

        watcher.Start(state.Settings.CategoryMappings, state.Settings.LegacyArchiveMappings);
        Directory.Delete(source, recursive: true);
        await Task.Delay(250);
        var beforeRecreation = Volatile.Read(ref completionCount);

        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "change.temp"), "trigger");

        await WaitUntilAsync(
            () => Volatile.Read(ref completionCount) > beforeRecreation,
            TimeSpan.FromSeconds(5));
        Assert.True(watcher.IsRunning);
    }

    [Fact]
    public async Task ArchiveOnlyChangeRefreshesAndCleansExactDuplicates()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var state = await store.LoadAsync();
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var scanner = new ScanCoordinator(
            store,
            indexer,
            decoder,
            new FileRouter(store, decoder, recycleBin));
        using var watcher = new WatchService(
            scanner,
            indexer,
            debounce: TimeSpan.FromMilliseconds(25),
            retryInterval: TimeSpan.FromMilliseconds(25));
        var first = Path.Combine(archive, "one.gif");
        var copy = Path.Combine(archive, "two (2).gif");
        using (var image = ImageFixtureFactory.CreatePattern(191))
        {
            await image.SaveAsGifAsync(first);
        }
        await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var completed = new TaskCompletionSource<CategoryScanResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.ScanCompleted += (_, result) =>
        {
            if (result.ArchiveDuplicatesRemoved > 0)
                completed.TrySetResult(result);
        };
        watcher.Start(state.Settings.CategoryMappings, state.Settings.LegacyArchiveMappings);

        File.Copy(first, copy);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.ArchiveDuplicatesRemoved);
        Assert.Equal(copy, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task StopAsyncCancelsAnActiveWatcherScanBeforeItMovesFiles()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var state = await store.LoadAsync();
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        var scanner = new ScanCoordinator(
            store,
            indexer,
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.FromSeconds(5));
        using var watcher = new WatchService(
            scanner,
            indexer,
            debounce: TimeSpan.FromMilliseconds(10),
            retryInterval: TimeSpan.FromSeconds(10));
        var incoming = Path.Combine(source, "still-here.png");

        watcher.Start(state.Settings.CategoryMappings, state.Settings.LegacyArchiveMappings);
        using (var image = ImageFixtureFactory.CreatePattern(190))
        {
            await image.SaveAsPngAsync(incoming);
        }

        await WaitUntilAsync(
            async () => (await store.LoadAsync()).ArchiveIndex.Categories[0].Status == IndexStatus.Current,
            TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        await watcher.StopAsync();
        stopwatch.Stop();

        Assert.False(watcher.IsRunning);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(incoming));
        Assert.Empty(Directory.GetFiles(archive));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "The watcher did not resume after the folder was recreated.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(await condition(), "The expected watcher scan state was not reached.");
    }
}
