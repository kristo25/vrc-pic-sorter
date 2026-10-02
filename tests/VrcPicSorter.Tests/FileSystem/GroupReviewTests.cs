using SixLabors.ImageSharp;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class GroupReviewTests
{
    [Fact]
    public async Task CancelAfterArchivingKeepsDestinationAndDoesNotMoveIncomingAgain()
    {
        using var f = await Fixture.Create(2);
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Keep)));
        var archivedPath = Assert.Single((await f.Store.LoadAsync()).ReviewQueue).HeldFilePath;
        Assert.NotEqual(f.Incoming, archivedPath);
        await f.Router.CancelReviewDecisionAsync(f.Review.Id);
        var state = await f.Store.LoadAsync();
        var review = Assert.Single(state.ReviewQueue);
        Assert.Equal(archivedPath, review.KeptIncomingArchivedPath);
        Assert.True(File.Exists(archivedPath));
        Assert.Empty(f.Recycler.Removed);
        Assert.Contains(state.ManualKeeps, p => p.Path == archivedPath);
        await f.Router.ApplyReviewDecisionAsync(new(review.Id,
            [new(null, review.HeldFilePath, review.IncomingFingerprint, ReviewFileAction.Keep),
            .. review.Candidates.Select(c => new ReviewFileChoice(c.Id, c.ArchivePath, c.ExpectedFingerprint, ReviewFileAction.Keep))]));
        Assert.True(File.Exists(archivedPath));
        Assert.Equal(3, System.IO.Directory.GetFiles(f.Archive, "*.png", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task QueueRemovalCannotHidePendingDecision()
    {
        using var f = await Fixture.Create(1);
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(_ => ReviewFileAction.Unchanged, ReviewFileAction.Remove, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.DismissReviewAsync(f.Review.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.RestoreReviewAsync(f.Review));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.RestoreReviewsAsync([f.Review]));
        var state = await f.Store.LoadAsync();
        Assert.Single(state.ReviewQueue); Assert.Single(state.ReviewDecisions);
        Assert.True(File.Exists(f.Incoming)); Assert.Empty(f.Recycler.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupMakesHiddenPendingDecisionVisible(bool missing)
    {
        using var f = await Fixture.Create(1);
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(_ => ReviewFileAction.Unchanged, ReviewFileAction.Remove, true)));
        await f.Store.UpdateAsync(state =>
        {
            if (missing) state.ReviewQueue.Clear();
            else { state.ReviewQueue[0].Status = ReviewStatus.Resolved; state.ReviewQueue[0].HeldFilePath = ""; }
            return true;
        });
        await f.Router.RestorePendingReviewVisibilityAsync();
        var review = Assert.Single((await f.Store.LoadAsync()).ReviewQueue);
        Assert.Equal(f.Review.Id, review.Id); Assert.NotEqual(ReviewStatus.Resolved, review.Status);
        Assert.Equal(f.Incoming, review.HeldFilePath);
        await f.Router.CancelReviewDecisionAsync(review.Id);
        Assert.Empty((await f.Store.LoadAsync()).ReviewDecisions);
        Assert.Empty(f.Recycler.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelMatchDeletionReconcilesWithoutMoreRemovals(bool afterEffect)
    {
        using var f = await Fixture.Create(2);
        var target = f.Review.Candidates[0];
        var request = f.Request(c => c.Id == target.Id ? ReviewFileAction.Remove : ReviewFileAction.Unchanged, ReviewFileAction.Unchanged)
            with
        { MatchOnlyId = target.Id };
        f.Recycler.FailOnCall = 1; f.Recycler.FailAfterDelete = afterEffect;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(request));
        await f.Router.CancelReviewDecisionAsync(f.Review.Id);
        var state = await f.Store.LoadAsync();
        Assert.Empty(state.ReviewDecisions); Assert.Empty(state.OperationJournal);
        Assert.Equal(afterEffect ? 1 : 0, f.Recycler.Removed.Count);
        Assert.Equal(!afterEffect, File.Exists(target.ArchivePath));
        Assert.True(File.Exists(f.Incoming));
        Assert.Equal(afterEffect ? 1 : 2, Assert.Single(state.ReviewQueue).Candidates.Count);
        Assert.All(state.ReviewQueue[0].Candidates, c => Assert.False(c.IsStale));
        var current = state.ReviewQueue[0];
        await f.Router.ApplyReviewDecisionAsync(new(current.Id,
            [new(null, current.HeldFilePath, current.IncomingFingerprint, ReviewFileAction.Keep),
            .. current.Candidates.Select(c => new ReviewFileChoice(c.Id, c.ArchivePath, c.ExpectedFingerprint, ReviewFileAction.Keep))]));
        Assert.Empty((await f.Store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task CancelPartialDecisionWithChangedKeeperPreservesCompletedEffects()
    {
        using var f = await Fixture.Create(4);
        var keeper = f.Review.Candidates[0];
        f.Recycler.AfterDelete = _ => { File.WriteAllText(keeper.ArchivePath, "changed"); f.Recycler.AfterDelete = null; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(c => c.Id == keeper.Id ? ReviewFileAction.Keep : ReviewFileAction.Remove, ReviewFileAction.Remove)));
        using var changed = ImageFixtureFactory.CreatePattern(99);
        await changed.SaveAsPngAsync(keeper.ArchivePath);
        await f.Router.CancelReviewDecisionAsync(f.Review.Id);
        var state = await f.Store.LoadAsync();
        Assert.Empty(state.ReviewDecisions); Assert.Single(f.Recycler.Removed);
        Assert.True(File.Exists(f.Incoming)); Assert.Single(state.ReviewQueue);
        Assert.Contains(state.ManualKeeps, p => p.Path == keeper.ArchivePath);
        var review = Assert.Single(state.ReviewQueue);
        var refreshed = review.Candidates.Single(c => c.Id == keeper.Id);
        Assert.False(refreshed.IsStale);
        Assert.NotEqual(keeper.ExpectedFingerprint, refreshed.ExpectedFingerprint);
        Assert.Equal(0, refreshed.SimilarityScore);
        await f.Router.ApplyReviewDecisionAsync(new(review.Id,
            [new(null, review.HeldFilePath, review.IncomingFingerprint, ReviewFileAction.Keep),
            .. review.Candidates.Select(c => new ReviewFileChoice(c.Id, c.ArchivePath, c.ExpectedFingerprint, ReviewFileAction.Keep))]));
        Assert.Empty((await f.Store.LoadAsync()).ReviewQueue);
        Assert.Single(f.Recycler.Removed);
    }

    [Fact]
    public async Task CancelCannotForgetAnOperationOnUnavailableStorage()
    {
        using var f = await Fixture.Create(1);
        var target = f.Review.Candidates[0];
        var request = f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Unchanged) with { MatchOnlyId = target.Id };
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(request));
        var temporarilyMoved = f.Archive + "-offline";
        System.IO.Directory.Move(f.Archive, temporarilyMoved);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.CancelReviewDecisionAsync(f.Review.Id));
            var state = await f.Store.LoadAsync();
            Assert.Single(state.ReviewDecisions); Assert.Single(state.OperationJournal);
            Assert.Empty(f.Recycler.Removed);
        }
        finally { System.IO.Directory.Move(temporarilyMoved, f.Archive); }
    }

    [Fact]
    public async Task CancelAfterIncomingWasRecycledResolvesReview()
    {
        using var f = await Fixture.Create(1);
        f.Recycler.FailOnCall = 1; f.Recycler.FailAfterDelete = true;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(_ => ReviewFileAction.Unchanged, ReviewFileAction.Remove, true)));
        await f.Router.CancelReviewDecisionAsync(f.Review.Id);
        var state = await f.Store.LoadAsync();
        Assert.Empty(state.ReviewDecisions); Assert.Empty(state.ReviewQueue);
        Assert.Single(f.Recycler.Removed); Assert.True(File.Exists(f.Review.Candidates[0].ArchivePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task MatchKeepPreservesFileAndRemovesOnlyThatReviewReference(int count)
    {
        using var f = await Fixture.Create(count);
        var target = f.Review.Candidates[0];
        var other = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = f.Review.Category,
            HeldFilePath = "other",
            IncomingOriginalPath = "other",
            IncomingFingerprint = "other",
            Candidates = f.Review.Candidates.ToList()
        };
        await f.Store.UpdateAsync(s => { s.ReviewQueue.Add(other); return true; });
        var request = f.Request(c => c.Id == target.Id ? ReviewFileAction.Keep : ReviewFileAction.Unchanged, ReviewFileAction.Unchanged)
            with
        { MatchOnlyId = target.Id };
        var result = await f.Router.ApplyReviewDecisionAsync(request);
        Assert.False(result.ReviewResolved);
        Assert.True(File.Exists(target.ArchivePath)); Assert.True(File.Exists(f.Incoming));
        Assert.Empty(f.Recycler.Removed);
        using var reopened = new JsonStateStore(f.Store.StateDirectory, () => AppStateDefaults.Create(stateDirectoryPath: f.Store.StateDirectory));
        var state = await reopened.LoadAsync();
        Assert.Equal(count - 1, state.ReviewQueue.Single(r => r.Id == f.Review.Id).Candidates.Count);
        Assert.Equal(count, state.ReviewQueue.Single(r => r.Id == other.Id).Candidates.Count);
        Assert.Contains(state.ManualKeeps, keep => keep.Path == target.ArchivePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.ApplyReviewDecisionAsync(request));
        var remaining = state.ReviewQueue.Single(r => r.Id == f.Review.Id);
        var keepIncoming = new ReviewDecisionRequest(remaining.Id, [new(null, remaining.HeldFilePath, remaining.IncomingFingerprint, ReviewFileAction.Keep),
            .. remaining.Candidates.Select(c => new ReviewFileChoice(c.Id, c.ArchivePath, c.ExpectedFingerprint, ReviewFileAction.Keep))]);
        await f.Router.ApplyReviewDecisionAsync(keepIncoming);
        Assert.Empty(f.Recycler.Removed);
        Assert.All(f.Review.Candidates, c => Assert.True(File.Exists(c.ArchivePath)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task MatchDeleteRecyclesOnlySelectedFileAndCanRetry(int count, bool failAfterEffect)
    {
        using var f = await Fixture.Create(count);
        var target = f.Review.Candidates[^1];
        var request = f.Request(c => c.Id == target.Id ? ReviewFileAction.Remove : ReviewFileAction.Unchanged, ReviewFileAction.Unchanged)
            with
        { MatchOnlyId = target.Id };
        f.Recycler.FailOnCall = 1; f.Recycler.FailAfterDelete = failAfterEffect;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(request));
        Assert.Single((await f.Store.LoadAsync()).ReviewDecisions);
        f.Recycler.FailOnCall = 0;
        await f.Router.RecoverPendingOperationsAsync();
        var result = await f.Router.RetryReviewDecisionAsync(f.Review.Id);
        Assert.False(result.ReviewResolved);
        Assert.Equal(target.ArchivePath, Assert.Single(f.Recycler.Removed));
        Assert.True(File.Exists(f.Incoming));
        var remaining = Assert.Single((await f.Store.LoadAsync()).ReviewQueue);
        Assert.Equal(count - 1, remaining.Candidates.Count);
        Assert.All(remaining.Candidates, c => Assert.True(File.Exists(c.ArchivePath)));
    }

    [Fact]
    public async Task ExplicitRemovalOverridesProtectionAndAmbiguousGroupCannotBeDismissed()
    {
        using var f = await Fixture.Create(2);
        var remove = f.Review.Candidates[1];
        var info = new FileInfo(remove.ArchivePath);
        await f.Store.UpdateAsync(s =>
        {
            s.ManualKeeps.Add(new(remove.ArchivePath, remove.ExpectedFingerprint, info.Length, info.LastWriteTimeUtc));
            return true;
        });
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(
            f.Request(c => c.Id == remove.Id ? ReviewFileAction.Remove : ReviewFileAction.Keep, ReviewFileAction.Unchanged)));
        await f.Store.UpdateAsync(s => { s.OperationJournal[0].Phase = JournalPhase.NeedsAttention; return true; });
        Assert.Equal(0, await f.Router.DismissNeedsAttentionOperationsAsync());
        Assert.Single((await f.Store.LoadAsync()).OperationJournal);
        f.Recycler.FailOnCall = 0;
        await f.Router.RetryReviewDecisionAsync(f.Review.Id);
        Assert.False(File.Exists(remove.ArchivePath));
        Assert.DoesNotContain((await f.Store.LoadAsync()).ManualKeeps, p => p.Path == remove.ArchivePath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public async Task DeleteIncomingNeverTouchesArchivedMatches(int count)
    {
        using var fixture = await Fixture.Create(count);
        var request = fixture.Request(_ => ReviewFileAction.Unchanged, ReviewFileAction.Remove, true);
        var result = await fixture.Router.ApplyReviewDecisionAsync(request);
        Assert.True(result.ReviewResolved);
        Assert.False(File.Exists(fixture.Incoming));
        Assert.All(fixture.Review.Candidates, c => Assert.True(File.Exists(c.ArchivePath)));
        Assert.Empty((await fixture.Store.LoadAsync()).ReviewQueue);
        Assert.Single(fixture.Recycler.Removed);
    }

    [Fact]
    public async Task KeepOneOfFourRecyclesExactlyTheOtherFour()
    {
        using var f = await Fixture.Create(4);
        var keep = f.Review.Candidates[2];
        var request = f.Request(c => c.Id == keep.Id ? ReviewFileAction.Keep : ReviewFileAction.Remove, ReviewFileAction.Remove);
        Assert.Equal("Keep 1 · Recycle 4", request.Summary);
        await f.Router.ApplyReviewDecisionAsync(request);
        Assert.True(File.Exists(keep.ArchivePath));
        Assert.Equal(4, f.Recycler.Removed.Count);
        Assert.Equal(f.Incoming, f.Recycler.Removed[^1]);
        var state = await f.Store.LoadAsync();
        Assert.Single(state.ManualKeeps);
        Assert.Empty(state.ReviewDecisions);
        Assert.Empty(state.OperationJournal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.ApplyReviewDecisionAsync(request));
    }

    [Fact]
    public async Task KeepAllSurvivesRestartRebuildAndAutomaticCleanupButNewCopyDoesNot()
    {
        using var f = await Fixture.Create(4);
        await f.Router.ApplyReviewDecisionAsync(f.Request(_ => ReviewFileAction.Keep, ReviewFileAction.Keep));
        var state = await f.Store.LoadAsync();
        Assert.Equal(5, state.ManualKeeps.Count);
        using var reopened = new JsonStateStore(f.Store.StateDirectory, () => AppStateDefaults.Create(stateDirectoryPath: f.Store.StateDirectory));
        var decoder = new ImageDecoder();
        var indexer = new ArchiveIndexer(reopened, decoder);
        await indexer.RebuildAsync(VrcImageCategory.Emoji);
        var router = new FileRouter(reopened, decoder, f.Recycler);
        var scanner = new ScanCoordinator(reopened, indexer, decoder, router);
        Assert.Equal(0, (await scanner.CleanArchiveDuplicatesAsync()).Removed);
        var extra = Path.Combine(f.Archive, "new-copy.png");
        File.Copy(state.ManualKeeps[0].Path, extra);
        var cleanup = await scanner.CleanArchiveDuplicatesAsync();
        Assert.Empty(cleanup.Warnings);
        Assert.False(File.Exists(extra));
        Assert.All(state.ManualKeeps, keep => Assert.True(File.Exists(keep.Path)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task RetryHandlesFailureBeforeOrAfterRecycleWithoutRepeatingCompletedFiles(int failureCall, bool after)
    {
        using var f = await Fixture.Create(4);
        f.Recycler.FailOnCall = failureCall; f.Recycler.FailAfterDelete = after;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(f.Request(c => c == f.Review.Candidates[0] ? ReviewFileAction.Keep : ReviewFileAction.Remove, ReviewFileAction.Remove)));
        var pending = await f.Store.LoadAsync();
        Assert.Single(pending.ReviewQueue);
        Assert.Single(pending.ReviewDecisions);
        f.Recycler.FailOnCall = 0;
        var restartedRouter = new FileRouter(f.Store, new ImageDecoder(), f.Recycler);
        await restartedRouter.RecoverPendingOperationsAsync();
        var result = await restartedRouter.RetryReviewDecisionAsync(f.Review.Id);
        Assert.True(result.ReviewResolved);
        Assert.Equal(4, f.Recycler.Removed.Count);
        Assert.Equal(4, f.Recycler.Removed.Distinct().Count());
    }

    [Fact]
    public async Task ChangedKeeperStopsRemainingRemovals()
    {
        using var f = await Fixture.Create(4);
        var keeper = f.Review.Candidates[0].ArchivePath;
        f.Recycler.AfterDelete = _ => { File.WriteAllText(keeper, "changed"); f.Recycler.AfterDelete = null; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.ApplyReviewDecisionAsync(f.Request(c => c == f.Review.Candidates[0] ? ReviewFileAction.Keep : ReviewFileAction.Remove, ReviewFileAction.Remove)));
        Assert.Single(f.Recycler.Removed);
        Assert.True(File.Exists(f.Incoming));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.RetryReviewDecisionAsync(f.Review.Id));
        Assert.Single(f.Recycler.Removed);
    }

    [Fact]
    public async Task KeepingIncomingArchivesBeforeRecyclingAndRefreshesOtherReviews()
    {
        using var f = await Fixture.Create(4);
        var other = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = f.Review.Category,
            IncomingOriginalPath = "other",
            HeldFilePath = "other",
            IncomingFingerprint = "other",
            Candidates = f.Review.Candidates.ToList()
        };
        await f.Store.UpdateAsync(s => { s.ReviewQueue.Add(other); return true; });
        f.Recycler.AfterDelete = _ => Assert.False(File.Exists(f.Incoming));
        await f.Router.ApplyReviewDecisionAsync(f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Keep));
        var state = await f.Store.LoadAsync();
        Assert.Single(state.ManualKeeps);
        Assert.True(File.Exists(state.ManualKeeps[0].Path));
        Assert.NotNull(state.ArchiveIndex.Categories.Single(c => c.Category == f.Review.Category).Images.Single().Fingerprint);
        Assert.Empty(Assert.Single(state.ReviewQueue).Candidates);
        Assert.Equal(4, f.Recycler.Removed.Count);
    }

    [Fact]
    public async Task FailureAfterArchivingIncomingRetriesFromItsNewPath()
    {
        using var f = await Fixture.Create(2);
        f.Recycler.FailOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Router.ApplyReviewDecisionAsync(f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Keep)));
        var state = await f.Store.LoadAsync();
        var group = Assert.Single(state.ReviewDecisions);
        Assert.True(group.Steps[0].Completed);
        Assert.False(File.Exists(f.Incoming));
        var destination = group.Files.Single(c => c.CandidateId is null).Path;
        Assert.True(File.Exists(destination));
        Assert.Equal(destination, Assert.Single(state.ManualKeeps).Path);
        f.Recycler.FailOnCall = 0;
        await new FileRouter(f.Store, new ImageDecoder(), f.Recycler).RetryReviewDecisionAsync(f.Review.Id);
        Assert.True(File.Exists(destination));
        Assert.Equal(2, f.Recycler.Removed.Count);
        Assert.Empty((await f.Store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task UnchangedIncomingStaysPendingAndStaleRequestsDoNothing()
    {
        using var f = await Fixture.Create(2);
        var request = f.Request(c => c == f.Review.Candidates[0] ? ReviewFileAction.Keep : ReviewFileAction.Remove, ReviewFileAction.Unchanged);
        var result = await f.Router.ApplyReviewDecisionAsync(request);
        Assert.False(result.ReviewResolved);
        Assert.True(File.Exists(f.Incoming));
        Assert.Single((await f.Store.LoadAsync()).ReviewQueue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Router.ApplyReviewDecisionAsync(request));
        Assert.Single(f.Recycler.Removed);
    }

    [Fact]
    public async Task CancellationPreservesRetryAndUnsupportedRecyclingNeverDeletes()
    {
        using var f = await Fixture.Create(2);
        var request = f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Keep);
        f.Recycler.CanUse = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => f.Router.ApplyReviewDecisionAsync(request));
        Assert.True(File.Exists(f.Incoming)); Assert.Empty(f.Recycler.Removed);
        f.Recycler.CanUse = true;
        using var cancellation = new CancellationTokenSource();
        f.Recycler.AfterDelete = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Router.ApplyReviewDecisionAsync(request, cancellation.Token));
        f.Recycler.AfterDelete = null;
        await f.Router.RetryReviewDecisionAsync(f.Review.Id);
        Assert.Equal(2, f.Recycler.Removed.Count);
    }

    [Fact]
    public async Task EmptyAllRemoveAndAliasedSelectionsAreRejected()
    {
        using var f = await Fixture.Create(1);
        Assert.Throws<InvalidOperationException>(() => f.Request(_ => ReviewFileAction.Unchanged, ReviewFileAction.Unchanged).Validate());
        Assert.Throws<InvalidOperationException>(() => f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Remove).Validate());
        var request = f.Request(_ => ReviewFileAction.Remove, ReviewFileAction.Keep);
        Assert.Throws<InvalidOperationException>(() => (request with { Files = [request.Files[0], request.Files[1] with { Path = f.Incoming }] }).Validate());
        Assert.Empty(f.Recycler.Removed);
    }

    private sealed class Fixture : IDisposable
    {
        public TestDirectory Directory { get; }
        public required JsonStateStore Store { get; init; }
        public required ReviewItem Review { get; init; }
        public required FileRouter Router { get; init; }
        public required RecordingRecycler Recycler { get; init; }
        public required string Incoming { get; init; }
        public required string Archive { get; init; }
        private Fixture(TestDirectory directory) => Directory = directory;
        public ReviewDecisionRequest Request(Func<ReviewCandidate, ReviewFileAction> choice, ReviewFileAction incoming, bool deleteOnly = false) =>
            new(Review.Id, [new(null, Incoming, Review.IncomingFingerprint, incoming), .. Review.Candidates.Select(c => new ReviewFileChoice(c.Id, c.ArchivePath, c.ExpectedFingerprint, choice(c)))], deleteOnly);
        public static async Task<Fixture> Create(int count)
        {
            var directory = new TestDirectory();
            var source = directory.GetPath("incoming"); var archive = directory.GetPath("archive", "Emoji");
            System.IO.Directory.CreateDirectory(source); System.IO.Directory.CreateDirectory(archive);
            using var image = ImageFixtureFactory.CreatePattern(23);
            var incoming = Path.Combine(source, "incoming.png");
            await image.SaveAsPngAsync(incoming);
            for (var i = 0; i < count; i++) await image.SaveAsPngAsync(Path.Combine(archive, $"match-{i}.png"));
            var store = FileRouterTests.CreateStore(directory, source, archive);
            await store.UpdateAsync(s =>
            {
                s.Settings.ScanWorkers = 2;
                foreach (var mapping in s.Settings.CategoryMappings.Where(m => m.Category != VrcImageCategory.Emoji))
                    mapping.ArchivePath = "";
                return true;
            });
            var decoder = new ImageDecoder();
            var indexer = new ArchiveIndexer(store, decoder);
            await indexer.RebuildAsync(VrcImageCategory.Emoji);
            var state = await store.LoadAsync();
            var index = state.ArchiveIndex.Categories.Single(c => c.Category == VrcImageCategory.Emoji);
            var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
            var review = new ReviewItem
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                IncomingOriginalPath = incoming,
                HeldFilePath = incoming,
                IncomingFingerprint = fingerprint.ExactIdentity,
                IncomingImageFingerprint = fingerprint,
                IndexGeneration = index.Generation,
                Candidates = index.Images.Select(i => new ReviewCandidate
                {
                    Id = Guid.NewGuid(),
                    IndexedImageId = i.Id,
                    ArchivePath = i.Path,
                    ExpectedFingerprint = i.ExactFingerprint,
                    SimilarityScore = 1
                }).ToList()
            };
            await store.UpdateAsync(s => { s.ReviewQueue.Add(review); return true; });
            var recycler = new RecordingRecycler();
            return new(directory)
            {
                Store = store,
                Review = review,
                Router = new FileRouter(store, decoder, recycler),
                Recycler = recycler,
                Incoming = incoming,
                Archive = archive
            };
        }
        public void Dispose() { Store.Dispose(); Directory.Dispose(); }
    }

    private sealed class RecordingRecycler : IRecycleBinService
    {
        // Explicit integration opt-in; every path still belongs to this test's disposable fixture.
        private readonly IRecycleBinService? _realRecycler = Environment.GetEnvironmentVariable("VRC_REAL_RECYCLE_FIXTURES") == "1"
            ? new WindowsRecycleBinService(VrcPicSorter.App.Services.RecycleBinPolicy.IsDisabledForVolume) : null;
        public bool CanUse { get; set; } = true;
        public int FailOnCall { get; set; }
        public bool FailAfterDelete { get; set; }
        public Action<string>? AfterDelete { get; set; }
        public List<string> Removed { get; } = [];
        private int _calls;
        public bool CanRecycle(string path) => CanUse && (_realRecycler?.CanRecycle(path) ?? true);
        public async Task RecycleAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fail = ++_calls == FailOnCall;
            if (fail && !FailAfterDelete) throw new IOException("Simulated unavailable storage");
            if (_realRecycler is null) File.Delete(path);
            else await _realRecycler.RecycleAsync(path, cancellationToken);
            Removed.Add(path); AfterDelete?.Invoke(path);
            if (fail) throw new IOException("Simulated crash after recycle");
        }
    }
}
