using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;
using SixLabors.ImageSharp;

namespace VrcPicSorter.Tests.Atlas;

public sealed class AtlasAnimationCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedDatedReferenceUsesItsOriginalArchive(bool existing)
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("old", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        using (var pixels = ImageFixtureFactory.CreatePattern(17, 64, 64))
            await pixels.SaveAsPngAsync(path);
        var reference = Path.Combine(root, "Animated", "Gif Ref", "2025-09", Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        File.Move(path, reference);
        var destination = Path.Combine(root, "Animated", "2025-09", Path.GetFileNameWithoutExtension(path) + ".gif");
        if (existing)
            Assert.True((await new AtlasAnimationWriter().TryWriteAsync(reference, root)).Exported);
        await store.UpdateAsync(state =>
        {
            state.ArchiveIndex.Categories.Single(x => x.Category == VrcImageCategory.Emoji).Images.Single().Path = reference;
            state.Settings.CategoryMappings.Single(x => x.Category == VrcImageCategory.Emoji).ArchivePath = directory.GetPath("new", "Emoji");
            state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping { Category = VrcImageCategory.Emoji, ArchivePath = root });
            return true;
        });
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());
        Assert.Equal(root, sheet.ArchiveRoot);
        Assert.Equal(destination, sheet.AnimationPath);
        var result = await catalog.ExportMissingAsync(sheet);
        Assert.True(result.Exported, result.Warning);
        Assert.Equal(existing, result.ReusedExisting);
        Assert.Equal(destination, result.Path);
        Assert.Single(Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LockedRetainedGifReportsAnIncompleteCatalogInsteadOfAnEmptyQueue()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        var retained = Path.ChangeExtension(AtlasAnimationWriter.BuildDestination(path, root), null) + " (2).gif";
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        await File.WriteAllBytesAsync(retained, [1, 2, 3]);
        using var locked = new FileStream(retained, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new AtlasAnimationCatalog(store).ListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedNumberedAnimationDoesNotReturnToTheMissingQueue(bool indexed)
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        using (var pixels = ImageFixtureFactory.CreatePattern(17, 64, 64))
            await pixels.SaveAsPngAsync(path);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());
        var first = await catalog.ExportAsync(sheet, sheet.Name);
        Assert.True(first.Exported, first.Warning);
        var retained = Path.ChangeExtension(first.Path, null) + " (2).gif";
        File.Move(first.Path!, retained);
        if (indexed)
            await store.UpdateAsync(state =>
            {
                state.ArchiveIndex.Categories.Single(x => x.Category == VrcImageCategory.Emoji).Images.Add(
                    new IndexedImageRecord
                    {
                        Id = Guid.NewGuid(),
                        Category = VrcImageCategory.Emoji,
                        Path = retained,
                        ExactFingerprint = "retained"
                    });
                return true;
            });
        var listed = Assert.Single(await catalog.ListAsync());
        Assert.False(listed.NeedsDecision);
        Assert.Equal(retained, listed.AnimationPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingExportRechecksDiskAndPreservesTheRetainedGif(bool filed)
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        using (var pixels = ImageFixtureFactory.CreatePattern(17, 64, 64))
            await pixels.SaveAsPngAsync(path);
        if (filed)
        {
            var reference = AtlasAnimationWriter.BuildReferenceDestination(path, root);
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            File.Move(path, reference);
            await store.UpdateAsync(state =>
            {
                state.ArchiveIndex.Categories.Single(x => x.Category == VrcImageCategory.Emoji).Images.Single().Path = reference;
                return true;
            });
        }
        var catalog = new AtlasAnimationCatalog(store);
        var stale = Assert.Single(await catalog.ListAsync());
        var first = await catalog.ExportAsync(stale, stale.Name);
        Assert.True(first.Exported, first.Warning);
        var retained = Path.ChangeExtension(first.Path, null) + " (3).gif";
        File.Move(first.Path!, retained);
        var bytes = await File.ReadAllBytesAsync(retained);

        for (var i = 0; i < 2; i++)
        {
            var result = await catalog.ExportMissingAsync(stale);
            Assert.True(result.ReusedExisting);
            Assert.Equal(retained, result.Path);
            Assert.Single(Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(retained));
        }
        Assert.False((await catalog.ExportAsync(stale, stale.Name)).Exported);
        var current = Assert.Single(await catalog.ListAsync());
        var corrected = await catalog.ExportAsync(current, current.Name with { FramesPerSecond = 20 });
        Assert.True(corrected.Exported, corrected.Warning);
        Assert.Equal(retained, corrected.Path);
        var correctedBytes = await File.ReadAllBytesAsync(retained);
        Assert.False(bytes.SequenceEqual(correctedBytes));
        Assert.Single(Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories));
        var staleReplacement = await catalog.ExportAsync(current, current.Name);
        Assert.False(staleReplacement.Exported);
        Assert.Contains("changed", staleReplacement.Warning);
        Assert.Equal(correctedBytes, await File.ReadAllBytesAsync(retained));
        File.Delete(retained);
        Assert.False((await catalog.ExportAsync(current, current.Name)).Exported);
        Assert.True((await catalog.ExportMissingAsync(stale)).Exported);
    }

    [Fact]
    public async Task CorruptRetainedGifIsNotOverwrittenOrDuplicated()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());
        var retained = Path.ChangeExtension(AtlasAnimationWriter.BuildDestination(path, root), null) + " (2).gif";
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        await File.WriteAllTextAsync(retained, "not a gif");
        var result = await catalog.ExportMissingAsync(sheet);
        Assert.False(result.Exported);
        Assert.Contains("could not be read", result.Warning);
        Assert.Equal("not a gif", await File.ReadAllTextAsync(retained));
        Assert.Single(Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnotherEmojiDoesNotSuppressAnExport()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        using (var pixels = ImageFixtureFactory.CreatePattern(17, 64, 64))
        {
            await pixels.SaveAsPngAsync(path);
            await pixels.SaveAsGifAsync(Path.Combine(root, "unrelated.gif"));
        }
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());
        Assert.True(sheet.NeedsDecision);
        var result = await catalog.ExportMissingAsync(sheet);
        Assert.True(result.Exported, result.Warning);
        Assert.False(result.ReusedExisting);
        Assert.Equal(2, Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task RedirectedAnimationFolderIsNeverWrittenThrough()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, root, "a", out var path);
        var sheet = Assert.Single(await new AtlasAnimationCatalog(store).ListAsync());
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(root, "Animated");
        Assert.True(await PathBoundaryTests.TryCreateJunctionAsync(linked, outside));
        try
        {
            var result = await new AtlasAnimationCatalog(store).ExportMissingAsync(sheet);
            Assert.False(result.Exported);
            Assert.NotNull(result.Warning);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Fact]
    public async Task SkippingASheetTakesItOutOfTheQueueWithoutTouchingIt()
    {
        using var directory = new TestDirectory();
        var archiveRoot = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, archiveRoot, "a", out var sheetPath);
        var catalog = new AtlasAnimationCatalog(store);

        var listed = Assert.Single(await catalog.ListAsync());
        Assert.True(listed.NeedsDecision);
        Assert.False(listed.IsSkipped);

        Assert.Equal(1, await catalog.SkipAsync([listed]));

        var skipped = Assert.Single(await catalog.ListAsync());
        Assert.True(skipped.IsSkipped);
        Assert.False(skipped.NeedsDecision);
        Assert.Contains("skipped", skipped.Summary, StringComparison.Ordinal);
        // A skip records a decision and nothing else. The sheet stays exactly where it was.
        Assert.Equal(sheetPath, skipped.AtlasPath);
    }

    [Fact]
    public async Task SkippingTwiceChangesNothingAndRestoringPutsItBack()
    {
        using var directory = new TestDirectory();
        using var store = CreateStoreWithSheet(directory, directory.GetPath("archive", "Emoji"), "a", out _);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());

        Assert.Equal(1, await catalog.SkipAsync([sheet]));
        Assert.Equal(0, await catalog.SkipAsync([sheet]));

        var skipped = Assert.Single(await catalog.ListAsync());
        Assert.Equal(1, await catalog.RestoreAsync([skipped]));
        Assert.True(Assert.Single(await catalog.ListAsync()).NeedsDecision);
    }

    [Fact]
    public async Task ASkipSurvivesTheIndexBeingRebuiltAndTheSheetBeingFiled()
    {
        // This is why skips are keyed by fingerprint rather than by index id or path: a rebuild
        // mints a new id for every record, and filing a sheet beside its animation moves it. Keyed
        // by either of those, every skip would come undone the first time the archive was rebuilt.
        using var directory = new TestDirectory();
        var archiveRoot = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, archiveRoot, "a", out var sheetPath);
        var catalog = new AtlasAnimationCatalog(store);
        await catalog.SkipAsync([Assert.Single(await catalog.ListAsync())]);

        var filed = Path.Combine(archiveRoot, "Animated", "Gif Ref", Path.GetFileName(sheetPath));
        await store.UpdateAsync(state =>
        {
            var index = state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji);
            index.Images.Clear();
            index.Images.Add(new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                Path = filed,
                ExactFingerprint = "fingerprint-a",
            });
            return true;
        });

        Assert.True(Assert.Single(await catalog.ListAsync()).IsSkipped);
    }

    [Theory]
    // Only the ceiling is a real clamp. Rates that merely land on a neighbouring hundredth used to
    // report themselves as clamped, which was nearly every sheet in a real archive.
    [InlineData(31, false)]
    [InlineData(30, false)]
    [InlineData(17, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(64, true)]
    public void OnlyARateTooFastForGifCountsAsClamped(int framesPerSecond, bool clamped)
    {
        var sheet = new ArchivedSheet(
            Guid.NewGuid(),
            VrcImageCategory.Emoji,
            "x.png",
            "root",
            "x.gif",
            false,
            new EmojiAtlasName(4, framesPerSecond, AtlasLoopStyle.Linear));

        Assert.Equal(clamped, sheet.RateWasClamped);
    }

    private static JsonStateStore CreateStoreWithSheet(
        TestDirectory directory,
        string archiveRoot,
        string key,
        out string sheetPath)
    {
        Directory.CreateDirectory(archiveRoot);
        sheetPath = Path.Combine(archiveRoot, $"player_{key}_4frames_10fps_linearloopStyle.png");
        var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), archiveRoot);
        var path = sheetPath;
        store.UpdateAsync(state =>
        {
            var index = state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji);
            index.Images.Add(new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                Path = path,
                ExactFingerprint = $"fingerprint-{key}",
            });
            return true;
        }).GetAwaiter().GetResult();
        return store;
    }
}
