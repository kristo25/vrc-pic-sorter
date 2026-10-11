
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
        token.ThrowIfCancellationRequested();
        var info = RasterCodec.Identify(stream);
        var estimate = DecodeMemoryBudget.EstimateBytes(info.Width, info.Height, info.FrameCount);
        using var lease = await budget.AcquireAsync(estimate, token).ConfigureAwait(false);
        stream.Position = 0;
        var decoded = await DecodeCoreAsync(stream, path, info, token).ConfigureAwait(false);
        if (!decoded.IsSuccess)
        {
            var failure = decoded.Failure!;
            throw failure.Kind switch
            {
                ImageDecodeFailureKind.AccessDenied => new UnauthorizedAccessException(failure.Message),
                ImageDecodeFailureKind.InputOutput or ImageDecodeFailureKind.FileNotFound => new IOException(failure.Message),
                _ => new InvalidDataException(failure.Message),
            };
        }
        token.ThrowIfCancellationRequested();
        return ImageFingerprint.Create(decoded.Image!);
    }


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

    private Task<ImageDecodeResult> DecodeCoreAsync(
        Stream stream, string? sourceName, RasterInfo? identifiedInfo, CancellationToken cancellationToken)
        // Native reads are synchronous. Keep them off WPF callers and await their
        // completion even when cancellation arrives, so stream/scan locks stay alive.
        => Task.Run(() => DecodeCore(stream, sourceName, identifiedInfo, cancellationToken));

    private ImageDecodeResult DecodeCore(
        Stream stream, string? sourceName, RasterInfo? identifiedInfo, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = startPosition;
            var image = RasterCodec.Decode(stream, knownInfo: identifiedInfo);
            cancellationToken.ThrowIfCancellationRequested();
            return ImageDecodeResult.Success(image);
        }
        catch (NotSupportedException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.UnsupportedFormat,
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
