using ImageMagick;
using ImageMagick.Configuration;
using System.Buffers.Binary;

namespace VrcPicSorter.Core.Imaging;

internal sealed record RasterInfo(int Width, int Height, int FrameCount, string Format);

/// <summary>Owns all native image state; callers exchange only canonical RGBA bytes.</summary>
internal static class RasterCodec
{
    private static readonly Lazy<bool> Initialized = new(Initialize);

    private static bool Initialize()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="coder" rights="none" pattern="*" />
              <policy domain="coder" rights="read|write" pattern="{PNG,PNG8,PNG24,PNG32,PNG48,PNG64,GIF,JPEG,JPG,WEBP,BMP,BMP2,BMP3}" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="resource" name="map" value="0" />
              <policy domain="resource" name="disk" value="0" />
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        ResourceLimits.Width = ImageResourceLimits.MaximumWidth;
        ResourceLimits.Height = ImageResourceLimits.MaximumHeight;
        ResourceLimits.ListLength = ImageResourceLimits.MaximumFrames;
        ResourceLimits.Memory = 1024UL * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 1;
        ResourceLimits.MaxMemoryRequest = (ulong)ImageResourceLimits.MaximumDecodedBytes;
        ResourceLimits.MaxProfileSize = 16UL * 1024 * 1024;
        return true;
    }

    public static RasterInfo Identify(string path)
    {
        using var stream = Open(path);
        return Identify(stream);
    }

    public static RasterInfo Identify(Stream stream) => Translate(() =>
    {
        var format = Prepare(stream);
        var start = stream.Position;
        try
        {
            using var frames = new MagickImageCollection();
            frames.Ping(stream, new MagickReadSettings { Format = format });
            return Validate(frames, FormatName(format));
        }
        finally { stream.Position = start; }
    });

    public static DecodedImage Decode(string path, bool firstFrameOnly = false)
    {
        using var stream = Open(path);
        return Decode(stream, firstFrameOnly);
    }

    public static DecodedImage Decode(Stream stream, bool firstFrameOnly = false, RasterInfo? knownInfo = null) => Translate(() =>
    {
        var format = Prepare(stream);
        var info = knownInfo ?? Identify(stream);
        using var frames = new MagickImageCollection();
        var settings = new MagickReadSettings { Format = format };
        if (firstFrameOnly) settings.FrameCount = 1;
        frames.Read(stream, settings);
        Validate(frames, info.Format);
        if (frames.Count > 1 || frames.Any(frame => frame.Width != info.Width
            || frame.Height != info.Height || frame.Page.X != 0 || frame.Page.Y != 0))
            frames.Coalesce();
        foreach (var frame in frames) frame.AutoOrient();
        var oriented = Validate(frames, info.Format);
        var result = new List<DecodedImageFrame>(frames.Count);
        foreach (var frame in frames)
        {
            if (frame.Width != oriented.Width || frame.Height != oriented.Height)
                throw new InvalidDataException("Animation frames do not have a consistent canvas.");
            var ticks = frame.AnimationTicksPerSecond;
            var delay = frames.Count > 1 || format is MagickFormat.Gif or MagickFormat.WebP
                ? checked((int)Math.Round(frame.AnimationDelay * 1000d / (ticks > 0 ? ticks : 100))) : 0;
            result.Add(new DecodedImageFrame(Pixels(frame), delay));
        }
        return new DecodedImage(oriented.Width, oriented.Height, info.Format, result);
    });

    public static DecodedImage Resize(DecodedImage image, int width, int height) => Transform(image, width, height,
        frame => frame.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true }));

    public static DecodedImage Crop(DecodedImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > image.Width || (long)y + height > image.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The crop must be inside the image.");
        return Transform(image, width, height, frame =>
        {
            frame.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
            frame.ResetPage();
        });
    }

    public static void WriteGif(DecodedImage image, Stream output) => Translate(() =>
    {
        _ = Initialized.Value;
        ImageResourceLimits.EnsureSafe(image.Width, image.Height, image.Frames.Count);
        using var frames = new MagickImageCollection();
        foreach (var source in image.Frames)
        {
            var frame = FromPixels(image, source);
            frame.Format = MagickFormat.Gif;
            frame.AnimationIterations = 0;
            frame.AnimationTicksPerSecond = 100;
            frame.AnimationDelay = checked((uint)Math.Max(1, (source.DelayMilliseconds + 5L) / 10));
            // Every input is a full composed frame. Clear it before showing the next, including blank frames.
            frame.BackgroundColor = MagickColors.Transparent;
            frame.GifDisposeMethod = GifDisposeMethod.Background;
            frames.Add(frame);
        }
        frames.Write(output, MagickFormat.Gif);
        return true;
    });

    public static void WritePng(DecodedImage image, Stream output) => Translate(() =>
    {
        _ = Initialized.Value;
        ImageResourceLimits.EnsureSafe(image.Width, image.Height, image.Frames.Count);
        using var frame = FromPixels(image, image.Frames[0]);
        frame.Write(output, MagickFormat.Png);
        return true;
    });

    private static DecodedImage Transform(DecodedImage image, int width, int height, Action<MagickImage> operation) => Translate(() =>
    {
        _ = Initialized.Value;
        ImageResourceLimits.EnsureSafe(width, height, image.Frames.Count);
        var result = new List<DecodedImageFrame>(image.Frames.Count);
        foreach (var source in image.Frames)
        {
            using var frame = FromPixels(image, source);
            operation(frame);
            result.Add(new DecodedImageFrame(Pixels(frame), source.DelayMilliseconds));
        }
        return new DecodedImage(width, height, image.FormatName, result);
    });

    private static MagickImage FromPixels(DecodedImage image, DecodedImageFrame frame)
    {
        var result = new MagickImage();
        try
        {
            result.ReadPixels(frame.RgbaPixels,
                new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Char, PixelMapping.RGBA));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static byte[] Pixels(IMagickImage<byte> frame)
    {
        if (frame.ColorSpace == ColorSpace.CMYK)
            frame.ColorSpace = ColorSpace.sRGB;
        using var pixels = frame.GetPixels();
        var bytes = pixels.ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException("The image has no pixels.");
        for (var i = 0; i < bytes.Length; i += 4)
            if (bytes[i + 3] == 0) bytes[i] = bytes[i + 1] = bytes[i + 2] = 0;
        return bytes;
    }

    private static RasterInfo Validate(MagickImageCollection frames, string format)
    {
        if (frames.Count == 0) throw new InvalidDataException("The image has no frames.");
        var width = 0;
        var height = 0;
        foreach (var frame in frames)
        {
            var page = frame.Page;
            var w = checked((int)Math.Max(frame.Width, page.Width));
            var h = checked((int)Math.Max(frame.Height, page.Height));
            if (page.X > 0) w = checked((int)Math.Max(w, (long)page.X + frame.Width));
            if (page.Y > 0) h = checked((int)Math.Max(h, (long)page.Y + frame.Height));
            width = Math.Max(width, w);
            height = Math.Max(height, h);
            ImageResourceLimits.EnsureSafe(width, height, frames.Count);
        }
        return new RasterInfo(width, height, frames.Count, format);
    }

    private static MagickFormat Prepare(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new IOException("The image stream must support seeking for safe decoding.");
        if (stream.Length - stream.Position > ImageResourceLimits.MaximumEncodedBytes)
            throw new InvalidDataException("The image exceeds the configured encoded-file resource limit.");
        Span<byte> header = stackalloc byte[24];
        var start = stream.Position;
        var count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = start;
        var data = header[..count];
        var format = data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? MagickFormat.Png
            : data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) ? MagickFormat.Gif
            : data.StartsWith(new byte[] { 255, 216, 255 }) ? MagickFormat.Jpeg
            : data.StartsWith("BM"u8) ? MagickFormat.Bmp
            : count >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8) ? MagickFormat.WebP
            : throw new NotSupportedException("The image is not a supported PNG, JPEG, GIF, WebP, or BMP file.");
        if (format == MagickFormat.Png && count >= 24)
            ImageResourceLimits.EnsureSafe(checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4))),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4))), 1);
        if (format == MagickFormat.Gif && count >= 10)
            ImageResourceLimits.EnsureSafe(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)), 1);
        _ = Initialized.Value;
        return format;
    }

    private static string FormatName(MagickFormat format) => format switch
    {
        MagickFormat.Jpeg => "JPEG",
        MagickFormat.WebP => "WEBP",
        _ => format.ToString().ToUpperInvariant(),
    };

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);

    private static T Translate<T>(Func<T> action)
    {
        try { return action(); }
        catch (MagickException exception) { throw new InvalidDataException(exception.Message, exception); }
        catch (DllNotFoundException exception) { throw new InvalidDataException("The bundled native image codec could not be loaded. Extract the complete portable app again.", exception); }
        catch (BadImageFormatException exception) { throw new InvalidDataException("The bundled native image codec does not match this Windows architecture.", exception); }
        catch (OverflowException exception) { throw new InvalidDataException("The image exceeds the configured resource limit.", exception); }
    }
}
