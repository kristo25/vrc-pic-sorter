using SixLabors.ImageSharp;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class ArchiveDuplicateRefreshTests
{
    [Fact]
    public async Task RefreshFindsDiskOnlyGifsInDisabledArchiveAndDropsRemovedCopies()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var root = directory.GetPath("archive");
        var first = Path.Combine(root, "one.gif");
        var copy = Path.Combine(root, "two.gif");
        using (var image = ImageFixtureFactory.CreatePattern(707))
        {
            await image.SaveAsGifAsync(first);
            await image.SaveAsGifAsync(copy);
        }
        var coordinator = CreateCoordinator(store);
        var snapshot = await coordinator.RefreshArchiveDuplicatesAsync();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(ArchiveDuplicateKind.Identical, Assert.Single(snapshot.Groups).Kind);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(copy));

        File.Delete(copy);
        snapshot = await coordinator.RefreshArchiveDuplicatesAsync();
        Assert.True(snapshot.IsComplete);
        Assert.Empty(snapshot.Groups);
        Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories.Single(c => c.Category == VrcImageCategory.Emoji).Images);
    }

    [Fact]
    public async Task MissingRootExcludesItsPreviouslyIndexedDuplicates()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        using (var image = ImageFixtureFactory.CreatePattern(708))
        {
            await image.SaveAsGifAsync(directory.GetPath("archive", "one.gif"));
            await image.SaveAsGifAsync(directory.GetPath("archive", "two.gif"));
        }
        var coordinator = CreateCoordinator(store);
        Assert.Single((await coordinator.RefreshArchiveDuplicatesAsync()).Groups);
        Directory.Move(directory.GetPath("archive"), directory.GetPath("offline"));
        var snapshot = await coordinator.RefreshArchiveDuplicatesAsync();
        Assert.False(snapshot.IsComplete);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Empty(snapshot.Groups);
    }

    [Fact]
    public async Task LegacyRootsAreRefreshedAndMissingLegacyRootIsReported()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var legacy = directory.GetPath("legacy");
        Directory.CreateDirectory(legacy);
        await store.UpdateAsync(state =>
        {
            state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
            { Category = VrcImageCategory.Emoji, ArchivePath = legacy });
            return true;
        });
        using (var image = ImageFixtureFactory.CreatePattern(709))
        {
            await image.SaveAsGifAsync(directory.GetPath("archive", "one.gif"));
            await image.SaveAsGifAsync(Path.Combine(legacy, "two.gif"));
        }
        var coordinator = CreateCoordinator(store);
        Assert.Single((await coordinator.RefreshArchiveDuplicatesAsync()).Groups);
        Directory.Move(legacy, directory.GetPath("offline"));
        var snapshot = await coordinator.RefreshArchiveDuplicatesAsync();
        Assert.False(snapshot.IsComplete);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains(legacy, StringComparison.Ordinal));
        Assert.Empty(snapshot.Groups);
    }

    [Fact]
    public async Task CorruptGifReportsIncompleteRefreshInsteadOfClaimingNoDuplicates()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var corrupt = directory.GetPath("archive", "broken.gif");
        await File.WriteAllTextAsync(corrupt, "not a gif");
        var snapshot = await CreateCoordinator(store).RefreshArchiveDuplicatesAsync();
        Assert.False(snapshot.IsComplete);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("broken.gif", StringComparison.Ordinal));
        Assert.Empty(snapshot.Groups);
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotPreventTheNextRefresh()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var coordinator = CreateCoordinator(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RefreshArchiveDuplicatesAsync(cancellation.Token));
        var snapshot = await coordinator.RefreshArchiveDuplicatesAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(snapshot.IsComplete);
    }

    [Fact]
    public async Task CleanupAutomaticallyRecyclesProvenArchiveDuplicates()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var root = directory.GetPath("archive");
        var first = Path.Combine(root, "one.gif");
        var copy = Path.Combine(root, "two (2).gif");
        using (var image = ImageFixtureFactory.CreatePattern(710))
        {
            await image.SaveAsGifAsync(first);
            await image.SaveAsGifAsync(copy);
        }
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = CreateCoordinator(store, recycleBin);

        var result = await coordinator.CleanArchiveDuplicatesAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Removed);
        Assert.Equal(copy, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(first));
        Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories
            .Single(category => category.Category == VrcImageCategory.Emoji).Images);
    }

    [Fact]
    public async Task CleanupRefreshesAnIndexMarkedCurrentBeforeRemovingNewDuplicates()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var root = directory.GetPath("archive");
        var first = Path.Combine(root, "one.gif");
        var copy = Path.Combine(root, "two (2).gif");
        using (var image = ImageFixtureFactory.CreatePattern(715))
        {
            await image.SaveAsGifAsync(first);
        }
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = CreateCoordinator(store, recycleBin);
        await coordinator.RefreshArchiveDuplicatesAsync();
        Assert.Equal(IndexStatus.Current, (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(category => category.Category == VrcImageCategory.Emoji).Status);
        File.Copy(first, copy);

        var result = await coordinator.CleanArchiveDuplicatesAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Removed);
        Assert.Equal(copy, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task CleanupRemovesNothingWhenArchiveCoverageIsIncomplete()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var root = directory.GetPath("archive");
        using (var image = ImageFixtureFactory.CreatePattern(711))
        {
            await image.SaveAsGifAsync(Path.Combine(root, "one.gif"));
            await image.SaveAsGifAsync(Path.Combine(root, "two.gif"));
        }
        var missingLegacy = directory.GetPath("missing-legacy");
        await store.UpdateAsync(state =>
        {
            state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
            {
                Category = VrcImageCategory.Emoji,
                ArchivePath = missingLegacy,
            });
            return true;
        });
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = CreateCoordinator(store, recycleBin);

        var result = await coordinator.CleanArchiveDuplicatesAsync();

        Assert.False(result.IsComplete);
        Assert.Equal(0, result.Removed);
        Assert.Empty(recycleBin.RecycledPaths);
        Assert.Equal(2, Directory.EnumerateFiles(root).Count());
    }

    [Fact]
    public async Task CleanupLeavesDifferentAnimationsOfTheSameEmojiAlone()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        var root = directory.GetPath("archive");
        var first = Path.Combine(root, "emoji_inv_123_64frames_12fps_linearloopStyle.gif");
        var second = Path.Combine(root, "emoji_inv_123_64frames_12fps_linearloopStyle (2).gif");
        using (var image = ImageFixtureFactory.CreatePattern(712))
            await image.SaveAsGifAsync(first);
        using (var image = ImageFixtureFactory.CreatePattern(713))
            await image.SaveAsGifAsync(second);
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = CreateCoordinator(store, recycleBin);

        var result = await coordinator.CleanArchiveDuplicatesAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(0, result.Removed);
        Assert.Empty(recycleBin.RecycledPaths);
        Assert.Equal(2, Directory.EnumerateFiles(root).Count());
    }

    [Fact]
    public async Task CategoryScanCleansArchiveDuplicatesUsingItsFreshIndex()
    {
        using var directory = new TestDirectory();
        using var store = await CreateStoreAsync(directory);
        Directory.CreateDirectory(directory.GetPath("incoming"));
        await store.UpdateAsync(state =>
        {
            state.Settings.OutputRootConfirmed = true;
            state.Settings.CategoryMappings.Single(mapping => mapping.Category == VrcImageCategory.Emoji).IsEnabled = true;
            return true;
        });
        var root = directory.GetPath("archive");
        using (var image = ImageFixtureFactory.CreatePattern(714))
        {
            await image.SaveAsGifAsync(Path.Combine(root, "one.gif"));
            await image.SaveAsGifAsync(Path.Combine(root, "two (2).gif"));
        }
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = CreateCoordinator(store, recycleBin);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.ArchiveDuplicatesRemoved);
        Assert.Single(recycleBin.RecycledPaths);
        Assert.Single(Directory.EnumerateFiles(root));
    }

    private static async Task<JsonStateStore> CreateStoreAsync(TestDirectory directory)
    {
        Directory.CreateDirectory(directory.GetPath("archive"));
        var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), directory.GetPath("archive"));
        await store.UpdateAsync(state =>
        {
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                mapping.IsEnabled = false;
                if (mapping.Category != VrcImageCategory.Emoji)
                {
                    mapping.ArchivePath = string.Empty;
                }
            }
            return true;
        });
        return store;
    }

    private static ScanCoordinator CreateCoordinator(
        JsonStateStore store,
        FileRouterTests.FakeRecycleBinService? recycleBin = null)
    {
        var decoder = new ImageDecoder();
        return new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder,
            new FileRouter(store, decoder, recycleBin ?? new FileRouterTests.FakeRecycleBinService()));
    }
}
