using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class QueuedScoringRefreshTests
{
    [Fact]
    public async Task RefreshUpdatesSuggestionsWithoutMovingFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Empty(result.Errors);
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue);
        Assert.Equal(ImageMatcher.CurrentScoringVersion, review.ScoringVersion);
        Assert.Equal(fixture.CandidateId, Assert.Single(review.Candidates).Id);
        Assert.NotEqual(0.123, review.Candidates[0].SimilarityScore);
        Assert.Equal(ReviewStatus.Pending, review.Status);
        fixture.AssertFilesUnchanged();
    }

    [Fact]
    public async Task RemovedSuggestionsRemainPendingAcrossRepeatedScans()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Store.UpdateAsync(state =>
        {
            state.Settings.CustomSimilarityThresholds = new(100, 100);
            return true;
        });
        for (var scan = 0; scan < 2; scan++)
        {
            Assert.Empty((await fixture.Coordinator.ScanCategoryAsync(VrcImageCategory.Emoji)).Errors);
            var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue);
            Assert.Empty(review.Candidates);
            Assert.True(review.AwaitingDecisionAfterScoringRefresh);
            Assert.Equal(ReviewStatus.Pending, review.Status);
            fixture.AssertFilesUnchanged();
        }
    }

    [Fact]
    public async Task ChangedIncomingCannotRefreshSavedSuggestions()
    {
        using var fixture = await Fixture.CreateAsync();
        using var replacement = ImageFixtureFactory.CreatePattern(99);
        await replacement.SaveAsPngAsync(fixture.Incoming);
        var result = await fixture.Coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Contains(result.Errors, error => error.Contains("queued match scores were not refreshed"));
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue);
        Assert.Equal(0, review.ScoringVersion);
        Assert.Equal(0.123, Assert.Single(review.Candidates).SimilarityScore);
        Assert.True(File.Exists(fixture.Incoming));
        Assert.Empty(fixture.Recycle.RecycledPaths);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("decision")]
    [InlineData("protection")]
    [InlineData("journal")]
    public async Task RefreshPreservesExistingDecisions(string kind)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Store.UpdateAsync(state =>
        {
            var review = Assert.Single(state.ReviewQueue);
            switch (kind)
            {
                case "selection": review.Candidates[0].IsSelected = true; break;
                case "decision": state.ReviewDecisions.Add(new() { ReviewId = review.Id }); break;
                case "protection": state.ManualKeeps.Add(new(fixture.Archived, review.Candidates[0].ExpectedFingerprint, 0, default)); break;
                case "journal": state.OperationJournal.Add(new() { Id = Guid.NewGuid(), ReviewItemId = review.Id, SourcePath = fixture.Incoming, Phase = JournalPhase.IntentRecorded, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow }); break;
            }
            return true;
        });
        if (kind == "decision")
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ScanCategoryAsync(VrcImageCategory.Emoji));
        else
            Assert.Empty((await fixture.Coordinator.ScanCategoryAsync(VrcImageCategory.Emoji)).Errors);
        var review = Assert.Single((await fixture.Store.LoadAsync()).ReviewQueue);
        Assert.Equal(0, review.ScoringVersion);
        Assert.Equal(0.123, Assert.Single(review.Candidates).SimilarityScore);
        fixture.AssertFilesUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public JsonStateStore Store { get; private set; } = null!;
        public ScanCoordinator Coordinator { get; private set; } = null!;
        public FileRouterTests.FakeRecycleBinService Recycle { get; } = new();
        public string Incoming { get; private set; } = "";
        public string Archived { get; private set; } = "";
        public Guid CandidateId { get; } = Guid.NewGuid();
        private byte[] _incomingBytes = [];
        private byte[] _archiveBytes = [];

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var source = fixture._directory.GetPath("incoming");
            var archive = fixture._directory.GetPath("archive", "Emoji");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(archive);
            fixture.Incoming = Path.Combine(source, "incoming.png");
            fixture.Archived = Path.Combine(archive, "archived.png");
            using var image = ImageFixtureFactory.CreatePattern(21);
            await image.SaveAsPngAsync(fixture.Incoming);
            using var resized = image.Clone(context => context.Resize(128, 96));
            await resized.SaveAsPngAsync(fixture.Archived);
            fixture._incomingBytes = await File.ReadAllBytesAsync(fixture.Incoming);
            fixture._archiveBytes = await File.ReadAllBytesAsync(fixture.Archived);
            fixture.Store = FileRouterTests.CreateStore(fixture._directory, source, archive);
            var decoder = new ImageDecoder();
            var indexer = new ArchiveIndexer(fixture.Store, decoder);
            await indexer.RefreshAsync(VrcImageCategory.Emoji);
            var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
            await fixture.Store.UpdateAsync(state =>
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji);
                var indexed = Assert.Single(index.Images);
                state.ReviewQueue.Add(new()
                {
                    Id = Guid.NewGuid(),
                    Category = VrcImageCategory.Emoji,
                    IncomingOriginalPath = fixture.Incoming,
                    HeldFilePath = fixture.Incoming,
                    IncomingFingerprint = fingerprint.ExactIdentity,
                    IncomingImageFingerprint = fingerprint,
                    IndexGeneration = index.Generation,
                    Status = ReviewStatus.Pending,
                    Candidates = [new() { Id = fixture.CandidateId, IndexedImageId = indexed.Id,
                        ArchivePath = indexed.Path, ExpectedFingerprint = indexed.ExactFingerprint,
                        MatchKind = MatchKind.Similar, SimilarityScore = 0.123 }],
                });
                return true;
            });
            fixture.Coordinator = new(fixture.Store, indexer, decoder,
                new FileRouter(fixture.Store, decoder, fixture.Recycle), TimeSpan.Zero);
            return fixture;
        }

        public void AssertFilesUnchanged()
        {
            Assert.Equal(_incomingBytes, File.ReadAllBytes(Incoming));
            Assert.Equal(_archiveBytes, File.ReadAllBytes(Archived));
            Assert.Empty(Recycle.RecycledPaths);
        }

        public void Dispose()
        {
            Store.Dispose();
            _directory.Dispose();
        }
    }
}
