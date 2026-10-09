using SixLabors.ImageSharp;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class RemovalFilterTests
{
    [Fact]
    public async Task FilterReviewKeepContinuesIncomingAndExceptionFollowsItsMove()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source"); var archive = directory.GetPath("Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        var path = Path.Combine(source, "Me_2026-10-03_20-23-10.388_prnt_de65e61a-b986-4072-ac4c-c4ed590d01f5.png");
        using var image = ImageFixtureFactory.CreatePattern(43); await image.SaveAsPngAsync(path);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var id = Guid.NewGuid();
        await store.UpdateAsync(s =>
        {
            s.Settings.RemoveMyImages = true; s.Settings.PlayerName = "Me";
            s.ReviewQueue.Add(new ReviewItem
            {
                Id = id,
                Category = VrcImageCategory.Emoji,
                IncomingOriginalPath = path,
                HeldFilePath = path,
                IncomingFingerprint = fingerprint.ExactIdentity,
                IncomingImageFingerprint = fingerprint,
                FilterReview = new() { Reasons = [RemovalFilterReason.PlayerName] }
            });
            return true;
        });
        var decoder = new ImageDecoder(); var recycle = new FileRouterTests.FakeRecycleBinService();
        var scanner = new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder, new FileRouter(store, decoder, recycle));
        await scanner.ResolveFilterReviewAsync(id, true);
        Assert.Empty(recycle.RecycledPaths); Assert.False(File.Exists(path));
        var target = Assert.Single(Directory.GetFiles(archive, "*.png", SearchOption.AllDirectories));
        var saved = await store.LoadAsync();
        Assert.Equal(target, Assert.Single(saved.FilterKeeps).Path);
        Assert.True(FilterKeepRules.IsKept(saved, target, fingerprint.ExactIdentity, RemovalFilterReason.PlayerName));
        Assert.Equal(0, (await scanner.RescanArchiveFiltersAsync()).Recycled);
        using var changed = ImageFixtureFactory.CreatePattern(99); await changed.SaveAsPngAsync(target);
        Assert.Equal(1, (await scanner.RescanArchiveFiltersAsync()).Recycled);
    }

    [Fact]
    public async Task ExplicitArchiveFilterReviewRecyclesOnceAndRejectsRepeatedClicks()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source"); var archive = directory.GetPath("Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        var path = Path.Combine(archive, "frame.png");
        using var image = ImageFixtureFactory.CreatePattern(43); await image.SaveAsPngAsync(path);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var id = Guid.NewGuid();
        await store.UpdateAsync(s =>
        {
            s.ReviewQueue.Add(new ReviewItem
            {
                Id = id,
                Category = VrcImageCategory.Emoji,
                IncomingOriginalPath = path,
                HeldFilePath = path,
                IncomingFingerprint = fingerprint.ExactIdentity,
                IncomingImageFingerprint = fingerprint,
                FilterReview = new() { IsArchive = true, Reasons = [RemovalFilterReason.PrintFrame] }
            });
            return true;
        });
        var decoder = new ImageDecoder(); var recycle = new FileRouterTests.FakeRecycleBinService();
        var scanner = new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder, new FileRouter(store, decoder, recycle));
        await scanner.ResolveFilterReviewAsync(id, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ResolveFilterReviewAsync(id, false));
        Assert.Single(recycle.RecycledPaths); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(true, "My_Name", true)]
    [InlineData(false, "My_Name", false)]
    [InlineData(true, "my_name", false)]
    [InlineData(true, "My", false)]
    [InlineData(true, "", false)]
    public async Task IncomingExactPlayerFilterRunsBeforeArchiving(bool enabled, string name, bool removed)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source"); var archive = directory.GetPath("Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        var path = Path.Combine(source, "My_Name_2026-10-03_20-23-10.388_prnt_de65e61a-b986-4072-ac4c-c4ed590d01f5.png");
        using var image = ImageFixtureFactory.CreatePattern(43);
        await image.SaveAsPngAsync(path);
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        await store.UpdateAsync(s => { s.Settings.RemoveMyImages = enabled; s.Settings.PlayerName = name; return true; });
        var decoder = new ImageDecoder(); var recycle = new FileRouterTests.FakeRecycleBinService();
        var router = new FileRouter(store, decoder, recycle);
        var scanner = new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder, router, TimeSpan.Zero);
        var result = await scanner.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Empty(result.Errors);
        Assert.Equal(removed ? 1 : 0, result.FilterRecycled);
        Assert.Equal(removed ? 1 : 0, recycle.RecycledPaths.Count);
        Assert.Equal(removed ? 0 : 1, Directory.GetFiles(archive, "*.png", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task ArchiveFilterOverridesManualKeepButRespectsContentBoundFilterException()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source"); var archive = directory.GetPath("Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        var path = Path.Combine(archive, "Me_2026-10-03_20-23-10.388_prnt_de65e61a-b986-4072-ac4c-c4ed590d01f5.png");
        using var image = ImageFixtureFactory.CreatePattern(43);
        await image.SaveAsPngAsync(path);
        var identity = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image)).ExactIdentity;
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var info = new FileInfo(path);
        await store.UpdateAsync(s =>
        {
            s.Settings.RemoveMyImages = true; s.Settings.PlayerName = "Me";
            s.ManualKeeps.Add(new(path, identity, info.Length, info.LastWriteTimeUtc));
            s.FilterKeeps.Add(new(path, identity, info.Length, info.LastWriteTimeUtc, RemovalFilterReason.PlayerName));
            return true;
        });
        var decoder = new ImageDecoder(); var recycle = new FileRouterTests.FakeRecycleBinService();
        var scanner = new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder, new FileRouter(store, decoder, recycle));
        Assert.Equal(0, (await scanner.RescanArchiveFiltersAsync()).Recycled);
        Assert.True(File.Exists(path));
        await store.UpdateAsync(s => { s.FilterKeeps.Clear(); return true; });
        Assert.Equal(1, (await scanner.RescanArchiveFiltersAsync()).Recycled);
        Assert.Single(recycle.RecycledPaths);
        Assert.Empty((await store.LoadAsync()).ManualKeeps);
    }

    [Fact]
    public async Task FailedRecycleNeverDeletesFileAndRecoveryRechecksDisabledFilter()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source"); var archive = directory.GetPath("Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        var path = Path.Combine(source, "Me_2026-10-03_20-23-10.388_prnt_de65e61a-b986-4072-ac4c-c4ed590d01f5.png");
        using var image = ImageFixtureFactory.CreatePattern(43); await image.SaveAsPngAsync(path);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        await store.UpdateAsync(s => { s.Settings.RemoveMyImages = true; s.Settings.PlayerName = "Me"; return true; });
        var decoder = new ImageDecoder();
        var router = new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService(throwOnRecycle: true));
        await Assert.ThrowsAsync<IOException>(() => router.RecycleFilteredAsync(path, VrcImageCategory.Emoji, fingerprint, [RemovalFilterReason.PlayerName]));
        Assert.True(File.Exists(path));
        await store.UpdateAsync(s => { s.Settings.RemoveMyImages = false; return true; });
        var recycle = new FileRouterTests.FakeRecycleBinService();
        await new FileRouter(store, decoder, recycle).RecoverPendingOperationsAsync();
        Assert.True(File.Exists(path)); Assert.Empty(recycle.RecycledPaths);
        Assert.Equal(JournalPhase.NeedsAttention, Assert.Single((await store.LoadAsync()).OperationJournal).Phase);
    }
}
