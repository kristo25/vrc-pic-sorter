using System.Numerics;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Imaging;

public sealed record ImageCandidate(string CandidateKey, ImageFingerprint Fingerprint);

public sealed record ImageMatchResult(
    string CandidateKey,
    MatchKind MatchKind,
    double SimilarityScore,
    IReadOnlyList<string> MatchReasons);

public sealed record MatchingProfileDefinition(
    SimilarityProfile Profile,
    double MinimumScore,
    string Description);

public sealed record ImageSimilarityMeasurement(
    double Score,
    IReadOnlyList<string> Reasons,
    ImageSimilarityComponents Components);

public sealed record ImageSimilarityComponents(
    double HashScore,
    double ThumbnailScore,
    double ColorThumbnailScore,
    double AlphaScore,
    double LocalDetailScore,
    double ColorScore,
    double AspectScore,
    double AnimationScore,
    double InsetAlignmentScore);

public static class ImageMatcher
{
    private static readonly IReadOnlyDictionary<SimilarityProfile, MatchingProfileDefinition> Profiles =
        new Dictionary<SimilarityProfile, MatchingProfileDefinition>
        {
            [SimilarityProfile.Strict] = new(
                SimilarityProfile.Strict,
                0.94,
                "Only very close visual transformations become review candidates."),
            [SimilarityProfile.Conservative] = new(
                SimilarityProfile.Conservative,
                0.82,
                "Resizes, recompression, light recoloring, and mild crops become review candidates."),
            [SimilarityProfile.Broad] = new(
                SimilarityProfile.Broad,
                0.70,
                "Broader edits become review candidates and may produce more results."),
        };

    public static MatchingProfileDefinition GetProfile(SimilarityProfile profile) => Profiles[profile];

    /// <summary>
    /// The score at which the reported percentage rounds to 100%.
    /// </summary>
    /// <remarks>
    /// A presentation threshold and nothing more. It decides what a person is shown next to a
    /// match; it does not decide anything about their files, and no code path may treat a number
    /// on the screen as permission to discard one.
    /// </remarks>
    public const double DisplayedAsIdenticalThreshold = 0.995;

    /// <summary>
    /// True when a ranked match can be resolved without asking anyone: the two images decode to
    /// the very same pixels, at the same size, in the same frame order, with the same timing.
    /// </summary>
    /// <remarks>
    /// Perceptual similarity ranks matches for review. It never authorises a deletion, because it
    /// cannot prove identity: an animation is compared on eight sampled frames, so a pair that
    /// differs on half of its frames still scores a rounded 100%. The audit built exactly that
    /// pair - two 16-frame GIFs, eight frames red against eight frames blue - and watched the
    /// incoming image go to the Recycle Bin without anyone seeing it. Identity is the whole
    /// decoded content or it is not identity.
    /// </remarks>
    public static bool IsSamePicture(
        ImageMatchResult match,
        ImageFingerprint incoming,
        ImageFingerprint candidate)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(candidate);

        return match.MatchKind == MatchKind.Exact
            && incoming.ExactIdentity.Equals(candidate.ExactIdentity, StringComparison.Ordinal);
    }

    public static ImageSimilarityMeasurement MeasureSimilarity(
        ImageFingerprint first,
        ImageFingerprint second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.ExactIdentity.Equals(second.ExactIdentity, StringComparison.Ordinal))
        {
            return new ImageSimilarityMeasurement(
                1,
                ["Exact decoded pixels, dimensions, frame order, and timing."],
                new ImageSimilarityComponents(1, 1, 1, 1, 1, 1, 1, 1, 0));
        }

        var comparison = Compare(first, second);
        return new ImageSimilarityMeasurement(
            comparison.Score,
            BuildReasons(first, second, comparison),
            ToComponents(comparison));
    }

    public static IReadOnlyList<ImageMatchResult> RankCandidates(
        ImageFingerprint incoming,
        IEnumerable<ImageCandidate> candidates,
        SimilarityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(candidates);

        var matches = new List<ImageMatchResult>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.CandidateKey);

            if (incoming.ExactIdentity.Equals(candidate.Fingerprint.ExactIdentity, StringComparison.Ordinal))
            {
                matches.Add(new ImageMatchResult(
                    candidate.CandidateKey,
                    MatchKind.Exact,
                    1,
                    ["Exact decoded pixels, dimensions, frame order, and timing."]));
                continue;
            }

            var comparison = Compare(incoming, candidate.Fingerprint);
            if (MeetsProfileEvidence(incoming, candidate.Fingerprint, comparison, profile))
            {
                matches.Add(new ImageMatchResult(
                    candidate.CandidateKey,
                    MatchKind.Similar,
                    comparison.Score,
                    BuildReasons(incoming, candidate.Fingerprint, comparison)));
            }
        }

        return matches
            .OrderByDescending(match => match.SimilarityScore)
            .ThenBy(match => match.CandidateKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MeetsProfileEvidence(
        ImageFingerprint first,
        ImageFingerprint second,
        PerceptualComparison comparison,
        SimilarityProfile profile)
    {
        if (!first.HasCurrentFeatures || !second.HasCurrentFeatures)
        {
            return comparison.Score >= GetProfile(profile).MinimumScore;
        }

        var includesAnimation = first.FrameCount > 1 || second.FrameCount > 1;
        if (includesAnimation)
        {
            var compatibleSequence = comparison.AnimationScore >= 0.55;
            return profile switch
            {
                SimilarityProfile.Strict => compatibleSequence && comparison.Score >= 0.94,
                SimilarityProfile.Conservative => compatibleSequence && comparison.Score >= 0.75,
                SimilarityProfile.Broad => comparison.AnimationScore >= 0.50 && comparison.Score >= 0.65,
                _ => false,
            };
        }

        var detailGap = comparison.ColorThumbnailScore - comparison.LocalDetailScore;
        var coherentLargeTransformation = comparison.Score >= 0.82
            && comparison.HashScore >= 0.95
            && comparison.HashScore < 0.985
            && comparison.ColorThumbnailScore < 0.92
            && comparison.ColorScore >= 0.985
            && comparison.LocalDetailScore < 0.80;
        var cropAligned = comparison.InsetAlignmentScore >= 0.50
            && comparison.ColorScore >= 0.985;
        var localizedContentConflict = detailGap >= 0.09
            && !cropAligned
            && !coherentLargeTransformation;
        if (localizedContentConflict)
        {
            return false;
        }

        return comparison.Score >= GetProfile(profile).MinimumScore;
    }

    private static PerceptualComparison Compare(ImageFingerprint first, ImageFingerprint second)
    {
        var frameComparison = AlignedFrameComparison(first.PerceptualFrames, second.PerceptualFrames);
        var aspectScore = Math.Min(first.AspectRatio, second.AspectRatio)
            / Math.Max(first.AspectRatio, second.AspectRatio);
        var animationScore = AnimationSimilarity(first, second);
        var score = (frameComparison.HashScore * 0.07)
            + (frameComparison.ThumbnailScore * 0.05)
            + (frameComparison.ColorThumbnailScore * 0.18)
            + (frameComparison.AlphaScore * 0.20)
            + (frameComparison.LocalDetailScore * 0.30)
            + (frameComparison.ColorScore * 0.05)
            + (aspectScore * 0.05)
            + (animationScore * 0.10);
        return new PerceptualComparison(
            Math.Clamp(score, 0, 1),
            frameComparison.HashScore,
            frameComparison.ThumbnailScore,
            frameComparison.ColorThumbnailScore,
            frameComparison.AlphaScore,
            frameComparison.LocalDetailScore,
            frameComparison.ColorScore,
            aspectScore,
            animationScore,
            frameComparison.InsetAlignmentScore);
    }

    private static FrameComparison AlignedFrameComparison(
        IReadOnlyList<PerceptualFrameFingerprint> first,
        IReadOnlyList<PerceptualFrameFingerprint> second)
    {
        if (first.Count == 1 || second.Count == 1)
        {
            var staticFrame = first.Count == 1 ? first[0] : second[0];
            var animatedFrames = SampleFrames(first.Count == 1 ? second : first, 8);
            return Average(animatedFrames.Select(frame => CompareFrame(staticFrame, frame)));
        }

        var sampleCount = Math.Min(8, Math.Max(first.Count, second.Count));
        var firstSamples = SampleFrames(first, sampleCount);
        var secondSamples = SampleFrames(second, sampleCount);
        FrameComparison? best = null;
        var bestScore = double.MinValue;
        for (var offset = 0; offset < sampleCount; offset++)
        {
            var comparisons = Enumerable.Range(0, sampleCount)
                .Select(index => CompareFrame(firstSamples[index], secondSamples[(index + offset) % sampleCount]));
            var current = Average(comparisons);
            var currentScore = FrameScore(current);
            if (currentScore > bestScore)
            {
                best = current;
                bestScore = currentScore;
            }
        }

        return best!;
    }

    private static FrameComparison CompareFrame(
        PerceptualFrameFingerprint first,
        PerceptualFrameFingerprint second)
    {
        var comparisons = new[]
        {
            CompareViews(first.DifferenceHash, first.ThumbnailLuminance, first.ThumbnailRgb, first.ThumbnailAlpha, first.DetailLuminance, first.DetailAlpha, first.DetailRgb, second.DifferenceHash, second.ThumbnailLuminance, second.ThumbnailRgb, second.ThumbnailAlpha, second.DetailLuminance, second.DetailAlpha, second.DetailRgb, 0),
            CompareViews(first.InsetDifferenceHash, first.InsetThumbnailLuminance, first.InsetThumbnailRgb, first.InsetThumbnailAlpha, first.InsetDetailLuminance, first.InsetDetailAlpha, first.InsetDetailRgb, second.DifferenceHash, second.ThumbnailLuminance, second.ThumbnailRgb, second.ThumbnailAlpha, second.DetailLuminance, second.DetailAlpha, second.DetailRgb, 1),
            CompareViews(first.DifferenceHash, first.ThumbnailLuminance, first.ThumbnailRgb, first.ThumbnailAlpha, first.DetailLuminance, first.DetailAlpha, first.DetailRgb, second.InsetDifferenceHash, second.InsetThumbnailLuminance, second.InsetThumbnailRgb, second.InsetThumbnailAlpha, second.InsetDetailLuminance, second.InsetDetailAlpha, second.InsetDetailRgb, 1),
            CompareViews(first.InsetDifferenceHash, first.InsetThumbnailLuminance, first.InsetThumbnailRgb, first.InsetThumbnailAlpha, first.InsetDetailLuminance, first.InsetDetailAlpha, first.InsetDetailRgb, second.InsetDifferenceHash, second.InsetThumbnailLuminance, second.InsetThumbnailRgb, second.InsetThumbnailAlpha, second.InsetDetailLuminance, second.InsetDetailAlpha, second.InsetDetailRgb, 0),
        };
        var best = comparisons.MaxBy(FrameScore)!;
        var colorScore = ColorSimilarity(first.ColorStatistics, second.ColorStatistics);
        return best with { ColorScore = colorScore };
    }

    private static FrameComparison CompareViews(
        string firstDifferenceHash,
        byte[] firstThumbnail,
        byte[] firstColorThumbnail,
        byte[]? firstAlphaThumbnail,
        byte[]? firstDetailLuminance,
        byte[]? firstDetailAlpha,
        byte[]? firstDetailRgb,
        string secondDifferenceHash,
        byte[] secondThumbnail,
        byte[] secondColorThumbnail,
        byte[]? secondAlphaThumbnail,
        byte[]? secondDetailLuminance,
        byte[]? secondDetailAlpha,
        byte[]? secondDetailRgb,
        double insetAlignmentScore)
    {
        var hashScore = HashSimilarity(firstDifferenceHash, secondDifferenceHash);
        double squaredDifference = 0;
        for (var index = 0; index < firstThumbnail.Length; index++)
        {
            var difference = firstThumbnail[index] - secondThumbnail[index];
            squaredDifference += difference * difference;
        }

        var rootMeanSquare = Math.Sqrt(squaredDifference / firstThumbnail.Length);
        var thumbnailScore = 1 - (rootMeanSquare / 255);
        double colorSquaredDifference = 0;
        for (var index = 0; index < firstColorThumbnail.Length; index++)
        {
            var difference = firstColorThumbnail[index] - secondColorThumbnail[index];
            colorSquaredDifference += difference * difference;
        }

        var colorRootMeanSquare = Math.Sqrt(colorSquaredDifference / firstColorThumbnail.Length);
        var colorThumbnailScore = 1 - (colorRootMeanSquare / 255);
        var alphaScore = AlphaSimilarity(firstAlphaThumbnail, secondAlphaThumbnail);
        var localDetailScore = LocalDetailSimilarity(
            firstColorThumbnail,
            firstAlphaThumbnail,
            firstDetailLuminance,
            firstDetailAlpha,
            firstDetailRgb,
            secondColorThumbnail,
            secondAlphaThumbnail,
            secondDetailLuminance,
            secondDetailAlpha,
            secondDetailRgb);
        return new FrameComparison(
            hashScore,
            thumbnailScore,
            colorThumbnailScore,
            alphaScore,
            localDetailScore,
            0,
            insetAlignmentScore);
    }

    private static double AlphaSimilarity(byte[]? first, byte[]? second)
    {
        if (first is null || second is null || first.Length != second.Length)
        {
            return 1;
        }

        double squaredDifference = 0;
        double intersection = 0;
        double union = 0;
        double firstCoverage = 0;
        double secondCoverage = 0;
        for (var index = 0; index < first.Length; index++)
        {
            var firstAlpha = first[index] / 255D;
            var secondAlpha = second[index] / 255D;
            var difference = firstAlpha - secondAlpha;
            squaredDifference += difference * difference;
            intersection += Math.Min(firstAlpha, secondAlpha);
            union += Math.Max(firstAlpha, secondAlpha);
            firstCoverage += firstAlpha;
            secondCoverage += secondAlpha;
        }

        var rootMeanSquareScore = 1 - Math.Sqrt(squaredDifference / first.Length);
        var overlapScore = union <= double.Epsilon ? 1 : intersection / union;
        var coverageScore = Math.Max(firstCoverage, secondCoverage) <= double.Epsilon
            ? 1
            : Math.Min(firstCoverage, secondCoverage) / Math.Max(firstCoverage, secondCoverage);
        return Math.Clamp(
            (rootMeanSquareScore * 0.45) + (overlapScore * 0.40) + (coverageScore * 0.15),
            0,
            1);
    }

    private static double LocalDetailSimilarity(
        byte[] firstColor,
        byte[]? firstAlpha,
        byte[]? firstDetailLuminance,
        byte[]? firstDetailAlpha,
        byte[]? firstDetailRgb,
        byte[] secondColor,
        byte[]? secondAlpha,
        byte[]? secondDetailLuminance,
        byte[]? secondDetailAlpha,
        byte[]? secondDetailRgb)
    {
        if (firstDetailRgb is not null
            && secondDetailRgb is not null
            && firstDetailRgb.Length == secondDetailRgb.Length
            && firstDetailRgb.Length % 3 == 0)
        {
            var detailPixelCount = firstDetailRgb.Length / 3;
            var detailTailCount = Math.Min(
                64,
                Math.Max(6, (int)Math.Ceiling(detailPixelCount * 0.015)));
            Span<double> largestErrors = stackalloc double[detailTailCount];
            var largestErrorCount = 0;
            for (var pixelIndex = 0; pixelIndex < detailPixelCount; pixelIndex++)
            {
                var colorOffset = pixelIndex * 3;
                var red = firstDetailRgb[colorOffset] - secondDetailRgb[colorOffset];
                var green = firstDetailRgb[colorOffset + 1] - secondDetailRgb[colorOffset + 1];
                var blue = firstDetailRgb[colorOffset + 2] - secondDetailRgb[colorOffset + 2];
                var colorError = Math.Sqrt(((red * red) + (green * green) + (blue * blue)) / 3D) / 255D;
                var alphaError = firstDetailAlpha is not null
                        && secondDetailAlpha is not null
                        && firstDetailAlpha.Length == detailPixelCount
                        && secondDetailAlpha.Length == detailPixelCount
                    ? Math.Abs(firstDetailAlpha[pixelIndex] - secondDetailAlpha[pixelIndex]) / 255D
                    : 0;
                TrackLargestError(
                    largestErrors,
                    ref largestErrorCount,
                    Math.Max(colorError, alphaError));
            }

            return Math.Clamp(1 - Average(largestErrors, largestErrorCount), 0, 1);
        }

        if (firstDetailLuminance is not null
            && secondDetailLuminance is not null
            && firstDetailLuminance.Length == secondDetailLuminance.Length)
        {
            var detailTailCount = Math.Min(
                64,
                Math.Max(6, (int)Math.Ceiling(firstDetailLuminance.Length * 0.015)));
            Span<double> largestErrors = stackalloc double[detailTailCount];
            var largestErrorCount = 0;
            for (var index = 0; index < firstDetailLuminance.Length; index++)
            {
                var luminanceError = Math.Abs(firstDetailLuminance[index] - secondDetailLuminance[index]) / 255D;
                var alphaError = firstDetailAlpha is not null
                        && secondDetailAlpha is not null
                        && firstDetailAlpha.Length == firstDetailLuminance.Length
                        && secondDetailAlpha.Length == firstDetailLuminance.Length
                    ? Math.Abs(firstDetailAlpha[index] - secondDetailAlpha[index]) / 255D
                    : 0;
                TrackLargestError(
                    largestErrors,
                    ref largestErrorCount,
                    Math.Max(luminanceError, alphaError));
            }

            return Math.Clamp(1 - Average(largestErrors, largestErrorCount), 0, 1);
        }

        var pixelCount = firstColor.Length / 3;
        var tailCount = Math.Min(64, Math.Max(4, (int)Math.Ceiling(pixelCount * 0.015)));
        Span<double> largestFallbackErrors = stackalloc double[tailCount];
        var largestFallbackErrorCount = 0;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var colorOffset = pixelIndex * 3;
            var redDifference = firstColor[colorOffset] - secondColor[colorOffset];
            var greenDifference = firstColor[colorOffset + 1] - secondColor[colorOffset + 1];
            var blueDifference = firstColor[colorOffset + 2] - secondColor[colorOffset + 2];
            var colorError = Math.Sqrt(
                ((redDifference * redDifference)
                    + (greenDifference * greenDifference)
                    + (blueDifference * blueDifference)) / 3D) / 255;
            var alphaError = firstAlpha is not null
                    && secondAlpha is not null
                    && firstAlpha.Length == pixelCount
                    && secondAlpha.Length == pixelCount
                ? Math.Abs(firstAlpha[pixelIndex] - secondAlpha[pixelIndex]) / 255D
                : 0;
            TrackLargestError(
                largestFallbackErrors,
                ref largestFallbackErrorCount,
                Math.Max(colorError, alphaError));
        }

        var tailError = Average(largestFallbackErrors, largestFallbackErrorCount);
        return Math.Clamp(1 - tailError, 0, 1);
    }

    private static double HashSimilarity(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first)
            || string.IsNullOrEmpty(second)
            || first.Length != second.Length)
        {
            return 0;
        }

        var differentBits = 0;
        for (var index = 0; index < first.Length; index++)
        {
            var firstNibble = HexNibble(first[index]);
            var secondNibble = HexNibble(second[index]);
            if (firstNibble < 0 || secondNibble < 0)
            {
                return 0;
            }

            differentBits += BitOperations.PopCount((uint)(firstNibble ^ secondNibble));
        }

        return 1 - (differentBits / (double)(first.Length * 4));
    }

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static void TrackLargestError(
        Span<double> largestErrors,
        ref int count,
        double error)
    {
        if (count < largestErrors.Length)
        {
            largestErrors[count++] = error;
            return;
        }

        var smallestIndex = 0;
        for (var index = 1; index < largestErrors.Length; index++)
        {
            if (largestErrors[index] < largestErrors[smallestIndex])
            {
                smallestIndex = index;
            }
        }

        if (error > largestErrors[smallestIndex])
        {
            largestErrors[smallestIndex] = error;
        }
    }

    private static double Average(ReadOnlySpan<double> values, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        double total = 0;
        for (var index = 0; index < count; index++)
        {
            total += values[index];
        }

        return total / count;
    }

    private static IReadOnlyList<PerceptualFrameFingerprint> SampleFrames(
        IReadOnlyList<PerceptualFrameFingerprint> frames,
        int requestedCount)
    {
        if (frames.Count == requestedCount)
        {
            return frames;
        }

        if (requestedCount == 1)
        {
            return [frames[0]];
        }

        return Enumerable.Range(0, requestedCount)
            .Select(index => frames[(int)Math.Round(index * (frames.Count - 1D) / (requestedCount - 1D))])
            .ToArray();
    }

    private static FrameComparison Average(IEnumerable<FrameComparison> comparisons)
    {
        var items = comparisons.ToArray();
        return new FrameComparison(
            items.Average(item => item.HashScore),
            items.Average(item => item.ThumbnailScore),
            items.Average(item => item.ColorThumbnailScore),
            items.Average(item => item.AlphaScore),
            items.Average(item => item.LocalDetailScore),
            items.Average(item => item.ColorScore),
            items.Average(item => item.InsetAlignmentScore));
    }

    private static double FrameScore(FrameComparison comparison) =>
        (comparison.HashScore * 0.07)
        + (comparison.ThumbnailScore * 0.05)
        + (comparison.ColorThumbnailScore * 0.18)
        + (comparison.AlphaScore * 0.20)
        + (comparison.LocalDetailScore * 0.45)
        + (comparison.ColorScore * 0.05);

    private static double AnimationSimilarity(ImageFingerprint first, ImageFingerprint second)
    {
        var firstAnimated = first.FrameCount > 1;
        var secondAnimated = second.FrameCount > 1;
        if (!firstAnimated && !secondAnimated)
        {
            return 1;
        }

        if (firstAnimated != secondAnimated)
        {
            return 0.35;
        }

        var frameCountScore = Math.Min(first.FrameCount, second.FrameCount)
            / (double)Math.Max(first.FrameCount, second.FrameCount);
        var firstDuration = first.FrameDelaysMilliseconds.Sum(delay => Math.Max(1, delay));
        var secondDuration = second.FrameDelaysMilliseconds.Sum(delay => Math.Max(1, delay));
        var durationScore = Math.Min(firstDuration, secondDuration)
            / (double)Math.Max(firstDuration, secondDuration);
        return 0.55 + (frameCountScore * 0.25) + (durationScore * 0.20);
    }

    private static double ColorSimilarity(ImageColorStatistics first, ImageColorStatistics second)
    {
        var totalDifference = Math.Abs(first.MeanRed - second.MeanRed)
            + Math.Abs(first.MeanGreen - second.MeanGreen)
            + Math.Abs(first.MeanBlue - second.MeanBlue)
            + (Math.Abs(first.StandardDeviationRed - second.StandardDeviationRed) * 0.5)
            + (Math.Abs(first.StandardDeviationGreen - second.StandardDeviationGreen) * 0.5)
            + (Math.Abs(first.StandardDeviationBlue - second.StandardDeviationBlue) * 0.5);
        return Math.Clamp(1 - (totalDifference / (255 * 4.5)), 0, 1);
    }

    private static IReadOnlyList<string> BuildReasons(
        ImageFingerprint incoming,
        ImageFingerprint candidate,
        PerceptualComparison comparison)
    {
        var reasons = new List<string>
        {
            $"Perceptual structure is {comparison.HashScore:P1} similar.",
            $"Normalized thumbnail pixels are {comparison.ThumbnailScore:P1} similar.",
            $"Normalized color layout is {comparison.ColorThumbnailScore:P1} similar.",
            $"Transparency layout is {comparison.AlphaScore:P1} similar.",
            $"Localized visual details are {comparison.LocalDetailScore:P1} similar.",
            $"Color statistics are {comparison.ColorScore:P1} similar.",
            $"Aspect ratio is {comparison.AspectScore:P1} similar.",
        };
        if (incoming.FrameCount > 1 || candidate.FrameCount > 1)
        {
            reasons.Add($"Animation sequence and timing are {comparison.AnimationScore:P1} compatible.");
        }

        return reasons;
    }

    private static ImageSimilarityComponents ToComponents(PerceptualComparison comparison) =>
        new(
            comparison.HashScore,
            comparison.ThumbnailScore,
            comparison.ColorThumbnailScore,
            comparison.AlphaScore,
            comparison.LocalDetailScore,
            comparison.ColorScore,
            comparison.AspectScore,
            comparison.AnimationScore,
            comparison.InsetAlignmentScore);

    private sealed record FrameComparison(
        double HashScore,
        double ThumbnailScore,
        double ColorThumbnailScore,
        double AlphaScore,
        double LocalDetailScore,
        double ColorScore,
        double InsetAlignmentScore);

    private sealed record PerceptualComparison(
        double Score,
        double HashScore,
        double ThumbnailScore,
        double ColorThumbnailScore,
        double AlphaScore,
        double LocalDetailScore,
        double ColorScore,
        double AspectScore,
        double AnimationScore,
        double InsetAlignmentScore);
}
