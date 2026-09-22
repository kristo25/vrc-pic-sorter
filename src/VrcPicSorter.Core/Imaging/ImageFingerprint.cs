using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VrcPicSorter.Core.Imaging;

public sealed class DecodedImage
{
    public DecodedImage(
        int width,
        int height,
        string formatName,
        IReadOnlyList<DecodedImageFrame> frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);

        var expectedLength = checked(width * height * 4);
        foreach (var frame in frames)
        {
            if (frame.RgbaPixels.Length != expectedLength)
            {
                throw new ArgumentException(
                    $"Every frame must contain exactly {expectedLength} canonical RGBA bytes.",
                    nameof(frames));
            }
        }

        Width = width;
        Height = height;
        FormatName = formatName;
        Frames = frames;
    }

    public int Width { get; }

    public int Height { get; }

    public string FormatName { get; }

    public IReadOnlyList<DecodedImageFrame> Frames { get; }
}

public sealed class DecodedImageFrame
{
    public DecodedImageFrame(byte[] rgbaPixels, int delayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);
        RgbaPixels = rgbaPixels;
        DelayMilliseconds = delayMilliseconds;
    }

    public byte[] RgbaPixels { get; }

    public int DelayMilliseconds { get; }
}

public sealed record ImageColorStatistics(
    double MeanRed,
    double MeanGreen,
    double MeanBlue,
    double StandardDeviationRed,
    double StandardDeviationGreen,
    double StandardDeviationBlue);

public sealed record PerceptualFrameFingerprint(
    string DifferenceHash,
    byte[] ThumbnailLuminance,
    byte[] ThumbnailRgb,
    string InsetDifferenceHash,
    byte[] InsetThumbnailLuminance,
    byte[] InsetThumbnailRgb,
    ImageColorStatistics ColorStatistics,
    byte[]? ThumbnailAlpha = null,
    byte[]? InsetThumbnailAlpha = null,
    byte[]? DetailLuminance = null,
    byte[]? InsetDetailLuminance = null,
    byte[]? DetailAlpha = null,
    byte[]? InsetDetailAlpha = null,
    byte[]? DetailRgb = null,
    byte[]? InsetDetailRgb = null,
    PerceptualFrameFingerprint? ContentView = null,
    double AspectRatio = 0);

public sealed record ImageFingerprint(
    string ExactIdentity,
    int Width,
    int Height,
    IReadOnlyList<int> FrameDelaysMilliseconds,
    IReadOnlyList<PerceptualFrameFingerprint> PerceptualFrames,
    int FeatureVersion = 1,
    IReadOnlyList<byte[]>? AnimationFrameSummaries = null)
{
    public const int CurrentFeatureVersion = 5;
    internal const int AnimationSummarySize = 6;
    private const int AnimationSampleCount = 8;
    private const int DifferenceHashWidth = 17;
    private const int DifferenceHashHeight = 16;
    private const int ThumbnailWidth = 16;
    private const int ThumbnailHeight = 16;
    private const int DetailThumbnailSize = 24;
    private static readonly byte[] ExactIdentityVersion = Encoding.ASCII.GetBytes("VRCIC-EXACT-V1");

    public int FrameCount => FrameDelaysMilliseconds?.Count ?? PerceptualFrames?.Count ?? 0;

    public double AspectRatio => Width / (double)Height;

    public bool HasCurrentFeatures => FeatureVersion == CurrentFeatureVersion
        && Width > 0
        && Height > 0
        && IsSha256Hex(ExactIdentity)
        && FrameDelaysMilliseconds is { Count: > 0 }
        && FrameDelaysMilliseconds.All(delay => delay >= 0)
        && PerceptualFrames is { Count: > 0 }
        && PerceptualFrames.Count == (FrameDelaysMilliseconds.Count == 1 ? 1 : AnimationSampleCount)
        && PerceptualFrames.All(IsCurrentFrame)
        && (FrameCount == 1 || (AnimationFrameSummaries?.Count == FrameCount
            && AnimationFrameSummaries.All(frame => frame?.Length == AnimationSummarySize * AnimationSummarySize * 4)));

    public static ImageFingerprint Create(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ExactIdentityVersion);
        AppendInt32(hash, image.Width);
        AppendInt32(hash, image.Height);
        AppendInt32(hash, image.Frames.Count);

        var delays = new int[image.Frames.Count];
        for (var index = 0; index < image.Frames.Count; index++)
        {
            var frame = image.Frames[index];
            delays[index] = frame.DelayMilliseconds;
            AppendInt32(hash, frame.DelayMilliseconds);
            hash.AppendData(frame.RgbaPixels);
        }

        var perceptualFrames = SelectPerceptualFrameIndices(image.Frames)
            .Select(index => CreatePerceptualFrame(
                image.Frames[index].RgbaPixels,
                image.Width,
                image.Height,
                normalizeMargins: image.Frames.Count == 1))
            .ToArray();

        return new ImageFingerprint(
            Convert.ToHexString(hash.GetHashAndReset()),
            image.Width,
            image.Height,
            delays,
            perceptualFrames,
            CurrentFeatureVersion,
            image.Frames.Count == 1 ? null : image.Frames.Select(frame =>
                CreateAnimationSummary(frame.RgbaPixels, image.Width, image.Height)).ToArray());
    }

    private static byte[] CreateAnimationSummary(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var summary = new byte[AnimationSummarySize * AnimationSummarySize * 4];
        ResizeRgbArea(pixels, width, height, AnimationSummarySize, AnimationSummarySize).CopyTo(summary, 0);
        ResizeAlpha(pixels, width, height, AnimationSummarySize, AnimationSummarySize).CopyTo(summary, AnimationSummarySize * AnimationSummarySize * 3);
        return summary;
    }

    private static IReadOnlyList<int> SelectPerceptualFrameIndices(
        IReadOnlyList<DecodedImageFrame> frames)
    {
        if (frames.Count == 1)
        {
            return [0];
        }

        var durations = frames
            .Select(frame => (long)Math.Max(1, frame.DelayMilliseconds))
            .ToArray();
        var totalDuration = durations.Sum();
        var indices = new int[AnimationSampleCount];
        for (var sampleIndex = 0; sampleIndex < indices.Length; sampleIndex++)
        {
            var targetTime = ((sampleIndex * 2L) + 1) * totalDuration / (AnimationSampleCount * 2L);
            long elapsed = 0;
            var frameIndex = 0;
            while (frameIndex < durations.Length - 1
                && elapsed + durations[frameIndex] <= targetTime)
            {
                elapsed += durations[frameIndex];
                frameIndex++;
            }

            indices[sampleIndex] = frameIndex;
        }

        return indices;
    }

    private static bool IsCurrentFrame(PerceptualFrameFingerprint? frame) =>
        frame is not null
        && double.IsFinite(frame.AspectRatio) && frame.AspectRatio > 0
        && IsSha256Hex(frame.DifferenceHash)
        && frame.ThumbnailLuminance?.Length == ThumbnailWidth * ThumbnailHeight
        && frame.ThumbnailRgb?.Length == ThumbnailWidth * ThumbnailHeight * 3
        && IsSha256Hex(frame.InsetDifferenceHash)
        && frame.InsetThumbnailLuminance?.Length == ThumbnailWidth * ThumbnailHeight
        && frame.InsetThumbnailRgb?.Length == ThumbnailWidth * ThumbnailHeight * 3
        && frame.ColorStatistics is not null
        && IsFinite(frame.ColorStatistics)
        && frame.ThumbnailAlpha?.Length == ThumbnailWidth * ThumbnailHeight
        && frame.InsetThumbnailAlpha?.Length == ThumbnailWidth * ThumbnailHeight
        && frame.DetailLuminance?.Length == DetailThumbnailSize * DetailThumbnailSize
        && frame.InsetDetailLuminance?.Length == DetailThumbnailSize * DetailThumbnailSize
        && frame.DetailAlpha?.Length == DetailThumbnailSize * DetailThumbnailSize
        && frame.InsetDetailAlpha?.Length == DetailThumbnailSize * DetailThumbnailSize
        && frame.DetailRgb?.Length == DetailThumbnailSize * DetailThumbnailSize * 3
        && frame.InsetDetailRgb?.Length == DetailThumbnailSize * DetailThumbnailSize * 3
        && (frame.ContentView is null || (frame.ContentView.ContentView is null && IsCurrentFrame(frame.ContentView)));

    private static bool IsSha256Hex(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f');

    private static bool IsFinite(ImageColorStatistics statistics) =>
        double.IsFinite(statistics.MeanRed)
        && double.IsFinite(statistics.MeanGreen)
        && double.IsFinite(statistics.MeanBlue)
        && double.IsFinite(statistics.StandardDeviationRed)
        && double.IsFinite(statistics.StandardDeviationGreen)
        && double.IsFinite(statistics.StandardDeviationBlue);

    private static PerceptualFrameFingerprint CreatePerceptualFrame(
        ReadOnlySpan<byte> rgbaPixels,
        int width,
        int height,
        bool normalizeMargins)
    {
        // Preserve the full-canvas features and add a second view without empty margins.
        // Keeping both avoids regressions when resampling changes the visible bounding box.
        PerceptualFrameFingerprint? contentView = null;
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        // Per-frame trimming of animations would erase motion, so only stills are normalized.
        if (normalizeMargins)
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    if (rgbaPixels[((y * width) + x) * 4 + 3] == 0) continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }

        if (right >= left && bottom >= top
            && (left > 0 || top > 0 || right < width - 1 || bottom < height - 1))
        {
            var contentWidth = right - left + 1;
            var contentHeight = bottom - top + 1;
            var content = new byte[checked(contentWidth * contentHeight * 4)];
            for (var y = 0; y < contentHeight; y++)
                rgbaPixels.Slice((((top + y) * width) + left) * 4, contentWidth * 4)
                    .CopyTo(content.AsSpan(y * contentWidth * 4));
            contentView = CreatePerceptualFrame(content, contentWidth, contentHeight, normalizeMargins: false);
        }

        var differenceSamples = ResizeLuminance(
            rgbaPixels,
            width,
            height,
            DifferenceHashWidth,
            DifferenceHashHeight);
        var insetDifferenceSamples = ResizeLuminance(
            rgbaPixels,
            width,
            height,
            DifferenceHashWidth,
            DifferenceHashHeight,
            insetFraction: 0.035);

        var differenceHash = CreateDifferenceHash(differenceSamples);
        var insetDifferenceHash = CreateDifferenceHash(insetDifferenceSamples);

        return new PerceptualFrameFingerprint(
            Convert.ToHexString(differenceHash),
            ResizeLuminance(rgbaPixels, width, height, ThumbnailWidth, ThumbnailHeight),
            ResizeRgb(rgbaPixels, width, height, ThumbnailWidth, ThumbnailHeight),
            Convert.ToHexString(insetDifferenceHash),
            ResizeLuminance(
                rgbaPixels,
                width,
                height,
                ThumbnailWidth,
                ThumbnailHeight,
                insetFraction: 0.035),
            ResizeRgb(
                rgbaPixels,
                width,
                height,
                ThumbnailWidth,
                ThumbnailHeight,
                insetFraction: 0.035),
            CalculateColorStatistics(rgbaPixels),
            ResizeAlpha(rgbaPixels, width, height, ThumbnailWidth, ThumbnailHeight),
            ResizeAlpha(
                rgbaPixels,
                width,
                height,
                ThumbnailWidth,
                ThumbnailHeight,
                insetFraction: 0.035),
            ResizeLuminanceArea(rgbaPixels, width, height, DetailThumbnailSize, DetailThumbnailSize),
            ResizeLuminanceArea(
                rgbaPixels,
                width,
                height,
                DetailThumbnailSize,
                DetailThumbnailSize,
                insetFraction: 0.035),
            ResizeAlpha(rgbaPixels, width, height, DetailThumbnailSize, DetailThumbnailSize),
            ResizeAlpha(
                rgbaPixels,
                width,
                height,
                DetailThumbnailSize,
                DetailThumbnailSize,
                insetFraction: 0.035),
            ResizeRgbArea(rgbaPixels, width, height, DetailThumbnailSize, DetailThumbnailSize),
            ResizeRgbArea(
                rgbaPixels,
                width,
                height,
                DetailThumbnailSize,
                DetailThumbnailSize,
                insetFraction: 0.035),
            contentView,
            width / (double)height);
    }

    private static byte[] CreateDifferenceHash(ReadOnlySpan<byte> differenceSamples)
    {
        var differenceHash = new byte[32];
        var bitIndex = 0;
        for (var y = 0; y < DifferenceHashHeight; y++)
        {
            var rowOffset = y * DifferenceHashWidth;
            for (var x = 0; x < DifferenceHashWidth - 1; x++)
            {
                if (differenceSamples[rowOffset + x] > differenceSamples[rowOffset + x + 1])
                {
                    differenceHash[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                }

                bitIndex++;
            }
        }

        return differenceHash;
    }

    private static byte[] ResizeLuminance(
        ReadOnlySpan<byte> rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        double insetFraction = 0)
    {
        var insetX = Math.Min(sourceWidth / 4D, sourceWidth * insetFraction);
        var insetY = Math.Min(sourceHeight / 4D, sourceHeight * insetFraction);
        var sampledWidth = sourceWidth - (insetX * 2);
        var sampledHeight = sourceHeight - (insetY * 2);
        var output = new byte[targetWidth * targetHeight];
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            var sourceY = insetY + (((targetY + 0.5) * sampledHeight / targetHeight) - 0.5);
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var yWeight = Math.Clamp(sourceY - y0, 0, 1);

            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                var sourceX = insetX + (((targetX + 0.5) * sampledWidth / targetWidth) - 0.5);
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var xWeight = Math.Clamp(sourceX - x0, 0, 1);
                var top = LuminanceAt(rgbaPixels, sourceWidth, x0, y0) * (1 - xWeight)
                    + LuminanceAt(rgbaPixels, sourceWidth, x1, y0) * xWeight;
                var bottom = LuminanceAt(rgbaPixels, sourceWidth, x0, y1) * (1 - xWeight)
                    + LuminanceAt(rgbaPixels, sourceWidth, x1, y1) * xWeight;
                output[targetY * targetWidth + targetX] =
                    (byte)Math.Clamp(Math.Round(top * (1 - yWeight) + bottom * yWeight), 0, 255);
            }
        }

        return output;
    }

    private static byte[] ResizeLuminanceArea(
        ReadOnlySpan<byte> rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        double insetFraction = 0)
    {
        var insetX = Math.Min(sourceWidth / 4D, sourceWidth * insetFraction);
        var insetY = Math.Min(sourceHeight / 4D, sourceHeight * insetFraction);
        var sampledWidth = sourceWidth - (insetX * 2);
        var sampledHeight = sourceHeight - (insetY * 2);
        var output = new byte[targetWidth * targetHeight];
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                double total = 0;
                const int sampleGrid = 4;
                for (var sampleY = 0; sampleY < sampleGrid; sampleY++)
                {
                    var sourceY = insetY
                        + ((targetY + ((sampleY + 0.5) / sampleGrid)) * sampledHeight / targetHeight);
                    var y = Math.Clamp((int)sourceY, 0, sourceHeight - 1);
                    for (var sampleX = 0; sampleX < sampleGrid; sampleX++)
                    {
                        var sourceX = insetX
                            + ((targetX + ((sampleX + 0.5) / sampleGrid)) * sampledWidth / targetWidth);
                        var x = Math.Clamp((int)sourceX, 0, sourceWidth - 1);
                        total += LuminanceAt(rgbaPixels, sourceWidth, x, y);
                    }
                }

                output[targetY * targetWidth + targetX] =
                    (byte)Math.Clamp(Math.Round(total / (sampleGrid * sampleGrid)), 0, 255);
            }
        }

        return output;
    }

    private static double LuminanceAt(ReadOnlySpan<byte> pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        var alpha = pixels[offset + 3] / 255D;
        var luminance = (pixels[offset] * 0.2126)
            + (pixels[offset + 1] * 0.7152)
            + (pixels[offset + 2] * 0.0722);
        return (luminance * alpha) + (255 * (1 - alpha));
    }

    private static byte[] ResizeRgb(
        ReadOnlySpan<byte> rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        double insetFraction = 0)
    {
        var insetX = Math.Min(sourceWidth / 4D, sourceWidth * insetFraction);
        var insetY = Math.Min(sourceHeight / 4D, sourceHeight * insetFraction);
        var sampledWidth = sourceWidth - (insetX * 2);
        var sampledHeight = sourceHeight - (insetY * 2);
        var output = new byte[targetWidth * targetHeight * 3];
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            var sourceY = insetY + (((targetY + 0.5) * sampledHeight / targetHeight) - 0.5);
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var yWeight = Math.Clamp(sourceY - y0, 0, 1);

            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                var sourceX = insetX + (((targetX + 0.5) * sampledWidth / targetWidth) - 0.5);
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var xWeight = Math.Clamp(sourceX - x0, 0, 1);
                var outputOffset = ((targetY * targetWidth) + targetX) * 3;

                for (var channel = 0; channel < 3; channel++)
                {
                    var top = ColorAt(rgbaPixels, sourceWidth, x0, y0, channel) * (1 - xWeight)
                        + ColorAt(rgbaPixels, sourceWidth, x1, y0, channel) * xWeight;
                    var bottom = ColorAt(rgbaPixels, sourceWidth, x0, y1, channel) * (1 - xWeight)
                        + ColorAt(rgbaPixels, sourceWidth, x1, y1, channel) * xWeight;
                    output[outputOffset + channel] =
                        (byte)Math.Clamp(Math.Round(top * (1 - yWeight) + bottom * yWeight), 0, 255);
                }
            }
        }

        return output;
    }

    private static byte[] ResizeAlpha(
        ReadOnlySpan<byte> rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        double insetFraction = 0)
    {
        var insetX = Math.Min(sourceWidth / 4D, sourceWidth * insetFraction);
        var insetY = Math.Min(sourceHeight / 4D, sourceHeight * insetFraction);
        var sampledWidth = sourceWidth - (insetX * 2);
        var sampledHeight = sourceHeight - (insetY * 2);
        var output = new byte[targetWidth * targetHeight];
        const int sampleGrid = 4;
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                double total = 0;
                for (var sampleY = 0; sampleY < sampleGrid; sampleY++)
                {
                    var sourceY = insetY
                        + ((targetY + ((sampleY + 0.5) / sampleGrid)) * sampledHeight / targetHeight);
                    var y = Math.Clamp((int)sourceY, 0, sourceHeight - 1);
                    for (var sampleX = 0; sampleX < sampleGrid; sampleX++)
                    {
                        var sourceX = insetX
                            + ((targetX + ((sampleX + 0.5) / sampleGrid)) * sampledWidth / targetWidth);
                        var x = Math.Clamp((int)sourceX, 0, sourceWidth - 1);
                        total += AlphaAt(rgbaPixels, sourceWidth, x, y);
                    }
                }

                output[targetY * targetWidth + targetX] =
                    (byte)Math.Clamp(Math.Round(total / (sampleGrid * sampleGrid)), 0, 255);
            }
        }

        return output;
    }

    private static byte[] ResizeRgbArea(
        ReadOnlySpan<byte> rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        double insetFraction = 0)
    {
        var insetX = Math.Min(sourceWidth / 4D, sourceWidth * insetFraction);
        var insetY = Math.Min(sourceHeight / 4D, sourceHeight * insetFraction);
        var sampledWidth = sourceWidth - (insetX * 2);
        var sampledHeight = sourceHeight - (insetY * 2);
        var output = new byte[targetWidth * targetHeight * 3];
        const int sampleGrid = 4;
        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                double redTotal = 0;
                double greenTotal = 0;
                double blueTotal = 0;
                for (var sampleY = 0; sampleY < sampleGrid; sampleY++)
                {
                    var sourceY = insetY
                        + ((targetY + ((sampleY + 0.5) / sampleGrid)) * sampledHeight / targetHeight);
                    var y = Math.Clamp((int)sourceY, 0, sourceHeight - 1);
                    for (var sampleX = 0; sampleX < sampleGrid; sampleX++)
                    {
                        var sourceX = insetX
                            + ((targetX + ((sampleX + 0.5) / sampleGrid)) * sampledWidth / targetWidth);
                        var x = Math.Clamp((int)sourceX, 0, sourceWidth - 1);
                        redTotal += ColorAt(rgbaPixels, sourceWidth, x, y, 0);
                        greenTotal += ColorAt(rgbaPixels, sourceWidth, x, y, 1);
                        blueTotal += ColorAt(rgbaPixels, sourceWidth, x, y, 2);
                    }
                }

                var outputOffset = ((targetY * targetWidth) + targetX) * 3;
                output[outputOffset] = AverageChannel(redTotal);
                output[outputOffset + 1] = AverageChannel(greenTotal);
                output[outputOffset + 2] = AverageChannel(blueTotal);
            }
        }

        return output;

        static byte AverageChannel(double total) =>
            (byte)Math.Clamp(Math.Round(total / (sampleGrid * sampleGrid)), 0, 255);
    }

    private static byte AlphaAt(ReadOnlySpan<byte> pixels, int width, int x, int y) =>
        pixels[(((y * width) + x) * 4) + 3];

    private static double ColorAt(ReadOnlySpan<byte> pixels, int width, int x, int y, int channel)
    {
        var offset = ((y * width) + x) * 4;
        var alpha = pixels[offset + 3] / 255D;
        return (pixels[offset + channel] * alpha) + (255 * (1 - alpha));
    }

    private static ImageColorStatistics CalculateColorStatistics(ReadOnlySpan<byte> pixels)
    {
        var pixelCount = pixels.Length / 4;
        double redSum = 0;
        double greenSum = 0;
        double blueSum = 0;
        double redSquared = 0;
        double greenSquared = 0;
        double blueSquared = 0;

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3] / 255D;
            var red = (pixels[offset] * alpha) + (255 * (1 - alpha));
            var green = (pixels[offset + 1] * alpha) + (255 * (1 - alpha));
            var blue = (pixels[offset + 2] * alpha) + (255 * (1 - alpha));
            redSum += red;
            greenSum += green;
            blueSum += blue;
            redSquared += red * red;
            greenSquared += green * green;
            blueSquared += blue * blue;
        }

        var meanRed = redSum / pixelCount;
        var meanGreen = greenSum / pixelCount;
        var meanBlue = blueSum / pixelCount;
        return new ImageColorStatistics(
            meanRed,
            meanGreen,
            meanBlue,
            StandardDeviation(redSquared, meanRed, pixelCount),
            StandardDeviation(greenSquared, meanGreen, pixelCount),
            StandardDeviation(blueSquared, meanBlue, pixelCount));
    }

    private static double StandardDeviation(double squaredSum, double mean, int count) =>
        Math.Sqrt(Math.Max(0, (squaredSum / count) - (mean * mean)));

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
