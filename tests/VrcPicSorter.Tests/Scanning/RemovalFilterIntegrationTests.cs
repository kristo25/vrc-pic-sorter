using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class RemovalFilterIntegrationTests
{
    private const string Name = "Me_2026-10-03_20-23-10.000_prnt_de65e61a-b986-4072-ac4c-c4ed590d01f5.png";

    [Fact]
    public async Task InterruptedRelocationRestoresKeepsBeforeDuplicateAndFilterCleanup()
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var original = await fixture.Frame(VrcImageCategory.Prints, uncertain: true, archive: true);
        await fixture.Scanner.RescanArchiveFiltersAsync();
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue, r => r.Status == ReviewStatus.Pending);
        await fixture.Scanner.ResolveFilterReviewAsync(review.Id, true);
        var destinationRoot = fixture.Directory.GetPath("new-prints");
        Directory.CreateDirectory(destinationRoot);
        await fixture.Store.UpdateAsync(state =>
        {
            var keep = Assert.Single(state.FilterKeeps);
            state.ManualKeeps.Add(new(keep.Path, keep.Fingerprint, keep.FileSize, keep.LastWriteUtc));
            state.Settings.CategoryMappings.Single(m => m.Category == VrcImageCategory.Prints).ArchivePath = destinationRoot;
            return true;
        });
        fixture.Store.BeforeStateWrite = state =>
        {
            if (state.RelocationJournal.Count == 0) throw new IOException("Interrupted before atomic rebase");
        };
        await ArchiveRelocation.RelocateAsync(fixture.Store,
            [new(VrcImageCategory.Prints, fixture.Archive(VrcImageCategory.Prints), destinationRoot, 1, new FileInfo(original).Length)]);
        using var reopened = new JsonStateStore(fixture.Store.StateDirectory);
        Assert.Empty(await ArchiveRelocation.RecoverAsync(reopened));
        var moved = Path.Combine(destinationRoot, Name);
        var extra = Path.Combine(destinationRoot, "extra.png");
        File.Copy(moved, extra);
        var decoder = new ImageDecoder();
        var scanner = new ScanCoordinator(reopened, new ArchiveIndexer(reopened, decoder), decoder,
            new FileRouter(reopened, decoder, fixture.Recycle), TimeSpan.Zero);
        var cleanup = await scanner.CleanArchiveDuplicatesAsync();
        Assert.Empty(cleanup.Warnings);
        Assert.True(File.Exists(moved));
        var filtered = await scanner.RescanArchiveFiltersAsync();
        Assert.Empty(filtered.Errors);
        Assert.True(File.Exists(moved));
        Assert.Equal(0, filtered.ReviewNeeded);
        Assert.Equal(moved, Assert.Single((await reopened.LoadAsync()).ManualKeeps).Path);
    }

    [Fact]
    public async Task PrintFilterIsCategoryScopedAndRecyclesWholeFileWithoutCounterpart()
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        foreach (var category in Enum.GetValues<VrcImageCategory>())
        {
            var path = await fixture.Frame(category);
            var hash = Hash(path);
            var result = await fixture.Scanner.ScanCategoryAsync(category);
            Assert.Empty(result.Errors);
            Assert.False(File.Exists(path));
            if (category == VrcImageCategory.Prints)
            {
                Assert.Equal(1, result.FilterRecycled);
                Assert.Equal(hash, Hash(Assert.Single(fixture.Recycle.Copies)));
                Assert.Empty(Directory.GetFiles(fixture.Archive(category), "*.png", SearchOption.AllDirectories));
            }
            else
            {
                Assert.Equal(0, result.FilterRecycled);
                Assert.Equal(hash, Hash(Assert.Single(Directory.GetFiles(fixture.Archive(category), "*.png", SearchOption.AllDirectories))));
            }
        }
        Assert.Single(fixture.Recycle.Copies);
    }

    [Fact]
    public async Task BothReasonsRecycleOnceAndRecordBoth()
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, true);
        await fixture.Frame(VrcImageCategory.Prints);
        var result = await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.FilterRecycled);
        Assert.Single(fixture.Recycle.Copies);
        var state = await fixture.Store.LoadAsync();
        var entry = Assert.Single(state.History, item => item.Message.StartsWith("Recycled by removal filters:"));
        Assert.Contains("PrintFrame", entry.Message);
        Assert.Contains("PlayerName", entry.Message);
        Assert.DoesNotContain(state.OperationJournal, item => item.Phase != JournalPhase.Completed);
    }

    [Fact]
    public async Task BothOffRetainsFrameAndExplicitRescanUsesOnlyCurrentArchive()
    {
        using var fixture = new Fixture();
        await fixture.Configure(false, false);
        var input = await fixture.Frame(VrcImageCategory.Prints);
        var hash = Hash(input);
        Assert.Equal(1, (await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints)).MovedUnique);
        var archived = Assert.Single(Directory.GetFiles(fixture.Archive(VrcImageCategory.Prints), "*.png"));
        Assert.Equal(hash, Hash(archived));
        var retained = fixture.Directory.GetPath("retained");
        Directory.CreateDirectory(retained);
        var retainedImage = Path.Combine(retained, Name);
        File.Copy(archived, retainedImage);
        await fixture.Store.UpdateAsync(state =>
        {
            state.Settings.RemovePrintFrame = true;
            state.Settings.LegacyArchiveMappings.Add(new() { Category = VrcImageCategory.Prints, ArchivePath = retained });
            return true;
        });
        var result = await fixture.Scanner.RescanArchiveFiltersAsync();
        Assert.Equal(1, result.Recycled);
        Assert.Empty(result.Errors);
        Assert.Equal(hash, Hash(retainedImage));
    }

    [Fact]
    public async Task UncertainKeepSurvivesRestartAndFollowsIncomingArchiveMove()
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        await fixture.Frame(VrcImageCategory.Prints, uncertain: true);
        Assert.Equal(1, (await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints)).HeldForReview);
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue, r => r.Status == ReviewStatus.Pending);
        Assert.False(review.FilterReview!.IsArchive);
        await fixture.Scanner.ResolveFilterReviewAsync(review.Id, true);
        Assert.Empty(fixture.Recycle.Copies);
        var archived = Assert.Single(Directory.GetFiles(fixture.Archive(VrcImageCategory.Prints), "*.png"));
        using var reloaded = new JsonStateStore(fixture.Directory.GetPath("state"));
        var state = await reloaded.LoadAsync();
        Assert.Equal(archived, Assert.Single(state.FilterKeeps).Path);
        var decoder = new ImageDecoder();
        var scanner = new ScanCoordinator(reloaded, new ArchiveIndexer(reloaded, decoder), decoder, new FileRouter(reloaded, decoder, fixture.Recycle));
        var result = await scanner.RescanArchiveFiltersAsync();
        Assert.Equal(0, result.Recycled);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.True(File.Exists(archived));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrLockedReviewCannotRecycle(bool locked)
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var path = await fixture.Frame(VrcImageCategory.Prints, uncertain: true, archive: true);
        Assert.Equal(1, (await fixture.Scanner.RescanArchiveFiltersAsync()).ReviewNeeded);
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue, r => r.FilterReview is not null);
        FileStream? handle = null;
        try
        {
            if (locked) handle = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else { using var replacement = ImageFixtureFactory.CreatePattern(88); await replacement.SaveAsPngAsync(path); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scanner.ResolveFilterReviewAsync(review.Id, false));
            Assert.Empty(fixture.Recycle.Copies);
            Assert.True(File.Exists(path));
        }
        finally { handle?.Dispose(); }
    }

    [Fact]
    public async Task ArchiveReviewRemovalPreservesCompanionAndRejectsRepeat()
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var path = await fixture.Frame(VrcImageCategory.Prints, uncertain: true, archive: true);
        var companion = Path.Combine(fixture.Archive(VrcImageCategory.Prints), Name.Replace(".000_", ".388_"));
        using (var image = ImageFixtureFactory.CreatePattern(61)) await image.SaveAsPngAsync(companion);
        var hash = Hash(companion);
        Assert.Equal(1, (await fixture.Scanner.RescanArchiveFiltersAsync()).ReviewNeeded);
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue, r => r.FilterReview is not null);
        Assert.True(review.FilterReview!.IsArchive);
        await fixture.Scanner.ResolveFilterReviewAsync(review.Id, false);
        Assert.False(File.Exists(path));
        Assert.Equal(hash, Hash(companion));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scanner.ResolveFilterReviewAsync(review.Id, false));
        Assert.Single(fixture.Recycle.Copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRecycleRecoversWithoutRepeatingCompletedRemoval(bool cancel)
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var first = await fixture.Frame(VrcImageCategory.Prints, archive: true);
        var hash = Hash(first);
        var second = Path.Combine(fixture.Archive(VrcImageCategory.Prints), "Z" + Name);
        File.Copy(first, second);
        using var cancellation = new CancellationTokenSource();
        fixture.Recycle.AfterEffect = () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("Simulated interruption after recycling");
        };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Scanner.RescanArchiveFiltersAsync(cancellationToken: cancellation.Token));
        else
        {
            // Stop enumeration after the first interrupted operation, so a second removal cannot hide a duplicate retry.
            fixture.Recycle.AfterEffect = () => { cancellation.Cancel(); throw new IOException("Simulated process interruption"); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Scanner.RescanArchiveFiltersAsync(cancellationToken: cancellation.Token));
        }
        Assert.Single(fixture.Recycle.Copies);
        Assert.Equal(hash, Hash(fixture.Recycle.Copies[0]));
        Assert.True(File.Exists(second));
        fixture.Recycle.AfterEffect = null;
        await fixture.Router.RecoverPendingOperationsAsync();
        await fixture.Router.RecoverPendingOperationsAsync();
        Assert.Single(fixture.Recycle.Copies);
        Assert.All((await fixture.Store.LoadAsync()).OperationJournal, entry => Assert.Equal(JournalPhase.Completed, entry.Phase));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RescanReleasesObsoleteFilterReview(bool archive, bool disable)
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var path = await fixture.Frame(VrcImageCategory.Prints, uncertain: true, archive: archive);
        if (archive) await fixture.Scanner.RescanArchiveFiltersAsync();
        else await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints);
        Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue, r => r.Status == ReviewStatus.Pending);
        using (var replacement = ImageFixtureFactory.CreatePattern(23)) await replacement.SaveAsPngAsync(path);
        if (disable) await fixture.Configure(false, false);
        if (archive) await fixture.Scanner.RescanArchiveFiltersAsync();
        else await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints);
        Assert.DoesNotContain((await fixture.Store.LoadAsync()).ReviewQueue, r => r.Status != ReviewStatus.Resolved);
        Assert.Empty(fixture.Recycle.Copies);
        Assert.True(File.Exists(Path.Combine(fixture.Archive(VrcImageCategory.Prints), Name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptImageIsReportedAndRemainingFilesAreProcessed(bool archive)
    {
        using var fixture = new Fixture();
        await fixture.Configure(true, false);
        var good = await fixture.Frame(VrcImageCategory.Prints, archive: archive);
        var bad = Path.Combine(Path.GetDirectoryName(good)!, "000-corrupt.png");
        await File.WriteAllTextAsync(bad, "not an image");
        if (archive)
        {
            var result = await fixture.Scanner.RescanArchiveFiltersAsync();
            Assert.Equal(1, result.Failed);
            Assert.Equal(1, result.Recycled);
        }
        else
        {
            var result = await fixture.Scanner.ScanCategoryAsync(VrcImageCategory.Prints);
            Assert.Single(result.Errors);
            Assert.Equal(1, result.FilterRecycled);
        }
        Assert.True(File.Exists(bad));
        Assert.False(File.Exists(good));
    }

    private sealed class Fixture : IDisposable
    {
        internal TestDirectory Directory { get; } = new();
        internal JsonStateStore Store { get; }
        internal RecordingRecycle Recycle { get; }
        internal FileRouter Router { get; }
        internal ScanCoordinator Scanner { get; }
        internal Fixture()
        {
            Store = FileRouterTests.CreateStore(Directory, Source(VrcImageCategory.Emoji), Archive(VrcImageCategory.Emoji));
            Recycle = new(Directory.GetPath("recycled"));
            var decoder = new ImageDecoder();
            Router = new(Store, decoder, Recycle);
            Scanner = new(Store, new ArchiveIndexer(Store, decoder), decoder, Router, TimeSpan.Zero);
        }
        internal string Source(VrcImageCategory category) => Directory.GetPath("source", category.ToString());
        internal string Archive(VrcImageCategory category) => Directory.GetPath("output", category.ToString());
        internal Task Configure(bool frame, bool player) => Store.UpdateAsync(state =>
        {
            state.Settings.RemovePrintFrame = frame; state.Settings.RemoveMyImages = player; state.Settings.PlayerName = "Me";
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                mapping.SourcePath = Source(mapping.Category); mapping.ArchivePath = Archive(mapping.Category); mapping.IsEnabled = true;
                System.IO.Directory.CreateDirectory(mapping.SourcePath); System.IO.Directory.CreateDirectory(mapping.ArchivePath);
            }
            return true;
        });
        internal async Task<string> Frame(VrcImageCategory category, bool uncertain = false, bool archive = false)
        {
            var decoded = RemovalDetectorTests.Frame(false, uncertain ? "02/40/2024" : "12/11/2024");
            var path = Path.Combine(archive ? Archive(category) : Source(category), Name);
            using var image = Image.LoadPixelData<Rgba32>(decoded.Frames[0].RgbaPixels, decoded.Width, decoded.Height);
            await image.SaveAsPngAsync(path);
            return path;
        }
        public void Dispose() { Store.Dispose(); Directory.Dispose(); }
    }

    private sealed class RecordingRecycle(string root) : IRecycleBinService
    {
        internal List<string> Copies { get; } = [];
        internal Action? AfterEffect { get; set; }
        public bool CanRecycle(string path) => true;
        public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            File.Move(path, destination);
            Copies.Add(destination);
            AfterEffect?.Invoke();
            return Task.CompletedTask;
        }
    }
}
