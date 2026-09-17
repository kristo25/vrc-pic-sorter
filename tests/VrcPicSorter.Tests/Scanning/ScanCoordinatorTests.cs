using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class ScanCoordinatorTests
{
    [Fact]
    public async Task FileChangedDuringSettleWindowIsNotMoved()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var incoming = Path.Combine(sourceRoot, "changing.png");
        using (var image = ImageFixtureFactory.CreatePattern(20))
        {
            await image.SaveAsPngAsync(incoming);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.FromMilliseconds(400));

        var scan = coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        await Task.Delay(150);
        File.SetLastWriteTimeUtc(incoming, DateTime.UtcNow.AddSeconds(1));
        var result = await scan;

        Assert.Contains(result.Errors, error => error.Contains("still being written", StringComparison.Ordinal));
        Assert.True(File.Exists(incoming));
        Assert.Empty(Directory.GetFiles(archiveRoot));
    }

    [Fact]
    public async Task ExactMatchRecyclesTheIncomingCopyAndKeepsTheArchivedOne()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(21);
        var incoming = Path.Combine(sourceRoot, "different-name.png");
        var archived = Path.Combine(archiveRoot, "original.png");
        await image.SaveAsPngAsync(incoming);
        await image.SaveAsPngAsync(archived);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "ignored.temp"), "not an image");
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.Equal(0, result.HeldForReview);
        Assert.Equal(incoming, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(archived));
        Assert.True(File.Exists(Path.Combine(sourceRoot, "ignored.temp")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task ExactMatchIsQueuedForReviewWhenTheRecycleBinIsUnavailable()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(22);
        var incoming = Path.Combine(sourceRoot, "copy.png");
        var archived = Path.Combine(archiveRoot, "original.png");
        await image.SaveAsPngAsync(incoming);
        await image.SaveAsPngAsync(archived);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService(canRecycle: false);
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // Never delete permanently: without a Recycle Bin the decision goes back to the user.
        Assert.Equal(0, result.AutoKeptArchived);
        Assert.Equal(1, result.HeldForReview);
        Assert.Empty(recycleBin.RecycledPaths);
        Assert.True(File.Exists(incoming));
        Assert.True(File.Exists(archived));
        var review = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Equal(MatchKind.Exact, Assert.Single(review.Candidates).MatchKind);
    }

    [Fact]
    public async Task UniqueImageMovesToArchiveAndSecondScanIsIdempotent()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(31);
        var incoming = Path.Combine(sourceRoot, "unique.png");
        await image.SaveAsPngAsync(incoming);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var first = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, first.MovedUnique);
        Assert.Equal(0, second.Examined);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task NormalScanRefreshesIndexAfterExternalArchiveAddition()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        var incoming = Path.Combine(sourceRoot, "incoming.png");
        var archived = Path.Combine(archiveRoot, "externally-added.png");
        using (var image = ImageFixtureFactory.CreatePattern(205))
        {
            await image.SaveAsPngAsync(incoming);
            await image.SaveAsPngAsync(archived);
        }

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // The externally added archive file was picked up by the refresh, so the incoming
        // exact copy resolved itself.
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.MovedUnique);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.True(File.Exists(archived));
        Assert.False(File.Exists(incoming));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task ConcurrentScanRequestsRouteAUniqueFileExactlyOnce()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(41);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "once.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var results = await Task.WhenAll(
            coordinator.ScanCategoryAsync(VrcImageCategory.Emoji),
            coordinator.ScanCategoryAsync(VrcImageCategory.Emoji));

        Assert.Equal(1, results.Sum(result => result.MovedUnique));
        Assert.Single(Directory.GetFiles(archiveRoot, "*.png"));
        Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images);
    }

    [Fact]
    public async Task ArchiveChangeRefreshWaitsForActiveScanBeforeInvalidatingIndex()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var incomingPaths = new[]
        {
            Path.Combine(sourceRoot, "01.png"),
            Path.Combine(sourceRoot, "02.png"),
            Path.Combine(sourceRoot, "03.png"),
        };
        for (var index = 0; index < incomingPaths.Length; index++)
        {
            using var image = ImageFixtureFactory.CreatePattern(80 + index);
            await image.SaveAsPngAsync(incomingPaths[index]);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.FromMilliseconds(100));

        var activeScan = coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        await WaitUntilAsync(() => !File.Exists(incomingPaths[0]), TimeSpan.FromSeconds(5));
        var watcherRefresh = coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        var first = await activeScan;
        var second = await watcherRefresh;

        Assert.Empty(first.Errors);
        Assert.Equal(3, first.MovedUnique);
        Assert.Equal(0, second.Examined);
        Assert.Equal(3, Directory.GetFiles(archiveRoot, "*.png").Length);
        Assert.Equal(IndexStatus.Current, (await store.LoadAsync()).ArchiveIndex.Categories[0].Status);
    }

    [Fact]
    public async Task ConfiguredScanPreservesCategoryRelativeSubfolders()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var month = Path.Combine(sourceRoot, "2025-05");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(month);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(63);
        await image.SaveAsPngAsync(Path.Combine(month, "emoji.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);

        // Archiving flat is the default now, so a test about keeping the incoming folders has to
        // ask for that policy rather than inherit it.
        await store.UpdateAsync(state =>
        {
            state.Settings.OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "2025-05", "emoji.png")));
    }

    [Fact]
    public async Task ManualScanUsesSelectedCategoryWithoutEnablingItsConfiguredSource()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(64);
        await image.SaveAsPngAsync(Path.Combine(manualSource, "manual.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji).IsEnabled = false;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var progress = new RecordingProgress<ScanProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "manual.png")));
        Assert.Equal(new ScanProgress(VrcImageCategory.Emoji, 0, 1), progress.Values[0]);
        Assert.Equal(1, progress.Values[^1].ScannedImages);
        Assert.Equal(1, progress.Values[^1].TotalImages);
    }

    [Fact]
    public async Task ScanningTheOutputFolderSaysThatIsWhatItIs()
    {
        // Choosing the archive as a folder to scan is the easy mistake to make, and being told
        // only that it "overlaps a configured source, output, or holding folder" left three
        // candidates and no way to tell which had been hit.
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var outputRoot = directory.GetPath("archive");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.OutputRootPath = outputRoot;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(outputRoot, VrcImageCategory.Emoji, progress: null));

        Assert.Contains("is already the output folder", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanningInsideAConfiguredSourceNamesThatSourceAndItsPath()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var nested = Path.Combine(configuredSource, "nested");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(nested, VrcImageCategory.Emoji, progress: null));

        Assert.Contains("the Emoji source folder", failure.Message, StringComparison.Ordinal);
        Assert.Contains(configuredSource, failure.Message, StringComparison.Ordinal);

        // The same-folder wording belongs to the other case; this one really is an overlap.
        Assert.DoesNotContain("is already", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualScanProgressUsesOneStableFileSnapshot()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var first = ImageFixtureFactory.CreatePattern(70);
        using var later = ImageFixtureFactory.CreatePattern(71);
        await first.SaveAsPngAsync(Path.Combine(manualSource, "first.png"));
        var staged = directory.GetPath("later.png");
        await later.SaveAsPngAsync(staged);
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var values = new List<ScanProgress>();
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            values.Add(value);
            if (value.ScannedImages == 0)
            {
                File.Copy(staged, Path.Combine(manualSource, "added-after-count.png"));
            }
        });

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, values[^1].ScannedImages);
        Assert.Equal(1, values[^1].TotalImages);
        Assert.True(File.Exists(Path.Combine(manualSource, "added-after-count.png")));
    }

    /// <summary>
    /// Reading runs ahead of routing, so the next image may already have been read by this point.
    /// What must not change is that images are routed one at a time in path order.
    /// </summary>
    [Fact]
    public async Task RoutesEachImageBeforeReportingTheNextRead()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        var paths = new[]
        {
            Path.Combine(manualSource, "first.png"),
            Path.Combine(manualSource, "second.png"),
        };
        for (var index = 0; index < paths.Length; index++)
        {
            using var image = ImageFixtureFactory.CreatePattern(90 + index);
            await image.SaveAsPngAsync(paths[index]);
        }

        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var firstRoutedBeforeSecondReadCompleted = false;
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            if (value.ScannedImages == 2)
            {
                firstRoutedBeforeSecondReadCompleted = !File.Exists(paths[0]) && File.Exists(paths[1]);
            }
        });

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.True(firstRoutedBeforeSecondReadCompleted);
        Assert.Equal(2, result.MovedUnique);
    }

    [Fact]
    public async Task DuplicateBeyondTheReadAheadWindowStillMatchesTheEarlierUnique()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);

        // Six files: the last one repeats the first. Reads run ahead by up to five images, so the
        // copy is read before the original has been archived. The decision for it still has to be
        // made against the archive as it stands when its turn comes.
        using var original = ImageFixtureFactory.CreatePattern(120);
        await original.SaveAsPngAsync(Path.Combine(sourceRoot, "01-original.png"));
        for (var index = 2; index <= 5; index++)
        {
            using var other = ImageFixtureFactory.CreatePattern(120 + index);
            await other.SaveAsPngAsync(Path.Combine(sourceRoot, $"0{index}-other.png"));
        }

        await original.SaveAsPngAsync(Path.Combine(sourceRoot, "06-copy.png"));

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(5, result.MovedUnique);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.Equal(0, result.HeldForReview);
        Assert.False(File.Exists(Path.Combine(sourceRoot, "06-copy.png")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "01-original.png")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task LaterImageMatchesUniqueMovedEarlierInSameScan()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(98);
        var first = Path.Combine(sourceRoot, "01-first.png");
        var second = Path.Combine(sourceRoot, "02-copy.png");
        await image.SaveAsPngAsync(first);
        await image.SaveAsPngAsync(second);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // The second copy is compared against the first one already archived in this same scan.
        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.Equal(0, result.HeldForReview);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "01-first.png")));
        Assert.False(File.Exists(second));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task ProcessingProgressCompletesAfterUniqueAndReviewRouting()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(96);
        using var duplicate = ImageFixtureFactory.CreateNearDuplicate(archived);
        using var unique = ImageFixtureFactory.CreatePattern(97);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        await duplicate.SaveAsPngAsync(Path.Combine(manualSource, "duplicate.png"));
        await unique.SaveAsPngAsync(Path.Combine(manualSource, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var readingProgress = new RecordingProgress<ScanProgress>();
        var processingProgress = new RecordingProgress<ScanProcessingProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            readingProgress,
            processingProgress);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        Assert.Equal(new ScanProcessingProgress(0, 2), processingProgress.Values[0]);
        Assert.Equal(2, processingProgress.Values[^1].ProcessedImages);
        Assert.Equal(2, processingProgress.Values[^1].TotalImages);
        Assert.Equal(3, processingProgress.Values.Count);
    }

    [Fact]
    public async Task ProcessesReviewAndUniqueImagesInPathOrder()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(92);
        using var duplicate = ImageFixtureFactory.CreateNearDuplicate(archived);
        using var unique = ImageFixtureFactory.CreatePattern(93);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        await duplicate.SaveAsPngAsync(Path.Combine(sourceRoot, "01-duplicate.png"));
        await unique.SaveAsPngAsync(Path.Combine(sourceRoot, "02-unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        var state = await store.LoadAsync();
        Assert.True(File.Exists(Path.Combine(sourceRoot, "01-duplicate.png")));
        Assert.False(File.Exists(Path.Combine(sourceRoot, "02-unique.png")));
        var operations = state.History
            .Select(entry => entry.Message)
            .ToArray();
        Assert.Contains("Queued incoming image for review.", operations);
        Assert.Contains(nameof(JournalOperationPurpose.MoveUnique), operations);
        Assert.True(
            Array.IndexOf(operations, "Queued incoming image for review.")
            < Array.IndexOf(operations, nameof(JournalOperationPurpose.MoveUnique)));
    }

    [Fact]
    public async Task ScanAllProcessesEnabledCategoriesSequentially()
    {
        using var directory = new TestDirectory();
        var emojiSource = directory.GetPath("incoming", "Emoji");
        var printsSource = directory.GetPath("incoming", "Prints");
        var emojiArchive = directory.GetPath("archive", "Emoji");
        var printsArchive = directory.GetPath("archive", "Prints");
        Directory.CreateDirectory(emojiSource);
        Directory.CreateDirectory(printsSource);
        Directory.CreateDirectory(emojiArchive);
        Directory.CreateDirectory(printsArchive);
        var duplicatePath = Path.Combine(emojiSource, "duplicate.png");
        var uniquePath = Path.Combine(printsSource, "unique.png");
        using var archived = ImageFixtureFactory.CreatePattern(94);
        using var duplicate = ImageFixtureFactory.CreateNearDuplicate(archived);
        using var unique = ImageFixtureFactory.CreatePattern(95);
        await archived.SaveAsPngAsync(Path.Combine(emojiArchive, "existing.png"));
        await duplicate.SaveAsPngAsync(duplicatePath);
        await unique.SaveAsPngAsync(uniquePath);
        using var store = FileRouterTests.CreateStore(directory, emojiSource, emojiArchive);
        await store.UpdateAsync(state =>
        {
            var prints = state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Prints);
            prints.SourcePath = printsSource;
            prints.ArchivePath = printsArchive;
            prints.IsEnabled = true;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var emojiProcessedBeforePrintsRead = false;
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            if (value.Category == VrcImageCategory.Prints && value.ScannedImages == 2)
            {
                emojiProcessedBeforePrintsRead = File.Exists(duplicatePath) && File.Exists(uniquePath);
            }
        });
        var processingProgress = new RecordingProgress<ScanProcessingProgress>();

        var results = await coordinator.ScanAllAsync(progress, processingProgress);

        Assert.True(emojiProcessedBeforePrintsRead);
        Assert.Equal(1, results.Sum(result => result.MovedUnique));
        Assert.Equal(1, results.Sum(result => result.HeldForReview));
        Assert.Equal(new ScanProcessingProgress(0, 2), processingProgress.Values[0]);
        Assert.Equal(2, processingProgress.Values[^1].ProcessedImages);
        Assert.Equal(2, processingProgress.Values[^1].TotalImages);
        Assert.Equal(3, processingProgress.Values.Count);
        var operations = (await store.LoadAsync()).History
            .Where(entry => entry.OperationId is not null)
            .Select(entry => entry.Message)
            .ToArray();
        Assert.Single(operations);
        Assert.Equal(nameof(JournalOperationPurpose.MoveUnique), operations[0]);
    }

    [Fact]
    public async Task ProgressCompletesWhenArchiveIndexCannotBeBuilt()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(72);
        await image.SaveAsPngAsync(Path.Combine(manualSource, "blocked.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);

        // A scan creates a missing output folder, so block the path with a file instead:
        // the index genuinely cannot be built, and progress must still complete.
        Directory.Delete(archiveRoot);
        await File.WriteAllTextAsync(archiveRoot, "not a folder");
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var progress = new RecordingProgress<ScanProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.NotEmpty(result.Errors);
        Assert.Equal(1, progress.Values[^1].ScannedImages);
        Assert.Equal(1, progress.Values[^1].TotalImages);
    }

    [Fact]
    public async Task ManualScanRejectsAnInputInsideTheOutputRoot()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(archiveRoot, VrcImageCategory.Emoji));
    }

    [Fact]
    public async Task ManualScanRejectsRetainedLegacyArchive()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var legacyRoot = directory.GetPath("legacy", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(legacyRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
            {
                Category = VrcImageCategory.Emoji,
                ArchivePath = legacyRoot,
            });
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(legacyRoot, VrcImageCategory.Emoji));
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected scan state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task UnreadableArchiveFileDoesNotBlockScanningTheCategory()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(41);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "readable.png"));
        var unreadable = Path.Combine(archiveRoot, "unreadable.png");
        await File.WriteAllBytesAsync(unreadable, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        using var image = ImageFixtureFactory.CreatePattern(42);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
        Assert.True(File.Exists(unreadable));
        Assert.Contains(result.Errors, error => error.Contains(unreadable, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanCreatesAMissingOutputFolderInsteadOfFailing()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        using var image = ImageFixtureFactory.CreatePattern(43);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);
        Assert.False(Directory.Exists(archiveRoot));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.True(Directory.Exists(archiveRoot));
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
    }

    private static ScanCoordinator CreateCoordinator(JsonStateStore store)
    {
        var decoder = new ImageDecoder();
        return new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);
    }

    [Fact]
    public async Task ScanningIncomingPathsAnalyzesOnlyTheNamedFiles()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var arrived = Path.Combine(sourceRoot, "arrived.png");
        var untouched = Path.Combine(sourceRoot, "untouched.png");
        using (var image = ImageFixtureFactory.CreatePattern(51))
        {
            await image.SaveAsPngAsync(arrived);
        }

        using (var image = ImageFixtureFactory.CreatePattern(52))
        {
            await image.SaveAsPngAsync(untouched);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanIncomingPathsAsync(VrcImageCategory.Emoji, [arrived]);

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "arrived.png")));

        // The file that was already sitting in the folder is never looked at.
        Assert.True(File.Exists(untouched));
        Assert.False(File.Exists(Path.Combine(archiveRoot, "untouched.png")));
    }

    [Fact]
    public async Task ScanningIncomingPathsIgnoresPathsOutsideTheSourceFolder()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var elsewhere = directory.GetPath("elsewhere");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(elsewhere);
        var outside = Path.Combine(elsewhere, "outside.png");
        using (var image = ImageFixtureFactory.CreatePattern(53))
        {
            await image.SaveAsPngAsync(outside);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanIncomingPathsAsync(VrcImageCategory.Emoji, [outside]);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.MovedUnique);
        Assert.True(File.Exists(outside));
        Assert.Empty(Directory.GetFiles(archiveRoot));
    }

    [Fact]
    public async Task ScanningIncomingPathsSkipsUnsupportedFiles()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var video = Path.Combine(sourceRoot, "clip.mp4");
        await File.WriteAllTextAsync(video, "not an image");
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanIncomingPathsAsync(VrcImageCategory.Emoji, [video]);

        Assert.Equal(0, result.Examined);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task ExactMatchFallsBackToReviewWhenTheRecycleFails()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(23);
        var incoming = Path.Combine(sourceRoot, "copy.png");
        var archived = Path.Combine(archiveRoot, "original.png");
        await image.SaveAsPngAsync(incoming);
        await image.SaveAsPngAsync(archived);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService(throwOnRecycle: true);
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // The automatic path failed, so the image must still reach the user rather than vanish
        // or be silently dropped.
        Assert.Equal(0, result.AutoKeptArchived);
        Assert.Equal(1, result.HeldForReview);
        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(incoming));
        Assert.True(File.Exists(archived));
        Assert.Single((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task SettleCheckDoesNotBlockAnotherWriterHoldingTheFile()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(24);
        var incoming = Path.Combine(sourceRoot, "held-open.png");
        await image.SaveAsPngAsync(incoming);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        // Stand in for VRCX still holding the file it just wrote, sharing read access.
        await using (new FileStream(
            incoming,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

            Assert.Equal(1, result.Examined);
            Assert.Empty(result.Errors);
        }
    }

    [Fact]
    public async Task AnAnimatedSheetIsFiledWithTheAnimationItProduced()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string name = "player_x_4frames_10fps_linearloopStyle.png";
        WriteSheet(Path.Combine(sourceRoot, name));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.Animated);

        var animation = Path.Combine(archiveRoot, "Animated", "player_x_4frames_10fps_linearloopStyle.gif");
        var filed = Path.Combine(archiveRoot, "Animated", "Gif Ref", name);
        Assert.True(File.Exists(animation));
        Assert.True(File.Exists(filed));
        // The sheet does not stay among the stills a person browses.
        Assert.False(File.Exists(Path.Combine(archiveRoot, name)));

        var state = await store.LoadAsync();
        var indexed = Assert.Single(
            state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji).Images);
        Assert.Equal(filed, indexed.Path);
    }

    /// <summary>
    /// The archiver never overwrites: when the name it wants is taken it adds a number. That means
    /// an animation can be sitting right there under a name the next scan does not think to look
    /// for - and asking File.Exists about one exact name is how an archive ends up holding two of
    /// everything it had already made.
    /// </summary>
    [Fact]
    public async Task AnAnimationAlreadyThereUnderAnotherNameIsNotWrittenAgain()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string sheetName =
            "player_inv_11111111-2222-3333-4444-555555555555_stopanimationStyle_4frames_10fps_linearloopStyle.png";
        WriteSheet(Path.Combine(sourceRoot, sheetName));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        var first = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Equal(1, first.Animated);

        var animationFolder = Path.Combine(archiveRoot, "Animated");
        var made = Assert.Single(Directory.GetFiles(animationFolder, "*.gif"));
        var renamed = Path.Combine(
            animationFolder,
            Path.GetFileNameWithoutExtension(made) + " (2).gif");
        File.Move(made, renamed);

        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Empty(second.Errors);
        Assert.Equal(0, second.Animated);
        Assert.Equal(renamed, Assert.Single(Directory.GetFiles(animationFolder, "*.gif")));
    }

    /// <summary>
    /// Run it again and nothing happens. When that stops being true every scan adds another copy
    /// of whatever it failed to recognise, which is exactly how an archive doubles overnight.
    /// </summary>
    [Fact]
    public async Task ASecondScanLeavesTheArchiveExactlyAsItWas()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        WriteSheet(Path.Combine(
            sourceRoot,
            "player_inv_22222222-2222-3333-4444-555555555555_stopanimationStyle_4frames_10fps_linearloopStyle.png"));
        using var still = ImageFixtureFactory.CreatePattern(seed: 70);
        await still.SaveAsPngAsync(Path.Combine(sourceRoot, "ordinary.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var first = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Empty(first.Errors);
        Assert.Equal(2, first.MovedUnique);
        Assert.Equal(1, first.Animated);
        var settled = Snapshot(archiveRoot);

        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Empty(second.Errors);
        Assert.Equal(0, second.MovedUnique);
        Assert.Equal(0, second.Animated);
        Assert.Equal(0, second.HeldForReview);
        Assert.Equal(0, second.AutoKeptArchived);
        Assert.Equal(settled, Snapshot(archiveRoot));
    }

    /// <summary>
    /// The guard that makes the rest of this safe to trust: a run that wants to animate as much as
    /// the archive already holds has lost track of what is there, and the cost of letting it
    /// proceed is a second copy of every animation in the folder.
    /// </summary>
    [Fact]
    public async Task AScanThatWouldDoubleTheAnimationFolderRefusesAndSaysSo()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var animationFolder = Path.Combine(archiveRoot, "Animated");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(animationFolder);

        // Sheets the app can see, and just as many animations it cannot recognise as theirs.
        using var frame = ImageFixtureFactory.CreatePattern(seed: 71, width: 32, height: 32);
        for (var index = 0; index < 26; index++)
        {
            WriteSheet(Path.Combine(
                archiveRoot,
                $"player_inv_3333{index:D4}-2222-3333-4444-555555555555_stopanimationStyle_4frames_10fps_linearloopStyle.png"));
            await ImageFixtureFactory.SaveGifAsync(
                directory,
                Path.Combine("archive", "Emoji", "Animated", $"unrecognised-{index}.gif"),
                [frame, frame],
                [100, 100]);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(0, result.Animated);
        Assert.Equal(26, Directory.GetFiles(animationFolder, "*.gif").Length);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Stopped before writing 26 animations", StringComparison.Ordinal));
    }

    private static string[] Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task SheetsAreScannedBeforeReadyMadeGifs()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);

        // Same emoji, both copies arriving together. Alphabetically the GIF comes first, which is
        // the wrong way round: it is judged against the animation the sheet produces, so the sheet
        // has to be archived before the GIF's turn comes.
        const string sheetName = "player_x_4frames_10fps_linearloopStyle.png";
        const string gifName = "player_x_4frames_10fps_linearloopStyle.gif";
        const string stillName = "aaa-ordinary-still.png";
        WriteSheet(Path.Combine(manualSource, sheetName));
        using var still = ImageFixtureFactory.CreatePattern(seed: 61);
        await still.SaveAsPngAsync(Path.Combine(manualSource, stillName));
        using var frame = ImageFixtureFactory.CreatePattern(seed: 60, width: 64, height: 64);
        await ImageFixtureFactory.SaveGifAsync(
            directory,
            Path.Combine("manual", gifName),
            [frame, frame],
            [100, 100]);
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var coordinator = CreateCoordinator(store);
        var reading = new RecordingProgress<ScanProgress>();

        await coordinator.ScanFolderAsync(manualSource, VrcImageCategory.Emoji, reading, null);

        var order = reading.Values
            .Select(value => value.FileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sheetAt = Array.FindIndex(order, name => string.Equals(name, sheetName, StringComparison.OrdinalIgnoreCase));
        var stillAt = Array.FindIndex(order, name => string.Equals(name, stillName, StringComparison.OrdinalIgnoreCase));
        var gifAt = Array.FindIndex(order, name => string.Equals(name, gifName, StringComparison.OrdinalIgnoreCase));
        Assert.True(sheetAt >= 0 && stillAt >= 0 && gifAt >= 0, string.Join(", ", order));
        Assert.True(sheetAt < stillAt, string.Join(", ", order));
        Assert.True(stillAt < gifAt, string.Join(", ", order));
    }

    [Fact]
    public async Task AnAnimationWrittenDuringAScanIsIndexedByThatSameScan()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string name = "player_x_4frames_10fps_linearloopStyle.png";

        // Already in the archive, the way a sheet filed before animations existed would be. The
        // scan fills in its missing animation, and that happens after the index has been built.
        WriteSheet(Path.Combine(archiveRoot, name));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Animated);
        var animation = Path.Combine(archiveRoot, "Animated", "player_x_4frames_10fps_linearloopStyle.gif");
        Assert.True(File.Exists(animation));

        // Left out of the index it is invisible to matching for the rest of the scan, and an
        // incoming copy of it is archived all over again as though the app had never made it.
        var images = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji).Images;
        Assert.Contains(images, image => string.Equals(image.Path, animation, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFiledSheetIsStillRecognisedWhenTheSameEmojiArrivesAgain()
    {
        // The reference folder sits inside the folder the indexer skips, so the whole point of
        // carving it back out is this: a second copy of an emoji already animated must be caught
        // as a duplicate rather than archived and animated all over again.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string name = "player_x_4frames_10fps_linearloopStyle.png";
        WriteSheet(Path.Combine(sourceRoot, name));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        WriteSheet(Path.Combine(sourceRoot, name));
        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(0, second.MovedUnique);
        Assert.Equal(0, second.Animated);
        Assert.Equal(1, second.AutoKeptArchived);

        // The second scan indexes the animation as well as the sheet, so the archive holds two
        // records. What matters is that the sheet is still there exactly once.
        var state = await store.LoadAsync();
        var images = state.ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji).Images;
        Assert.Single(images, image => image.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASheetThatCouldNotBeAnimatedStaysWhereItCanBeSeen()
    {
        // Twenty frames need an 8x8 grid, and 1020 pixels do not divide into eight whole cells, so
        // there is no layout to animate at all. That is a real failure rather than a disagreement
        // about frame count, and the sheet stays among the stills where it can be seen instead of
        // being filed away as finished work.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string name = "player_x_20frames_10fps_linearloopStyle.png";
        using (var image = new Image<Rgba32>(1020, 1020))
        {
            image.Mutate(context => context.BackgroundColor(Color.FromRgb(30, 90, 160)));
            await image.SaveAsPngAsync(Path.Combine(sourceRoot, name));
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(0, result.Animated);
        Assert.True(File.Exists(Path.Combine(archiveRoot, name)));
        Assert.False(Directory.Exists(Path.Combine(archiveRoot, "Animated", "Gif Ref")));
    }


    [Fact]
    public async Task AReadyMadeGifIsFiledWithTheAnimationsRatherThanTheStills()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        WriteGif(Path.Combine(sourceRoot, "loose.gif"), 3);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Animated", "loose.gif")));
        Assert.False(File.Exists(Path.Combine(archiveRoot, "loose.gif")));
    }

    [Fact]
    public async Task ASecondCopyOfAnArchivedGifIsRecognised()
    {
        // Animations are indexed like everything else, so the copy that arrives second is caught
        // the same way a repeated still is. They used to be invisible to the index and archived
        // again on every scan.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        WriteGif(Path.Combine(sourceRoot, "loose.gif"), 3);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        WriteGif(Path.Combine(sourceRoot, "loose.gif"), 3);
        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(0, second.MovedUnique);
        Assert.Equal(1, second.AutoKeptArchived);
    }

    [Fact]
    public async Task AGifThatMatchesTheSheetsOwnAnimationIsRecycled()
    {
        // The sheet is archived but never animated. A ready-made GIF of the same emoji then
        // arrives: the sheet's own animation is generated so there is something to compare
        // against, and the incoming copy turns out to be the same animation.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string sheetName = "player_x_4frames_10fps_linearloopStyle.png";
        var archivedSheet = Path.Combine(archiveRoot, sheetName);
        WriteSheet(archivedSheet);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        var generated = Path.Combine(
            archiveRoot, "Animated", "player_x_4frames_10fps_linearloopStyle.gif");
        Assert.True(File.Exists(generated));

        // Keep a copy as the incoming GIF, then put the archive back to a sheet with no animation
        // and skip it so the backfill leaves it that way. Now the only way to judge the incoming
        // file is to make the sheet's animation on the spot and compare the two.
        var incoming = Path.Combine(sourceRoot, "player_x_4frames_10fps_linearloopStyle.gif");
        File.Copy(generated, incoming);
        File.Delete(generated);
        var archived = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji).Images
            .Single(item => item.Path.EndsWith(".png", StringComparison.Ordinal));
        await store.UpdateAsync(state =>
        {
            state.SkippedAnimations.Add(archived.ExactFingerprint);
            return true;
        });

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(0, result.MovedUnique);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.False(File.Exists(incoming));
    }

    [Fact]
    public async Task AnArchivedGifIsNotOverwrittenByTheSheetThatArrivesAfterIt()
    {
        // The GIF lands first and is archived under the very name the sheet's export would take.
        // The exporter writes with overwrite, so without care the sheet would destroy an archived
        // file and leave the index describing pixels that are gone.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string stem = "player_x_4frames_10fps_linearloopStyle";
        WriteGif(Path.Combine(sourceRoot, stem + ".gif"), 3);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        var archivedGif = Path.Combine(archiveRoot, "Animated", stem + ".gif");
        Assert.True(File.Exists(archivedGif));
        var before = await File.ReadAllBytesAsync(archivedGif);

        WriteSheet(Path.Combine(sourceRoot, stem + ".png"));
        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(before, await File.ReadAllBytesAsync(archivedGif));
    }

    [Fact]
    public async Task ArtLeftOutOfAnAnimationIsRecordedWithoutFailingTheScan()
    {
        // The sheet is animated to its name and then filed away as finished. If some of its art did
        // not make it into the animation the history is the only place that will ever say so - but
        // it is not a failure, and counting it among the errors made a scan that did exactly what
        // it was asked announce itself as having completed with warnings.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        const string name = "player_x_3frames_10fps_linearloopStyle.png";
        WriteSheet(Path.Combine(sourceRoot, name));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.Animated);
        Assert.Empty(result.Errors);

        var history = (await store.LoadAsync()).History;
        var note = Assert.Single(
            history,
            entry => entry.Message.Contains("4 of 4 cells", StringComparison.Ordinal));
        Assert.Equal(ActivityLevel.Information, note.Level);

        // The summary entry is what the window reads to decide whether to say "completed with
        // warnings", so the note must not drag it up to Warning either.
        Assert.DoesNotContain(
            history,
            entry => entry.Level == ActivityLevel.Warning);
    }

    /// <summary>A small animation of solid frames, standing in for a ready-made GIF.</summary>
    private static void WriteGif(string path, int frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(32, 32);
        image.Mutate(context => context.BackgroundColor(Color.FromRgb(10, 60, 120)));
        for (var index = 1; index < frames; index++)
        {
            using var frame = new Image<Rgba32>(32, 32);
            frame.Mutate(context => context.BackgroundColor(
                Color.FromRgb((byte)(10 + (index * 40)), 60, 120)));
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }

        image.SaveAsGif(path);
    }

    /// <summary>
    /// A 2x2 sheet of four frames, each a solid square inset in its cell so the sheet carries the
    /// alpha a real emoji sheet does.
    /// </summary>
    /// <summary>
    /// A sheet the backfill animates is filed beside its animation, rather than left sitting among
    /// the single emoji.
    /// </summary>
    /// <remarks>
    /// The archive this was reported against had four sprite sheets in the Emoji folder, each with
    /// its animation already made. They had all been animated by the backfill, which wrote the GIF
    /// and deliberately left the sheet where it was - so while browsing, a grid of sixty-four
    /// thumbnails sat among the emoji looking like one of them.
    /// </remarks>
    [Fact]
    public async Task ASheetTheBackfillAnimatesIsFiledWithIt()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        const string sheetName = "player_x_4frames_10fps_linearloopStyle.png";
        var archivedSheet = Path.Combine(archiveRoot, sheetName);
        WriteSheet(archivedSheet);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Animated);
        Assert.Empty(result.Errors);

        // The animation exists, and the sheet has gone to sit beside it.
        Assert.True(File.Exists(Path.Combine(
            archiveRoot, "Animated", "player_x_4frames_10fps_linearloopStyle.gif")));
        var filed = Path.Combine(
            archiveRoot, "Animated", "Gif Ref", "player_x_4frames_10fps_linearloopStyle.png");
        Assert.True(File.Exists(filed));
        Assert.False(File.Exists(archivedSheet));

        // And nothing is left in the Emoji folder that a person would mistake for an emoji.
        Assert.Empty(Directory.GetFiles(archiveRoot));

        var index = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji);
        Assert.Contains(index.Images, item => string.Equals(item.Path, filed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A sheet animated before any of this existed is caught up with rather than left behind.
    /// </summary>
    /// <remarks>
    /// The four sheets found sitting in a real Emoji folder were exactly this: their animation had
    /// been made long ago, so there was nothing left to write for them and the backfill skipped
    /// them outright. Nothing brought them along, and nothing ever would have.
    /// </remarks>
    [Fact]
    public async Task ASheetAnimatedLongAgoIsFiledOnTheNextScan()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        const string sheetName = "player_x_4frames_10fps_linearloopStyle.png";
        var archivedSheet = Path.Combine(archiveRoot, sheetName);
        WriteSheet(archivedSheet);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);

        // The first scan animates it and files it, which is the fixed behaviour. Put the sheet
        // back where a version that only ever wrote the GIF would have left it.
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        var filed = Path.Combine(
            archiveRoot, "Animated", "Gif Ref", sheetName);
        Assert.True(File.Exists(filed));
        File.Move(filed, archivedSheet);
        Directory.Delete(Path.GetDirectoryName(filed)!, recursive: true);
        Assert.True(File.Exists(Path.Combine(
            archiveRoot, "Animated", "player_x_4frames_10fps_linearloopStyle.gif")));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // Nothing to animate - the GIF is already there - and the sheet still comes along.
        Assert.Equal(0, result.Animated);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(filed));
        Assert.False(File.Exists(archivedSheet));
        Assert.Empty(Directory.GetFiles(archiveRoot));

        // And the index followed it, rather than describing a file that is no longer there.
        var images = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji).Images;
        Assert.Contains(images, item => string.Equals(item.Path, filed, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            images,
            item => string.Equals(item.Path, archivedSheet, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A ready-made GIF for an emoji the archive has already animated never becomes a second copy.
    /// </summary>
    /// <remarks>
    /// VRChat hands its own GIF over for some emoji, and it is a different encoding of the very
    /// same animation - different bytes, and a perceptual score that can fall short of any
    /// threshold. It was filed as a brand new image, which is where a folder of "x" beside
    /// "x (2)" came from. It earns a review card now, because the emoji id and the frame count,
    /// rate and loop direction in the name are VRChat's own account of what both files are.
    /// </remarks>
    [Fact]
    public async Task AReadyMadeGifOfAnAlreadyAnimatedEmojiIsNotArchivedAgain()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        const string sheetName =
            "trav_inv_f0fdf2cf-2371-42f2-90ae-8432ecda1d39_stopanimationStyle_4frames_10fps_linearloopStyle.png";
        WriteSheet(Path.Combine(archiveRoot, sheetName));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var coordinator = CreateCoordinator(store);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        var archived = Path.Combine(
            archiveRoot,
            "Animated",
            "trav_inv_f0fdf2cf-2371-42f2-90ae-8432ecda1d39_stopanimationStyle_4frames_10fps_linearloopStyle.gif");
        Assert.True(File.Exists(archived));

        // VRChat's own GIF of the same emoji. Deliberately nothing like the app's export to look
        // at: if the two merely resembled each other the perceptual ranking would already have
        // caught it, and this test would prove nothing. All these two have in common is what
        // VRChat wrote into both names, which is the whole point.
        var incoming = Path.Combine(sourceRoot, Path.GetFileName(archived));
        await WriteUnrelatedLookingAnimationAsync(incoming, frames: 4);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // Not archived as something new, and not discarded either: asked about.
        Assert.Equal(0, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        Assert.True(File.Exists(incoming));
        var review = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Contains(
            review.Candidates,
            candidate => string.Equals(candidate.ArchivePath, archived, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            review.Candidates.SelectMany(candidate => candidate.MatchReasons),
            reason => reason.Contains("same emoji", StringComparison.OrdinalIgnoreCase));

        // Nothing was thrown away on the strength of a name. Both files are still there.
        Assert.True(File.Exists(archived));
    }

    /// <summary>
    /// An animation that shares nothing with the archive but its file name.
    /// </summary>
    private static async Task WriteUnrelatedLookingAnimationAsync(string destination, int frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var animation = new Image<Rgba32>(64, 64, Colour(0));
        for (var index = 1; index < frames; index++)
        {
            using var frame = new Image<Rgba32>(64, 64, Colour(index));
            animation.Frames.AddFrame(frame.Frames.RootFrame);
        }

        await animation.SaveAsGifAsync(destination);

        static Rgba32 Colour(int index) =>
            new((byte)(30 + (index * 40)), (byte)(200 - (index * 30)), 90, 255);
    }

    /// <summary>
    /// An animation exported from the Animations tab has to go through the rest of what a scan
    /// does, or it is invisible to deduplication until the next full index.
    /// </summary>
    /// <remarks>
    /// This is how the archive came to hold the same emoji twice: Export GIF wrote the file and
    /// stopped there, so nothing knew it existed, the sheet stayed among the stills, and a copy
    /// arriving later had nothing to be compared against.
    /// </remarks>
    [Fact]
    public async Task AHandMadeExportIsIndexedAndItsSheetIsFiled()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        const string sheetName = "player_x_4frames_10fps_linearloopStyle.png";
        var archivedSheet = Path.Combine(archiveRoot, sheetName);
        WriteSheet(archivedSheet);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        Assert.Equal(IndexStatus.Current, (await indexer.RefreshAsync(VrcImageCategory.Emoji)).Status);
        var coordinator = CreateCoordinator(store);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());

        var exported = await catalog.ExportAsync(sheet, sheet.Name);
        Assert.True(exported.Exported);
        var animationPath = exported.Path!;

        var followUp = await coordinator.FinishExportedAnimationsAsync(
            [new ExportedAnimation(sheet.Id, sheet.Category, sheet.AtlasPath, animationPath)]);

        Assert.Empty(followUp.Warnings);
        Assert.Empty(followUp.Duplicates);

        // The archive now knows about the file the button wrote.
        var index = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji);
        Assert.Contains(index.Images, item => string.Equals(item.Path, animationPath, StringComparison.OrdinalIgnoreCase));

        // And the sheet was filed beside it, exactly as a scan files one.
        var filed = Assert.Single(followUp.Filed);
        Assert.Contains(AtlasAnimationWriter.ReferenceFolderName, filed, StringComparison.Ordinal);
        Assert.True(File.Exists(filed));
        Assert.False(File.Exists(archivedSheet));
        Assert.Contains(index.Images, item => string.Equals(item.Path, filed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// And when the export turns out to be a picture the archive already held, it is reported
    /// rather than acted on - both copies are archived, so which to keep is a decision.
    /// </summary>
    [Fact]
    public async Task AHandMadeExportThatDuplicatesAnArchivedCopyIsReportedNotRemoved()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        const string sheetName = "player_y_4frames_10fps_linearloopStyle.png";
        var archivedSheet = Path.Combine(archiveRoot, sheetName);
        WriteSheet(archivedSheet);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(store, decoder);
        await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var coordinator = CreateCoordinator(store);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());

        var exported = await catalog.ExportAsync(sheet, sheet.Name);
        var animationPath = exported.Path!;

        // The same animation already sitting in the archive under another name, which is exactly
        // what a ready-made GIF from VRChat looks like.
        var twin = Path.Combine(archiveRoot, "player_y (2).gif");
        File.Copy(animationPath, twin);

        var followUp = await coordinator.FinishExportedAnimationsAsync(
            [new ExportedAnimation(sheet.Id, sheet.Category, sheet.AtlasPath, animationPath)]);

        var duplicate = Assert.Single(followUp.Duplicates);
        Assert.Equal(animationPath, duplicate.AnimationPath);
        Assert.Equal(twin, Assert.Single(duplicate.ExistingCopies));

        // Reported, and both files still there.
        Assert.True(File.Exists(animationPath));
        Assert.True(File.Exists(twin));
    }

    private static void WriteSheet(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(128, 128);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var span = accessor.GetRowSpan(y);
                for (var x = 0; x < span.Length; x++)
                {
                    var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                    var cell = ((y / 64) * 2) + (x / 64);
                    span[x] = inset ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255) : default;
                }
            }
        });
        image.SaveAsPng(path);
    }

    [Fact]
    public async Task TwoCopiesOfOnePictureOpenOneReviewCard()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(140);
        using var incoming = ImageFixtureFactory.CreateNearDuplicate(archived);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        var first = Path.Combine(sourceRoot, "copy-a.png");
        var second = Path.Combine(sourceRoot, "copy-b.png");
        await incoming.SaveAsPngAsync(first);
        await incoming.SaveAsPngAsync(second);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // Both copies match the archived image the same way, and deciding that twice is the same
        // decision twice. The first is held; the second is the same picture and goes to the bin.
        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.HeldForReview);
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.Empty(result.Errors);
        var review = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Equal(first, review.IncomingOriginalPath);
        Assert.Equal(second, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task ACopyArrivingAfterTheReviewIsQueuedIsRecognisedToo()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(141);
        using var incoming = ImageFixtureFactory.CreateNearDuplicate(archived);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        var first = Path.Combine(sourceRoot, "copy-a.png");
        await incoming.SaveAsPngAsync(first);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);
        var held = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Equal(1, held.HeldForReview);

        var second = Path.Combine(sourceRoot, "copy-b.png");
        await incoming.SaveAsPngAsync(second);
        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // The queue survives between scans, so the copy that arrives later has to be measured
        // against what is waiting in it, not only against the archive.
        Assert.Equal(1, result.AutoKeptArchived);
        Assert.Equal(0, result.HeldForReview);
        Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Equal(second, Assert.Single(recycleBin.RecycledPaths));
    }

    [Fact]
    public async Task ASecondCopyGetsItsOwnReviewWhenTheRecycleBinIsUnavailable()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(142);
        using var incoming = ImageFixtureFactory.CreateNearDuplicate(archived);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        var first = Path.Combine(sourceRoot, "copy-a.png");
        var second = Path.Combine(sourceRoot, "copy-b.png");
        await incoming.SaveAsPngAsync(first);
        await incoming.SaveAsPngAsync(second);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FileRouterTests.FakeRecycleBinService(canRecycle: false);
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, recycleBin),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        // Tidying the queue is never worth losing a file: without a Recycle Bin both copies stay
        // on disk and both get asked about.
        Assert.Equal(0, result.AutoKeptArchived);
        Assert.Equal(2, result.HeldForReview);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, (await store.LoadAsync()).ReviewQueue.Count);
    }
}
