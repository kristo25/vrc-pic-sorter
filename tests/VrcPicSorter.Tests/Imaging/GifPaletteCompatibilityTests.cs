using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class GifPaletteCompatibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OutOfTableTransparencyPreservesPixelsTimingAndInput(bool local, bool interlaced)
    {
        var bytes = Gif(local, interlaced);
        var before = bytes.ToArray();
        using var stream = new MemoryStream(bytes);
        var decoded = await new ImageDecoder().DecodeAsync(stream, "legacy.gif");
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal((2, 4), (decoded.Image!.Width, decoded.Image.Height));
        var frame = Assert.Single(decoded.Image.Frames);
        Assert.Equal(70, frame.DelayMilliseconds);
        byte[] expected = [255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255,
            255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255];
        Assert.Equal(expected, frame.RgbaPixels);
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidVisibleColourIsNotSilentlyAdded(bool local)
    {
        using var stream = new MemoryStream(Gif(local, false, invalidVisible: true));
        var decoded = await new ImageDecoder().DecodeAsync(stream, "damaged.gif");
        Assert.False(decoded.IsSuccess);
        Assert.Null(decoded.Image);
    }

    [Fact]
    public async Task TruncatedLegacyImageRemainsAControlledFailure()
    {
        using var stream = new MemoryStream(Gif(false, false)[..^4]);
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.False(decoded.IsSuccess);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingTransparencySlotPreservesDeltaDisposal(int disposal)
    {
        var bytes = Gif(false, false)[..^1].ToList();
        void Frame(int mode, byte[] pixels)
        {
            bytes.AddRange([33, 249, 4, (byte)((mode << 2) | 1), 7, 0, 255, 0,
                44, 0, 0, 0, 0, 2, 0, 4, 0, 0, 8]);
            AppendPixels(bytes, pixels);
        }
        Frame(disposal, [0, 255, 255, 255, 255, 255, 255, 255]);
        Frame(1, [255, 255, 255, 255, 255, 255, 255, 255]);
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal(3, decoded.Image!.Frames.Count);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, decoded.Image.Frames[1].RgbaPixels[12..16]);
        Assert.Equal(disposal == 3 ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 0, 0, 0 },
            decoded.Image.Frames[2].RgbaPixels[12..16]);
        Assert.All(decoded.Image.Frames, frame => Assert.Equal(70, frame.DelayMilliseconds));
    }

    [Fact]
    public async Task ExpandingGlobalPaletteDoesNotValidateAnInvalidOpaqueFrame()
    {
        var bytes = Gif(false, false)[..^1].ToList();
        bytes.AddRange([33, 249, 4, 0, 7, 0, 0, 0, 44, 0, 0, 0, 0, 2, 0, 4, 0, 0, 8]);
        AppendPixels(bytes, [7, 1, 1, 1, 1, 1, 1, 1]);
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        Assert.False((await new ImageDecoder().DecodeAsync(stream)).IsSuccess);
    }

    [Fact]
    public async Task PaletteExpansionCannotInventAnOpaqueBackgroundColour()
    {
        var bytes = Gif(false, false);
        bytes[11] = 7;
        using var stream = new MemoryStream(bytes);
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.False(decoded.IsSuccess);
        Assert.Contains("Background colour", decoded.Failure!.Message);
    }

    [Theory]
    [InlineData(255, 10)]
    [InlineData(1, 11)]
    public async Task RepairDoesNotHideMalformedFixedExtensionHeaders(byte label, byte count)
    {
        var original = Gif(false, false);
        using var stream = new MemoryStream([.. original[..19], 33, label, count,
            .. new byte[count], 0, .. original[19..]]);
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.False(decoded.IsSuccess);
        Assert.Contains("extension header size", decoded.Failure!.Message);
    }

    [Fact]
    public async Task PlainTextConsumesTransparencyButCommentDoesNot()
    {
        var original = Gif(false, false);
        using var withComment = new MemoryStream([.. original[..27], 33, 254, 3, 97, 98, 99, 0, .. original[27..]]);
        Assert.True((await new ImageDecoder().DecodeAsync(withComment)).IsSuccess);
        var bytes = original[..^1].ToList();
        bytes.AddRange([33, 249, 4, 1, 7, 0, 255, 0, 33, 1, 12,
            0, 0, 0, 0, 2, 0, 4, 0, 1, 1, 0, 1, 0,
            44, 0, 0, 0, 0, 2, 0, 4, 0, 0, 8]);
        AppendPixels(bytes, [255, 1, 1, 1, 1, 1, 1, 1]); bytes.Add(59);
        using var withPlainText = new MemoryStream(bytes.ToArray());
        Assert.False((await new ImageDecoder().DecodeAsync(withPlainText)).IsSuccess);
    }

    [Fact]
    public void OverlaySupportsSeekingAcrossEditsWithoutOwningSourceOrCopyingPayload()
    {
        var original = Gif(false, false);
        using var source = new SmallReadStream([91, 92, .. original]) { Position = 2 };
        byte[] expected = [.. original[..10], 135, .. original[11..19], .. new byte[762], .. original[19..]];
        using (var view = GifPaletteCompatibility.CreateView(source))
        {
            Assert.NotNull(view);
            Assert.Equal(2, source.Position);
            Assert.Equal(expected.Length, view.Length);
            foreach (var offset in new[] { 0, 9, 10, 18, 19, 770, 780, expected.Length - 5 })
            {
                view.Seek(offset, SeekOrigin.Begin);
                var chunk = new byte[Math.Min(8, expected.Length - offset)];
                view.ReadExactly(chunk);
                Assert.Equal(expected.AsSpan(offset, chunk.Length).ToArray(), chunk);
            }
            Assert.Throws<IOException>(() => view.Seek(-1, SeekOrigin.Begin));
            view.Seek(0, SeekOrigin.End); Assert.Equal(-1, view.ReadByte());
        }
        Assert.Equal(2, source.Position); Assert.True(source.CanRead);
        Assert.Equal(original[0], source.ReadByte());
    }

    [Fact]
    public async Task NormalizationRetainsFrameResourceLimit()
    {
        var one = Gif(false, false);
        var bytes = one[..19].ToList();
        for (var frame = 0; frame <= ImageResourceLimits.MaximumFrames; frame++) bytes.AddRange(one[19..^1]);
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.False(decoded.IsSuccess);
        Assert.Contains("resource limit", decoded.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SmallReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer)
        {
            Assert.True(buffer.Length <= 32, "Compatibility must not materialize the complete encoded file.");
            return base.Read(buffer);
        }
    }

    [Fact]
    public async Task DictionaryReferencesIncludingKwKwKPreservePixels()
    {
        var bytes = Gif(false, false)[..38].ToList();
        AppendCodes(bytes, [256, 1, 255, 258, 260, 255, 257]);
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal(Enumerable.Range(0, 8).SelectMany(i => i % 2 == 0
            ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 0, 0, 0 }),
            decoded.Image!.Frames[0].RgbaPixels);
    }

    [Fact]
    public async Task LzwCodeWidthGrowthPreservesPixels()
    {
        var bytes = Gif(false, false)[..38].ToList();
        bytes[6] = bytes[32] = 44; bytes[7] = bytes[33] = 1;
        bytes[8] = bytes[34] = 2; bytes[9] = bytes[35] = 0;
        AppendCodes(bytes, new[] { 256 }.Concat(Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? 1 : 255)).Append(257));
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal((300, 2), (decoded.Image!.Width, decoded.Image.Height));
        Assert.Equal(Enumerable.Range(0, 600).SelectMany(i => i % 2 == 0
            ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 0, 0, 0 }),
            decoded.Image.Frames[0].RgbaPixels);
    }

    [Fact]
    public async Task InitialClearCodeIsOptionalWithoutWeakeningIndexValidation()
    {
        var bytes = Gif(false, false)[..38].ToList();
        AppendCodes(bytes, [1, 255, 1, 255, 1, 255, 1, 255, 257]);
        bytes.Add(59);
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = await new ImageDecoder().DecodeAsync(stream);
        Assert.True(decoded.IsSuccess, decoded.Failure?.Message);
        Assert.Equal(Enumerable.Range(0, 8).SelectMany(i => i % 2 == 0
            ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 0, 0, 0 }),
            decoded.Image!.Frames[0].RgbaPixels);
    }

    private static byte[] Gif(bool local, bool interlaced, bool invalidVisible = false)
    {
        var bytes = new List<byte>("GIF89a"u8.ToArray());
        bytes.AddRange([2, 0, 4, 0, 128, 0, 0, 0, 0, 0, 255, 0, 0]);
        bytes.AddRange([33, 249, 4, 5, 7, 0, 255, 0, 44, 0, 0, 0, 0, 2, 0, 4, 0,
            (byte)((local ? 128 : 0) | (interlaced ? 64 : 0))]);
        if (local) bytes.AddRange([0, 0, 0, 255, 0, 0]);
        bytes.Add(8);
        byte[] pixels = interlaced ? [1, 255, 1, 255, 255, 1, 255, 1] : [1, 255, 255, 1, 1, 255, 255, 1];
        if (invalidVisible) pixels[2] = 7;
        AppendPixels(bytes, pixels);
        bytes.Add(59);
        return bytes.ToArray();
    }

    private static void AppendPixels(List<byte> bytes, byte[] pixels)
    {
        var codes = pixels.SelectMany(pixel => new[] { 256, (int)pixel }).Append(257);
        AppendCodes(bytes, codes);
    }

    private static void AppendCodes(List<byte> bytes, IEnumerable<int> codes)
    {
        var payload = new List<byte>();
        var bits = 0; var buffer = 0;
        var width = 9; var next = 258; var previous = false;
        foreach (var code in codes)
        {
            buffer |= code << bits; bits += width;
            while (bits >= 8) { payload.Add((byte)buffer); buffer >>= 8; bits -= 8; }
            if (code == 256) { width = 9; next = 258; previous = false; }
            else if (code != 257)
            {
                if (previous && next < 4096 && ++next == 1 << width && width < 12) width++;
                previous = true;
            }
        }
        if (bits != 0) payload.Add((byte)buffer);
        for (var offset = 0; offset < payload.Count; offset += 255)
        {
            var count = Math.Min(255, payload.Count - offset);
            bytes.Add((byte)count); bytes.AddRange(payload.GetRange(offset, count));
        }
        bytes.Add(0);
    }
}
