namespace VrcPicSorter.Core.Imaging;

public static class ImageResourceLimits
{
    public const int MaximumWidth = 16_384;
    public const int MaximumHeight = 16_384;
    public const int MaximumFrames = 256;
    public const long MaximumDecodedBytes = 512L * 1024 * 1024;
    public const long MaximumEncodedBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Rejects a file whose encoded bytes alone are past the limit, before a decoder is given it.
    /// </summary>
    /// <remarks>
    /// The cheapest check there is, and the only one available before anything has been read. A
    /// header can claim a modest size and still sit in front of gigabytes of frame data, so the
    /// file's own length is measured first and the header's claims second.
    /// </remarks>
    public static void EnsureEncodedSizeSafe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // Not this check's business. A file that is not there fails when it is opened, in
                // the words the caller already handles, rather than as a resource-limit refusal.
                return;
            }

            length = info.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The image could not be measured before decoding: {exception.Message}");
        }

        if (length > MaximumEncodedBytes)
        {
            throw new InvalidDataException("The image exceeds the configured encoded-file resource limit.");
        }
    }

    public static void EnsureSafe(int width, int height, int frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0
            || width > MaximumWidth
            || height > MaximumHeight
            || frameCount > MaximumFrames)
        {
            throw new InvalidDataException("The image exceeds the configured resource limit.");
        }

        var decodedBytes = checked((long)width * height * 4 * frameCount);
        if (decodedBytes > MaximumDecodedBytes)
        {
            throw new InvalidDataException("The image exceeds the configured decoded-memory resource limit.");
        }
    }
}
