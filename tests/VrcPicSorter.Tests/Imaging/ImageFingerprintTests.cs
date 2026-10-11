using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class ImageFingerprintTests
{
    private readonly ImageDecoder decoder = new();

    [Fact]
    public async Task IdenticalPixelsIgnoreFilenameAndNonVisualMetadata()
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreatePattern(7);
        var plain = await ImageFixtureFactory.SaveAsync(directory, "plain.png", source, FixtureFormat.Png);
        var metadata = await ImageFixtureFactory.SaveAsync(
            directory,
            "renamed-with-metadata.png",
            source,
            FixtureFormat.Png,
            addMetadata: true);

        var first = await FingerprintAsync(plain);
        var second = await FingerprintAsync(metadata);

        Assert.Equal(first.ExactIdentity, second.ExactIdentity);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
    }

    [Fact]
    public async Task OneChangedPixelChangesExactIdentity()
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreatePattern(8);
        using var changed = source.Clone();
        changed[3, 4] = new FixturePixel(1, 2, 3, 255);
        var firstPath = await ImageFixtureFactory.SaveAsync(directory, "first.png", source, FixtureFormat.Png);
        var secondPath = await ImageFixtureFactory.SaveAsync(directory, "second.png", changed, FixtureFormat.Png);

        var first = await FingerprintAsync(firstPath);
        var second = await FingerprintAsync(secondPath);

        Assert.NotEqual(first.ExactIdentity, second.ExactIdentity);
    }

    [Fact]
    public void DimensionsParticipateInExactIdentity()
    {
        using var wide = ImageFixtureFactory.CreateSolid(new FixturePixel(12, 34, 56), 12, 8);
        using var tall = ImageFixtureFactory.CreateSolid(new FixturePixel(12, 34, 56), 8, 12);

        var first = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(wide));
        var second = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(tall));

        Assert.NotEqual(first.ExactIdentity, second.ExactIdentity);
    }

    [Fact]
    public async Task AutoOrientationNormalizesDisplayedPixelsAndDimensions()
    {
        using var directory = new TestDirectory();
        using var displayed = ImageFixtureFactory.CreatePattern(10, 18, 12);
        var normalPath = await ImageFixtureFactory.SaveAsync(directory, "normal.png", displayed, FixtureFormat.Png);
        var orientedPath = await ImageFixtureFactory.SaveOrientedPngAsync(directory, "oriented.png", displayed);

        var normal = await FingerprintAsync(normalPath);
        var oriented = await FingerprintAsync(orientedPath);

        Assert.Equal((normal.Width, normal.Height), (oriented.Width, oriented.Height));
        Assert.Equal(normal.ExactIdentity, oriented.ExactIdentity);
    }

    [Fact]
    public async Task GifFrameOrderAndTimingParticipateInExactIdentity()
    {
        using var directory = new TestDirectory();
        using var red = ImageFixtureFactory.CreateSolid(new FixturePixel(255, 0, 0));
        using var green = ImageFixtureFactory.CreateSolid(new FixturePixel(0, 255, 0));
        using var blue = ImageFixtureFactory.CreateSolid(new FixturePixel(0, 0, 255));
        var originalPath = await ImageFixtureFactory.SaveGifAsync(directory, "original.gif", [red, green, blue], [100, 200, 300]);
        var copyPath = await ImageFixtureFactory.SaveGifAsync(directory, "copy.gif", [red, green, blue], [100, 200, 300]);
        var reorderedPath = await ImageFixtureFactory.SaveGifAsync(directory, "reordered.gif", [green, red, blue], [100, 200, 300]);
        var retimedPath = await ImageFixtureFactory.SaveGifAsync(directory, "retimed.gif", [red, green, blue], [100, 210, 300]);

        var original = await FingerprintAsync(originalPath);
        var copy = await FingerprintAsync(copyPath);
        var reordered = await FingerprintAsync(reorderedPath);
        var retimed = await FingerprintAsync(retimedPath);

        Assert.Equal(original.ExactIdentity, copy.ExactIdentity);
        Assert.NotEqual(original.ExactIdentity, reordered.ExactIdentity);
        Assert.NotEqual(original.ExactIdentity, retimed.ExactIdentity);
    }

    private async Task<ImageFingerprint> FingerprintAsync(string path)
    {
        var result = await decoder.DecodeAsync(path);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return ImageFingerprint.Create(result.Image!);
    }
}
