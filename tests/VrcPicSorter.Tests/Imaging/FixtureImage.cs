using System.Collections;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using ImageMagick;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

// Fixtures own their raw pixels, independently of the production decoder and transforms.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct FixturePixel(byte R, byte G, byte B, byte A = 255);

internal sealed class FixtureImage : IDisposable
{
    private FixturePixel[] pixels;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DelayMilliseconds { get; set; }
    public ushort Orientation { get; set; } = 1;
    public string? Description { get; set; }
    public FixtureFrames Frames { get; }

    public FixtureImage(int width, int height, FixturePixel color = default)
    {
        Width = width;
        Height = height;
        pixels = new FixturePixel[checked(width * height)];
        Array.Fill(pixels, color);
        Frames = new FixtureFrames(this);
    }

    public FixturePixel this[int x, int y] { get => pixels[y * Width + x]; set => pixels[y * Width + x] = value; }
    public Span<FixturePixel> GetRowSpan(int y) => pixels.AsSpan(y * Width, Width);
    public void ProcessPixelRows(Action<FixtureImage> action) => action(this);
    public void CopyPixelDataTo(byte[] destination) => MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(destination);
    public byte[] Bytes() => MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    public void Dispose() { }
    public void Mutate(Action<FixtureImage> action) => action(this);
    public FixtureImage Clone(Action<FixtureImage>? action = null)
    {
        var copy = FromPixels(Bytes(), Width, Height);
        copy.Orientation = Orientation;
        copy.Description = Description;
        copy.DelayMilliseconds = DelayMilliseconds;
        foreach (var frame in Frames.Skip(1)) copy.Frames.AddFrame(frame);
        action?.Invoke(copy);
        return copy;
    }

    public static FixtureImage FromPixels(byte[] data, int width, int height)
    {
        var result = new FixtureImage(width, height);
        MemoryMarshal.Cast<byte, FixturePixel>(data).CopyTo(result.pixels);
        return result;
    }

    public FixtureImage BackgroundColor(FixturePixel color) { Array.Fill(pixels, color); return this; }
    public FixtureImage Brightness(float amount)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = new((byte)Math.Clamp(MathF.Round(p.R * amount), 0, 255),
                (byte)Math.Clamp(MathF.Round(p.G * amount), 0, 255),
                (byte)Math.Clamp(MathF.Round(p.B * amount), 0, 255), p.A);
        }
        return this;
    }

    public FixtureImage DrawImage(FixtureImage source, int left, int top)
    {
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                if (x + left >= 0 && x + left < Width && y + top >= 0 && y + top < Height)
                    this[x + left, y + top] = source[x, y];
        return this;
    }

    public FixtureImage Crop(int left, int top, int width, int height)
    {
        var cropped = new FixturePixel[width * height];
        for (var y = 0; y < height; y++) GetRowSpan(y + top).Slice(left, width).CopyTo(cropped.AsSpan(y * width, width));
        pixels = cropped; Width = width; Height = height;
        return this;
    }

    public FixtureImage Resize(int width, int height)
    {
        using var native = FixtureCodec.Native(this);
        native.FilterType = FilterType.Catrom;
        native.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        Replace(native);
        return this;
    }

    public FixtureImage GaussianBlur(float sigma)
    {
        using var native = FixtureCodec.Native(this);
        native.GaussianBlur(0, sigma);
        Replace(native);
        return this;
    }

    public FixtureImage Rotate270()
    {
        var rotated = new FixturePixel[pixels.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++) rotated[(Width - x - 1) * Height + y] = this[x, y];
        pixels = rotated;
        (Width, Height) = (Height, Width);
        return this;
    }

    private void Replace(IMagickImage<byte> image)
    {
        Width = (int)image.Width; Height = (int)image.Height;
        using var collection = image.GetPixels();
        pixels = MemoryMarshal.Cast<byte, FixturePixel>(collection.ToByteArray(PixelMapping.RGBA)!).ToArray();
    }

    public Task SaveAsPngAsync(string path) { SaveAsPng(path); return Task.CompletedTask; }
    public Task SaveAsGifAsync(string path) { SaveAsGif(path); return Task.CompletedTask; }
    public void SaveAsPng(string path) => FixtureCodec.Save(this, path, FixtureFormat.Png);
    public void SaveAsGif(string path) => FixtureCodec.Save(this, path, FixtureFormat.Gif);
}

internal sealed class FixtureFrames(FixtureImage root) : IEnumerable<FixtureImage>
{
    private readonly List<FixtureImage> frames = [root];
    public FixtureImage RootFrame => frames[0];
    public FixtureImage this[int index] => frames[index];
    public int Count => frames.Count;
    public void AddFrame(FixtureImage frame)
    {
        var copy = FixtureImage.FromPixels(frame.Bytes(), frame.Width, frame.Height);
        copy.DelayMilliseconds = frame.DelayMilliseconds;
        frames.Add(copy);
    }
    public IEnumerator<FixtureImage> GetEnumerator() => frames.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// Native format-specific setup is deliberately isolated from assertions and pixel generation.
internal static class FixtureCodec
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U);
        return crc;
    }).ToArray();

    public static MagickImage Native(FixtureImage source)
    {
        var native = new MagickImage();
        native.ReadPixels(source.Bytes(), new PixelReadSettings((uint)source.Width, (uint)source.Height, StorageType.Char, PixelMapping.RGBA));
        return native;
    }

    public static void Save(FixtureImage source, string path, FixtureFormat format)
    {
        using var output = File.Create(path);
        Save(source, output, format);
    }

    public static void SaveCmykJpeg(FixtureImage source, string path)
    {
        using var native = Native(source);
        native.ColorSpace = ColorSpace.CMYK;
        native.Quality = 100;
        native.Write(path, MagickFormat.Jpeg);
    }

    public static void Save(FixtureImage source, Stream output, FixtureFormat format, uint quality = 90)
    {
        // Independent PNG writer also permits intentionally oversized header fixtures after
        // the production native resource policy has been initialized by another test.
        if (format == FixtureFormat.Png) { WritePng(source, output); return; }
        using var frames = new MagickImageCollection();
        foreach (var frame in source.Frames)
        {
            var native = Native(frame);
            native.AnimationTicksPerSecond = 100;
            native.AnimationDelay = (uint)(frame.DelayMilliseconds / 10);
            native.AnimationIterations = 0;
            native.BackgroundColor = MagickColors.Transparent;
            native.GifDisposeMethod = GifDisposeMethod.Background;
            native.Quality = quality;
            if (source.Orientation != 1 || source.Description is not null)
            {
                var profile = new ExifProfile();
                profile.SetValue(ExifTag.Orientation, source.Orientation);
                if (source.Description is not null) profile.SetValue(ExifTag.ImageDescription, source.Description);
                native.SetProfile(profile);
            }
            frames.Add(native);
        }
        var encodedFormat = format switch
        {
            FixtureFormat.Png => MagickFormat.Png32,
            FixtureFormat.Gif => MagickFormat.Gif,
            FixtureFormat.Jpeg => MagickFormat.Jpeg,
            FixtureFormat.Webp => MagickFormat.WebP,
            FixtureFormat.Bmp => MagickFormat.Bmp,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        if (format == FixtureFormat.Webp)
            frames.Write(output, new ImageMagick.Formats.WebPWriteDefines { Lossless = true, Exact = true });
        else frames.Write(output, encodedFormat);
    }

    private static void WritePng(FixtureImage source, Stream output)
    {
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, source.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), source.Height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        if (source.Description is not null)
            Chunk(output, "tEXt", System.Text.Encoding.UTF8.GetBytes("Description\0" + source.Description));
        if (source.Orientation != 1)
        {
            // Little-endian TIFF with one SHORT Orientation entry, without an external profile writer.
            byte[] exif = [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)source.Orientation, 0, 0, 0, 0, 0, 0, 0];
            Chunk(output, "eXIf", exif);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = source.Bytes();
            for (var y = 0; y < source.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(bytes, y * source.Width * 4, source.Width * 4);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length);
        output.Write(integer);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(name); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var part in new[] { name, data })
            foreach (var value in part)
            {
                crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
            }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }

    public static FixtureImage Load(Stream stream)
    {
        using var native = new MagickImage(stream);
        using var pixels = native.GetPixels();
        return FixtureImage.FromPixels(pixels.ToByteArray(PixelMapping.RGBA)!, (int)native.Width, (int)native.Height);
    }

    public static (int Width, int Height, uint RepeatCount, uint[] Delays) GifMetadata(string path)
    {
        using var frames = new MagickImageCollection(path);
        return ((int)frames[0].Width, (int)frames[0].Height, frames[0].AnimationIterations,
            frames.Select(frame => frame.AnimationDelay).ToArray());
    }
}
