using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VrcPicSorter.Core.Imaging;

public enum ImageDecodeFailureKind
{
    FileNotFound,
    UnsupportedFormat,
    InvalidContent,
    AccessDenied,
    InputOutput,
}

public sealed record ImageDecodeFailure(
    ImageDecodeFailureKind Kind,
    string Message,
    string? SourceName = null);

public sealed record ImageDecodeResult(DecodedImage? Image, ImageDecodeFailure? Failure)
{
    public bool IsSuccess => Image is not null && Failure is null;

    internal static ImageDecodeResult Success(DecodedImage image) => new(image, null);

    internal static ImageDecodeResult Failed(
        ImageDecodeFailureKind kind,
        string message,
        string? sourceName = null) =>
        new(null, new ImageDecodeFailure(kind, message, sourceName));
}

public sealed class ImageDecoder
{
    internal async Task<ImageFingerprint> FingerprintAsync(string path, DecodeMemoryBudget budget, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ImageResourceLimits.MaximumEncodedBytes)
            throw new InvalidDataException("The image exceeds the configured encoded-file resource limit.");
        var info = await Image.IdentifyAsync(stream, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The image header could not be read.");
        var estimate = DecodeMemoryBudget.EstimateBytes(info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count));
        using var lease = await budget.AcquireAsync(estimate, token).ConfigureAwait(false);
        stream.Position = 0;
        var decoded = await DecodeCoreAsync(stream, path, info, token).ConfigureAwait(false);
        if (!decoded.IsSuccess) throw new InvalidDataException(decoded.Failure!.Message);
        token.ThrowIfCancellationRequested();
        return ImageFingerprint.Create(decoded.Image!);
    }

    private static readonly HashSet<string> SupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "PNG", "GIF", "JPEG", "WEBP", "BMP" };

    public async Task<ImageDecodeResult> DecodeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.FileNotFound,
                "The image file does not exist.",
                path);
        }

        try
        {
            // Delete is shared for the same reason the settle check shares everything: the scan
            // reads ahead of itself, so when a scan is cancelled a decode can still be running
            // against a file the next scan is about to move. Without this that move fails with a
            // sharing violation after the journal has already recorded its intent, leaving an
            // operation only startup recovery can clear.
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await DecodeAsync(stream, path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.AccessDenied, exception.Message, path);
        }
        catch (IOException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InputOutput, exception.Message, path);
        }
    }

    public Task<ImageDecodeResult> DecodeAsync(
        Stream stream,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        DecodeCoreAsync(stream, sourceName, null, cancellationToken);

    private async Task<ImageDecodeResult> DecodeCoreAsync(
        Stream stream, string? sourceName, ImageInfo? identifiedInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            if (!stream.CanSeek)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InputOutput,
                    "The image stream must support seeking for safe decoding.",
                    sourceName);
            }

            if (stream.Length - stream.Position > ImageResourceLimits.MaximumEncodedBytes)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InvalidContent,
                    "The image exceeds the configured encoded-file resource limit.",
                    sourceName);
            }

            var startPosition = stream.Position;
            var info = identifiedInfo ?? await Image.IdentifyAsync(stream, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InvalidContent,
                    "The image header could not be read.",
                    sourceName);
            }

            ImageResourceLimits.EnsureSafe(
                info.Width,
                info.Height,
                Math.Max(1, info.FrameMetadataCollection.Count));
            stream.Position = startPosition;
            var options = new DecoderOptions { MaxFrames = ImageResourceLimits.MaximumFrames };
            using var image = await Image.LoadAsync<Rgba32>(options, stream, cancellationToken).ConfigureAwait(false);
            var formatName = image.Metadata.DecodedImageFormat?.Name;
            if (formatName is null || !SupportedFormats.Contains(formatName))
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.UnsupportedFormat,
                    $"The decoded format '{formatName ?? "unknown"}' is not supported.",
                    sourceName);
            }

            image.Mutate(context => context.AutoOrient());
            var frames = new List<DecodedImageFrame>(image.Frames.Count);
            foreach (var frame in image.Frames)
            {
                var pixels = new byte[checked(image.Width * image.Height * 4)];
                frame.CopyPixelDataTo(pixels);
                NormalizeTransparentPixels(pixels);
                var delay = formatName.Equals("GIF", StringComparison.OrdinalIgnoreCase)
                    ? checked(frame.Metadata.GetGifMetadata().FrameDelay * 10)
                    : 0;
                frames.Add(new DecodedImageFrame(pixels, delay));
            }

            return ImageDecodeResult.Success(new DecodedImage(
                image.Width,
                image.Height,
                formatName,
                frames));
        }
        catch (UnknownImageFormatException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.UnsupportedFormat,
                exception.Message,
                sourceName);
        }
        catch (InvalidImageContentException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.InvalidContent,
                exception.Message,
                sourceName);
        }
        catch (ImageFormatException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.InvalidContent,
                exception.Message,
                sourceName);
        }
        catch (InvalidDataException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InvalidContent, exception.Message, sourceName);
        }
        catch (OverflowException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InvalidContent, exception.Message, sourceName);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.AccessDenied, exception.Message, sourceName);
        }
        catch (IOException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InputOutput, exception.Message, sourceName);
        }
    }

    private static void NormalizeTransparentPixels(Span<byte> pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
            }
        }
    }
}
