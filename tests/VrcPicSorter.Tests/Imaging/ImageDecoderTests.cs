using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class ImageDecoderTests
{
    [Fact]
    public void DeltaGifRestoresPreviousCanvasAndRetainsUndrawnPixels()
    {
        var gif = new List<byte>("GIF89a"u8.ToArray());
        gif.AddRange([2, 0, 2, 0, 129, 0, 0, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255]);
        void Frame(byte x, byte y, byte width, byte height, byte color, byte disposal)
        {
            gif.AddRange([33, 249, 4, (byte)(disposal << 2), 2, 0, 0, 0, 44, x, 0, y, 0, width, 0, height, 0, 0, 2]);
            var codes = new List<int>();
            for (var i = 0; i < width * height; i++) { codes.Add(4); codes.Add(color); }
            codes.Add(5);
            var bytes = new List<byte>();
            var packed = 0; var bits = 0;
            foreach (var code in codes)
            {
                packed |= code << bits; bits += 3;
                if (bits >= 8) { bytes.Add((byte)packed); packed >>= 8; bits -= 8; }
            }
            if (bits > 0) bytes.Add((byte)packed);
            gif.Add((byte)bytes.Count); gif.AddRange(bytes); gif.Add(0);
        }
        Frame(0, 0, 2, 2, 1, 1); // Full red canvas.
        Frame(0, 0, 1, 1, 2, 3); // Green temporary patch, restore previous.
        Frame(1, 0, 1, 1, 3, 1); // Blue patch over restored red.
        gif.Add(59);
        using var stream = new MemoryStream(gif.ToArray());
        var image = RasterCodec.Decode(stream);
        Assert.Equal(3, image.Frames.Count);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, image.Frames[1].RgbaPixels[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Frames[2].RgbaPixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Frames[2].RgbaPixels[4..8]);
        Assert.All(image.Frames, frame => Assert.Equal(20, frame.DelayMilliseconds));
    }

    [Fact]
    public async Task CmykJpegDecodesToDisplayedRgbColors()
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreateSolid(new FixturePixel(200, 40, 20, 255));
        var path = directory.GetPath("cmyk.jpg");
        FixtureCodec.SaveCmykJpeg(source, path);
        var result = await new ImageDecoder().DecodeAsync(path);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var pixels = result.Image!.Frames[0].RgbaPixels;
        Assert.InRange(pixels[0], (byte)195, (byte)205);
        Assert.InRange(pixels[1], (byte)35, (byte)45);
        Assert.InRange(pixels[2], (byte)15, (byte)25);
    }

    [Fact]
    public async Task CancellationWaitsForNativeReadBeforeCompleting()
    {
        using var directory = new TestDirectory();
        using var fixture = ImageFixtureFactory.CreatePattern(7);
        var path = await ImageFixtureFactory.SaveAsync(directory, "read.png", fixture, FixtureFormat.Png);
        using var stream = new GatedReadStream(await File.ReadAllBytesAsync(path));
        using var cancellation = new CancellationTokenSource();
        var task = new ImageDecoder().DecodeAsync(stream, cancellationToken: cancellation.Token);
        Assert.True(stream.Entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        Assert.False(task.IsCompleted);
        stream.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class GatedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public override int Read(Span<byte> buffer)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Test read was not released.");
            return base.Read(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Entered.Dispose(); Release.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffsetFirstGifFrameUsesItsLogicalCanvas(bool firstFrameOnly)
    {
        // Independent GIF: 4x4 transparent canvas, red 2x2 descriptor at (1,1).
        var data = new List<byte>("GIF89a"u8.ToArray());
        data.AddRange([4, 0, 4, 0, 128, 0, 0, 0, 0, 0, 255, 0, 0]);
        data.AddRange([33, 249, 4, 1, 0, 0, 0, 0, 44, 1, 0, 1, 0, 2, 0, 2, 0, 0, 2]);
        var codes = new[] { 4, 1, 4, 1, 4, 1, 4, 1, 5 };
        var packed = 0;
        for (var i = 0; i < codes.Length; i++) packed |= codes[i] << (3 * i);
        data.AddRange([4, (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24), 0, 59]);
        using var stream = new MemoryStream(data.ToArray());
        var image = RasterCodec.Decode(stream, firstFrameOnly);
        Assert.Equal((4, 4), (image.Width, image.Height));
        var pixels = Assert.Single(image.Frames).RgbaPixels;
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var offset = (y * 4 + x) * 4;
                var red = x is 1 or 2 && y is 1 or 2;
                Assert.Equal(red ? (byte)255 : (byte)0, pixels[offset]);
                Assert.Equal(red ? (byte)255 : (byte)0, pixels[offset + 3]);
            }
    }

    [Theory]
    [InlineData(FixtureFormat.Png, "image.png")]
    [InlineData(FixtureFormat.Gif, "image.gif")]
    [InlineData(FixtureFormat.Jpeg, "image.jpg")]
    [InlineData(FixtureFormat.Webp, "image.webp")]
    [InlineData(FixtureFormat.Bmp, "image.bmp")]
    internal async Task SupportedFormatsDecodeToCanonicalRgbaFrames(FixtureFormat format, string fileName)
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreatePattern(4);
        var path = await ImageFixtureFactory.SaveAsync(directory, fileName, source, format);

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(source.Width, result.Image!.Width);
        Assert.Equal(source.Height, result.Image.Height);
        Assert.Single(result.Image.Frames);
        Assert.Equal(source.Width * source.Height * 4, result.Image.Frames[0].RgbaPixels.Length);
    }

    [Fact]
    public async Task UnsupportedContentReturnsTypedFailure()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("not-an-image.temp");
        await File.WriteAllTextAsync(path, "not an image");

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageDecodeFailureKind.UnsupportedFormat, result.Failure!.Kind);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(FixtureFormat.Png, "pixels.png")]
    [InlineData(FixtureFormat.Webp, "pixels.webp")]
    [InlineData(FixtureFormat.Bmp, "pixels.bmp")]
    internal async Task LosslessDecodeMatchesIndependentlyGeneratedPixels(FixtureFormat format, string name)
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreatePattern(29, 17, 11);
        source[2, 3] = new FixturePixel(12, 34, 56, 128);
        source[4, 5] = new FixturePixel(0, 0, 0, 0);
        var path = await ImageFixtureFactory.SaveAsync(directory, name, source, format);
        var decoded = await new ImageDecoder().DecodeAsync(path);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal(source.Bytes(), decoded.Image!.Frames[0].RgbaPixels);
    }

    [Fact]
    public async Task AnimatedWebpRetainsCanvasPixelsAndTiming()
    {
        using var directory = new TestDirectory();
        using var first = ImageFixtureFactory.CreateSolid(new FixturePixel(255, 0, 0), 8, 8);
        using var second = ImageFixtureFactory.CreateSolid(new FixturePixel(0, 255, 0), 8, 8);
        first.DelayMilliseconds = 80;
        second.DelayMilliseconds = 230;
        first.Frames.AddFrame(second);
        var path = await ImageFixtureFactory.SaveAsync(directory, "animation.webp", first, FixtureFormat.Webp);
        var decoded = await new ImageDecoder().DecodeAsync(path);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal([80, 230], decoded.Image!.Frames.Select(frame => frame.DelayMilliseconds));
        Assert.Equal(first.Bytes(), decoded.Image.Frames[0].RgbaPixels);
        Assert.Equal(second.Bytes(), decoded.Image.Frames[1].RgbaPixels);
    }

    [Fact]
    public async Task TransparentGifFramesClearPreviousCanvas()
    {
        using var directory = new TestDirectory();
        using var first = new FixtureImage(8, 8);
        using var second = new FixtureImage(8, 8);
        first[1, 1] = new FixturePixel(255, 0, 0);
        second[6, 6] = new FixturePixel(0, 255, 0);
        var path = await ImageFixtureFactory.SaveGifAsync(directory, "transparent.gif", [first, second], [100, 200]);
        var decoded = await new ImageDecoder().DecodeAsync(path);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal(first.Bytes(), decoded.Image!.Frames[0].RgbaPixels);
        Assert.Equal(second.Bytes(), decoded.Image.Frames[1].RgbaPixels);
    }

    [Fact]
    public async Task AnimatedGifPreservesOrderedFrameDelays()
    {
        using var directory = new TestDirectory();
        using var red = ImageFixtureFactory.CreateSolid(new FixturePixel(255, 0, 0));
        using var green = ImageFixtureFactory.CreateSolid(new FixturePixel(0, 255, 0));
        var path = await ImageFixtureFactory.SaveGifAsync(directory, "timed.gif", [red, green], [80, 230]);

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal([80, 230], result.Image!.Frames.Select(frame => frame.DelayMilliseconds));
    }

    [Theory]
    [InlineData(1, 1, ImageResourceLimits.MaximumFrames + 1)]
    [InlineData(ImageResourceLimits.MaximumWidth + 1, 1, 1)]
    [InlineData(4096, 4096, 9)]
    public void UnsafeDecodedImageSizesAreRejected(int width, int height, int frames)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => ImageResourceLimits.EnsureSafe(width, height, frames));

        Assert.Contains("resource limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
