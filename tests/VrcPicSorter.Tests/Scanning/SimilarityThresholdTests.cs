using SixLabors.ImageSharp;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class SimilarityThresholdTests
{
    [Theory]
    [InlineData(100, 100, true, "unique")]
    [InlineData(0, 100, true, "review")]
    [InlineData(0, 0, true, "auto")]
    [InlineData(0, 0, false, "review")]
    public async Task LimitsRouteNonExactImagesWithoutPermanentDeletion(
        double minimum, double maximum, bool canRecycle, string expected)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("incoming");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        using var keeperImage = ImageFixtureFactory.CreatePattern(21);
        using var incomingImage = ImageFixtureFactory.CreateNearDuplicate(keeperImage);
        var keeper = Path.Combine(archive, "keeper.png");
        var incoming = Path.Combine(source, "incoming.png");
        await keeperImage.SaveAsPngAsync(keeper);
        await incomingImage.SaveAsPngAsync(incoming);
        var originalKeeper = await File.ReadAllBytesAsync(keeper);
        var first = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(keeperImage));
        var second = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(incomingImage));
        Assert.NotEqual(first.ExactIdentity, second.ExactIdentity);
        Assert.True(ImageMatcher.MeasureSimilarity(first, second).Score < 1);
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        await store.UpdateAsync(state =>
        {
            state.Settings.CustomSimilarityThresholds = new(minimum, maximum);
            return true;
        });
        Assert.Equal(new SimilarityThresholds(minimum, maximum), (await store.LoadAsync()).Settings.CustomSimilarityThresholds);
        var decoder = new ImageDecoder();
        var recycler = new FileRouterTests.FakeRecycleBinService(canRecycle);
        var scanner = new ScanCoordinator(store, new ArchiveIndexer(store, decoder), decoder,
            new FileRouter(store, decoder, recycler), TimeSpan.Zero);
        var result = await scanner.ScanCategoryAsync(VrcImageCategory.Emoji);
        Assert.Equal(originalKeeper, await File.ReadAllBytesAsync(keeper));
        Assert.Equal(expected == "unique" ? 1 : 0, result.MovedUnique);
        Assert.Equal(expected == "auto" ? 1 : 0, result.AutoKeptArchived);
        Assert.Equal(expected == "review" ? 1 : 0, result.HeldForReview);
        Assert.Equal(expected == "review", File.Exists(incoming));
        Assert.Equal(expected == "auto" ? 1 : 0, recycler.RecycledPaths.Count);
        if (expected == "unique") Assert.True(File.Exists(Path.Combine(archive, "incoming.png")));
        if (canRecycle) Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(-1, 90)]
    [InlineData(80, 101)]
    [InlineData(95, 90)]
    [InlineData(double.NaN, 90)]
    [InlineData(80, double.PositiveInfinity)]
    public void InvalidLimitsCannotBeApplied(double minimum, double maximum)
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = new SettingsDraft(state.Settings.CategoryMappings.Select(mapping =>
                new CategorySettingsDraft(mapping.Category, mapping.SourcePath, false)).ToArray(),
            state.Settings.OutputRootPath, SimilarityProfile.Conservative, false, true,
            CustomSimilarityThresholds: new(minimum, maximum));
        Assert.Contains("Similarity limits", draft.DescribeBlockingProblem(state.Settings));
        Assert.Throws<InvalidOperationException>(() => draft.ApplyTo(state));
        Assert.Null(state.Settings.CustomSimilarityThresholds);
    }

    [Fact]
    public void CustomMinimumIncludesItsBoundary()
    {
        using var first = ImageFixtureFactory.CreatePattern(21);
        using var second = ImageFixtureFactory.CreateNearDuplicate(first);
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(first));
        var candidate = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(second));
        var score = ImageMatcher.MeasureSimilarity(incoming, candidate).Score;
        Assert.Single(ImageMatcher.RankCandidates(incoming, [new("candidate", candidate)], SimilarityProfile.Strict, score));
        Assert.Empty(ImageMatcher.RankCandidates(incoming, [new("candidate", candidate)], SimilarityProfile.Strict, Math.BitIncrement(score)));
    }
}
