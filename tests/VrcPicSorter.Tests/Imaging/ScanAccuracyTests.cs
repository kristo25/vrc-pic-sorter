using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Imaging;

public sealed class ScanAccuracyTests
{
    [Theory]
    [InlineData(0.12f)]
    [InlineData(0.30f)]
    public void DifferentDarkScenesDoNotBecomeHighConfidenceMatches(float brightness)
    {
        using var left = ImageFixtureFactory.CreatePattern(71);
        using var right = ImageFixtureFactory.CreatePattern(93);
        left.Mutate(c => c.Brightness(brightness));
        right.Mutate(c => c.Brightness(brightness));
        var a = Fingerprint(left);
        var b = Fingerprint(right);

        Assert.Empty(ImageMatcher.RankCandidates(a, [new("different", b)], SimilarityProfile.Conservative, .82));
        Assert.Equal(ImageMatcher.MeasureSimilarity(a, b).Score, ImageMatcher.MeasureSimilarity(b, a).Score, 10);
    }

    [Theory]
    [InlineData(0.12f)]
    [InlineData(0.30f)]
    public void ResizedDarkSceneStillMatches(float brightness)
    {
        using var source = ImageFixtureFactory.CreatePattern(71);
        source.Mutate(c => c.Brightness(brightness));
        using var resized = source.Clone(c => c.Resize(128, 96));
        var a = Fingerprint(source);
        var b = Fingerprint(resized);
        var result = Assert.Single(ImageMatcher.RankCandidates(a, [new("resized", b)], SimilarityProfile.Conservative, .82));
        Assert.False(ImageMatcher.IsSamePicture(result, a, b));
    }

    private static ImageFingerprint Fingerprint(FixtureImage image) =>
        ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));

    [Fact]
    public void ChangedLetteringOnSharedTemplateDoesNotPassCustomMinimum()
    {
        using var source = new FixtureImage(192, 128, new FixturePixel(235, 216, 170));
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                if (x < 10 || x >= 182 || y < 10 || y >= 118)
                    source[x, y] = new FixturePixel(115, 85, 25);
        using var other = source.Clone();
        for (var y = 42; y < 78; y++)
            for (var x = 25; x < 167; x++)
            {
                if ((x / 5 + y / 6) % 3 == 0) source[x, y] = new FixturePixel(25, 20, 15);
                if ((x / 7 + y / 4) % 4 == 0) other[x, y] = new FixturePixel(25, 20, 15);
            }
        Assert.Empty(ImageMatcher.RankCandidates(Fingerprint(source), [new("changed-text", Fingerprint(other))], SimilarityProfile.Conservative, .82));
    }
}
