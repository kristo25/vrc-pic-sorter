using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

internal enum FixtureFormat
{
    Png,
    Gif,
    Jpeg,
    Webp,
    Bmp,
}

internal static class ImageFixtureFactory
{
    public static FixtureImage CreatePattern(int seed, int width = 64, int height = 48)
    {
        var image = new FixtureImage(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var cell = ((x / 8) + ((y / 8) * 3) + seed) % 7;
                    var diagonal = (x + (y * (seed % 5 + 1))) % 19;
                    row[x] = new FixturePixel(
                        (byte)((seed * 37 + x * 3 + cell * 29) % 256),
                        (byte)((seed * 71 + y * 5 + diagonal * 11) % 256),
                        (byte)((seed * 19 + x * 2 + y * 3 + cell * 41) % 256),
                        255);
                }
            }
        });
        return image;
    }

    public static FixtureImage CreateSolid(FixturePixel color, int width = 24, int height = 16) =>
        new(width, height, color);

    public static async Task<string> SaveAsync(
        TestDirectory directory,
        string fileName,
        FixtureImage image,
        FixtureFormat format,
        bool addMetadata = false)
    {
        var path = directory.GetPath(fileName);
        using var copy = image.Clone();
        if (addMetadata)
        {
            copy.Description = $"metadata-{fileName}";
        }

        FixtureCodec.Save(copy, path, format);
        await Task.CompletedTask;
        return path;
    }

    public static async Task<string> SaveOrientedPngAsync(
        TestDirectory directory,
        string fileName,
        FixtureImage displayedImage)
    {
        using var storedImage = displayedImage.Clone(context => context.Rotate270());
        storedImage.Orientation = 6;
        return await SaveAsync(directory, fileName, storedImage, FixtureFormat.Png);
    }

    public static async Task<string> SaveGifAsync(
        TestDirectory directory,
        string fileName,
        IReadOnlyList<FixtureImage> frames,
        IReadOnlyList<int> delaysMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);
        ArgumentOutOfRangeException.ThrowIfNotEqual(frames.Count, delaysMilliseconds.Count);

        using var animation = frames[0].Clone();
        animation.Frames.RootFrame.DelayMilliseconds = delaysMilliseconds[0];
        for (var index = 1; index < frames.Count; index++)
        {
            animation.Frames.AddFrame(frames[index].Frames.RootFrame);
            animation.Frames[index].DelayMilliseconds = delaysMilliseconds[index];
        }

        var path = directory.GetPath(fileName);
        await animation.SaveAsGifAsync(path);
        return path;
    }

    public static DecodedImage ToDecodedImage(FixtureImage image)
    {
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        return new DecodedImage(
            image.Width,
            image.Height,
            "fixture",
            [new DecodedImageFrame(pixels, 0)]);
    }

    /// <summary>
    /// A visibly identical but not byte-identical copy: close enough to be proposed as a review
    /// candidate, different enough that it is never an exact match.
    /// </summary>
    public static FixtureImage CreateNearDuplicate(FixtureImage source)
    {
        var variant = source.Clone();
        variant.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    row[x] = new FixturePixel(
                        (byte)Math.Min(255, pixel.R + 5),
                        (byte)Math.Max(0, pixel.G - 3),
                        (byte)Math.Min(255, pixel.B + 4),
                        pixel.A);
                }
            }
        });
        return variant;
    }

    public static IReadOnlyList<FixtureImage> CreatePerceptualVariants(FixtureImage source)
    {
        var resized = source.Clone(context => context.Resize(source.Width + 19, source.Height + 13));
        var lightlyRecolored = CreateNearDuplicate(source);
        var cropped = source.Clone(context => context
            .Crop(2, 2, source.Width - 4, source.Height - 4)
            .Resize(source.Width, source.Height));
        var softened = source.Clone(context => context.GaussianBlur(0.65f));
        var recompressed = RoundTripJpeg(source, quality: 82);

        return [resized, lightlyRecolored, cropped, softened, recompressed];
    }

    private static FixtureImage RoundTripJpeg(FixtureImage source, int quality)
    {
        using var stream = new MemoryStream();
        FixtureCodec.Save(source, stream, FixtureFormat.Jpeg, (uint)quality);
        stream.Position = 0;
        return FixtureCodec.Load(stream);
    }

}
