using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Imaging;

public sealed class ImageMatcherTests
{
    [Fact]
    public void TransparentPaddingIsRecognizedWithoutBecomingExact()
    {
        using var source = ImageFixtureFactory.CreatePattern(811);
        using var padded = new Image<Rgba32>(source.Width * 2, source.Height * 2);
        padded.Mutate(context => context.DrawImage(source, new Point(19, 21), 1));
        var first = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var second = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(padded));
        var match = Assert.Single(ImageMatcher.RankCandidates(first, [new("padded", second)], SimilarityProfile.Strict));
        Assert.True(match.SimilarityScore >= .99);
        Assert.False(ImageMatcher.IsSamePicture(match, first, second));
        Assert.False((first with { FeatureVersion = 4 }).HasCurrentFeatures);
    }

    [Fact]
    public void EmptyTransparentImagesRemainValidAndFinite()
    {
        using var source = new Image<Rgba32>(48, 32);
        using var other = new Image<Rgba32>(96, 64);
        var first = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var second = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(other));
        Assert.True(first.HasCurrentFeatures);
        Assert.True(double.IsFinite(ImageMatcher.MeasureSimilarity(first, second).Score));
    }

    [Fact]
    public void ExactCandidateRanksFirstWithAnExactReason()
    {
        using var source = ImageFixtureFactory.CreatePattern(11);
        using var unrelated = ImageFixtureFactory.CreatePattern(12);
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var candidates = new[]
        {
            new ImageCandidate("unrelated", ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(unrelated))),
            new ImageCandidate("same", ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source))),
        };

        var results = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Strict);

        Assert.Equal("same", results[0].CandidateKey);
        Assert.Equal(MatchKind.Exact, results[0].MatchKind);
        Assert.Equal(1D, results[0].SimilarityScore);
        Assert.Contains(results[0].MatchReasons, reason => reason.Contains("Exact", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileThresholdsAreMonotonicAndConservativeFindsIntendedTransforms()
    {
        using var source = ImageFixtureFactory.CreatePattern(15);
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var variants = ImageFixtureFactory.CreatePerceptualVariants(source);
        try
        {
            var candidates = variants
                .Select((image, index) => new ImageCandidate(
                    $"variant-{index}",
                    ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image))))
                .ToArray();

            var strict = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Strict);
            var conservative = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Conservative);
            var broad = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Broad);

            Assert.Equal(candidates.Length, conservative.Count);
            Assert.True(strict.Count <= conservative.Count);
            Assert.True(conservative.Count <= broad.Count);
            Assert.All(conservative, result => Assert.Equal(MatchKind.Similar, result.MatchKind));
            Assert.All(conservative, result => Assert.NotEmpty(result.MatchReasons));
        }
        finally
        {
            foreach (var variant in variants)
            {
                variant.Dispose();
            }
        }
    }

    [Fact]
    public void ClearlyUnrelatedImageStaysBelowBroadThreshold()
    {
        using var black = ImageFixtureFactory.CreateSolid(new(0, 0, 0));
        using var white = ImageFixtureFactory.CreateSolid(new(255, 255, 255));
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(black));
        var candidate = new ImageCandidate("white", ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(white)));

        var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Broad);

        Assert.Empty(results);
    }

    [Fact]
    public void SparseTransparentImagesWithDifferentContentStayBelowBroadThreshold()
    {
        using var first = CreateSparseGraphic(42, 150, new Rgba32(255, 80, 170, 255));
        using var second = CreateSparseGraphic(164, 150, new Rgba32(255, 80, 170, 255));
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(first));
        var candidate = new ImageCandidate(
            "different-sparse-graphic",
            ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(second)));

        var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Broad);

        Assert.Empty(results);
    }

    [Fact]
    public void SmallChangedRegionOnTheSameBackgroundIsRejectedByConservativeProfile()
    {
        using var background = ImageFixtureFactory.CreatePattern(31, 192, 108);
        using var first = background.Clone();
        using var second = background.Clone();
        PaintRectangle(first, 30, 54, 20, 14, new Rgba32(235, 45, 70, 255));
        PaintRectangle(second, 138, 50, 20, 14, new Rgba32(45, 110, 235, 255));
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(first));
        var candidate = new ImageCandidate(
            "changed-object",
            ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(second)));
        var measurement = ImageMatcher.MeasureSimilarity(incoming, candidate.Fingerprint);

        var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Conservative);

        Assert.True(
            results.Count == 0,
            $"Changed foreground matched at {measurement.Score:F3}; "
            + $"hash={measurement.Components.HashScore:F3}, rgb={measurement.Components.ColorThumbnailScore:F3}, "
            + $"detail={measurement.Components.LocalDetailScore:F3}, color={measurement.Components.ColorScore:F3}.");
    }

    [Fact]
    public void ResizedSparseGraphicRemainsAConservativeMatch()
    {
        using var source = CreateSparseGraphic(82, 96, new Rgba32(120, 70, 245, 255));
        using var resized = source.Clone(context => context.Resize(192, 192));
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var candidate = new ImageCandidate(
            "resized",
            ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(resized)));

        var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Conservative);

        Assert.Single(results);
    }

    [Fact]
    public void UnrelatedAnimationsDoNotMatchByCherryPickingIndividualFrames()
    {
        var firstFrames = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(24 + (index * 16), 70, new Rgba32(245, 80, 120, 255)))
            .ToArray();
        var secondFrames = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(160 - (index * 14), 150, new Rgba32(70, 180, 245, 255)))
            .ToArray();
        try
        {
            var incoming = CreateAnimationFingerprint(firstFrames);
            var candidate = new ImageCandidate("unrelated-animation", CreateAnimationFingerprint(secondFrames));

            var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Conservative);

            Assert.Empty(results);
        }
        finally
        {
            foreach (var frame in firstFrames.Concat(secondFrames))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void ResizedAnimationRemainsAConservativeMatch()
    {
        var frames = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(38 + (index * 14), 92, new Rgba32(245, 80, 170, 255)))
            .ToArray();
        var resizedFrames = frames
            .Select(frame => frame.Clone(context => context.Resize(192, 192)))
            .ToArray();
        try
        {
            var incoming = CreateAnimationFingerprint(frames);
            var candidate = new ImageCandidate("resized-animation", CreateAnimationFingerprint(resizedFrames));

            var results = ImageMatcher.RankCandidates(incoming, [candidate], SimilarityProfile.Conservative);

            Assert.Single(results);
        }
        finally
        {
            foreach (var frame in frames.Concat(resizedFrames))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void AnimationsWithDifferentFrameCountsCanBeComparedSafely()
    {
        var shortAnimation = Enumerable.Range(0, 3)
            .Select(index => CreateSparseGraphic(40 + (index * 18), 92, new Rgba32(245, 80, 170, 255)))
            .ToArray();
        var longAnimation = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(40 + (index * 7), 92, new Rgba32(245, 80, 170, 255)))
            .ToArray();
        try
        {
            var first = CreateAnimationFingerprint(shortAnimation);
            var second = CreateAnimationFingerprint(longAnimation);

            var measurement = ImageMatcher.MeasureSimilarity(first, second);
            var matches = ImageMatcher.RankCandidates(
                first,
                [new ImageCandidate("same-motion", second)],
                SimilarityProfile.Conservative);

            Assert.InRange(measurement.Score, 0, 1);
            Assert.Single(matches);
        }
        finally
        {
            foreach (var frame in shortAnimation.Concat(longAnimation))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void EquivalentAnimationTimelinesMatchAcrossDuplicatedFramesAndTiming()
    {
        var frames = Enumerable.Range(0, 3)
            .Select(index => CreateSparseGraphic(42 + (index * 34), 92, new Rgba32(245, 80, 170, 255)))
            .ToArray();
        try
        {
            var compact = CreateAnimationFingerprint(frames, [100, 100, 100]);
            var expanded = CreateAnimationFingerprint(
                [frames[0], frames[0], frames[1], frames[1], frames[2], frames[2]],
                [40, 60, 70, 30, 55, 45]);

            var matches = ImageMatcher.RankCandidates(
                compact,
                [new ImageCandidate("expanded", expanded)],
                SimilarityProfile.Conservative);

            Assert.Single(matches);
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void AnimatedFingerprintCapsPersistedPerceptualFrames()
    {
        var pixels = new byte[] { 120, 70, 245, 255 };
        var frames = Enumerable.Range(0, 256)
            .Select(index => new DecodedImageFrame(pixels, 20 + (index % 3)))
            .ToArray();

        var fingerprint = ImageFingerprint.Create(new DecodedImage(1, 1, "fixture", frames));
        var serialized = JsonSerializer.Serialize(fingerprint);

        Assert.Equal(256, fingerprint.FrameCount);
        Assert.Equal(8, fingerprint.PerceptualFrames.Count);
        Assert.True(fingerprint.HasCurrentFeatures);
        Assert.True(serialized.Length < 150_000, $"Serialized fingerprint was {serialized.Length:N0} characters.");
    }

    [Fact]
    public void AnimatedCandidateComparisonStaysWithinAllocationBudget()
    {
        var firstFrames = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(24 + (index * 16), 70, new Rgba32(245, 80, 120, 255)))
            .ToArray();
        var secondFrames = Enumerable.Range(0, 8)
            .Select(index => CreateSparseGraphic(160 - (index * 14), 150, new Rgba32(70, 180, 245, 255)))
            .ToArray();
        try
        {
            var incoming = CreateAnimationFingerprint(firstFrames);
            var candidateFingerprint = CreateAnimationFingerprint(secondFrames);
            var candidates = Enumerable.Range(0, 100)
                .Select(index => new ImageCandidate($"candidate-{index}", candidateFingerprint))
                .ToArray();
            _ = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Conservative);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var matches = ImageMatcher.RankCandidates(incoming, candidates, SimilarityProfile.Conservative);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Empty(matches);
            Assert.True(allocated < 25_000_000, $"Animated matching allocated {allocated:N0} bytes.");
        }
        finally
        {
            foreach (var frame in firstFrames.Concat(secondFrames))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void UnrelatedAnimationTimelinesWithDifferentFrameCountsStayRejected()
    {
        var firstFrames = Enumerable.Range(0, 3)
            .Select(index => CreateSparseGraphic(35 + (index * 24), 65, new Rgba32(245, 75, 120, 255)))
            .ToArray();
        var secondFrames = Enumerable.Range(0, 6)
            .Select(index => CreateSparseGraphic(175 - (index * 18), 165, new Rgba32(65, 190, 245, 255)))
            .ToArray();
        try
        {
            var incoming = CreateAnimationFingerprint(firstFrames, [120, 40, 140]);
            var candidate = CreateAnimationFingerprint(secondFrames, [20, 70, 35, 85, 25, 65]);

            var matches = ImageMatcher.RankCandidates(
                incoming,
                [new ImageCandidate("unrelated", candidate)],
                SimilarityProfile.Broad);

            Assert.Empty(matches);
        }
        finally
        {
            foreach (var frame in firstFrames.Concat(secondFrames))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void NullCurrentPerceptualFrameIsRejectedWithoutThrowing()
    {
        using var image = ImageFixtureFactory.CreatePattern(220);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var frames = fingerprint.PerceptualFrames.ToArray();
        frames[0] = null!;

        var malformed = fingerprint with { PerceptualFrames = frames };

        Assert.False(malformed.HasCurrentFeatures);
    }

    [Fact]
    public void EveryAboveThresholdCandidateIsReturnedAndSerializableWithoutPagingLoss()
    {
        using var source = ImageFixtureFactory.CreatePattern(17);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(source));
        var candidates = Enumerable.Range(0, 40)
            .Select(index => new ImageCandidate($"candidate-{index:D2}", fingerprint))
            .ToArray();

        var results = ImageMatcher.RankCandidates(fingerprint, candidates, SimilarityProfile.Strict);
        var serialized = JsonSerializer.Serialize(results);
        var restored = JsonSerializer.Deserialize<List<ImageMatchResult>>(serialized);

        Assert.Equal(40, results.Count);
        Assert.Equal(40, restored!.Count);
        Assert.Equal(results.Select(result => result.CandidateKey), restored.Select(result => result.CandidateKey));
        Assert.Equal(10, results.Skip(10).Take(10).Count());
        Assert.Equal(10, results.Skip(30).Take(10).Count());
    }

    [Fact]
    public void ConservativeProfileMeetsDeterministicCorpusQualityGate()
    {
        const int baseImageCount = 50;
        var bases = new List<(string Key, ImageFingerprint Fingerprint)>();
        for (var seed = 0; seed < baseImageCount; seed++)
        {
            using var image = ImageFixtureFactory.CreatePattern(seed);
            bases.Add(($"base-{seed:D2}", ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image))));
        }

        var candidates = bases.Select(item => new ImageCandidate(item.Key, item.Fingerprint)).ToArray();
        var intendedVariants = 0;
        var truePositives = 0;
        var returnedCandidates = 0;
        var candidateCounts = new List<int>();
        var missed = new List<string>();
        var minimumTrueScore = 1D;
        var maximumFalseScore = 0D;

        for (var seed = 0; seed < baseImageCount; seed++)
        {
            using var source = ImageFixtureFactory.CreatePattern(seed);
            var variants = ImageFixtureFactory.CreatePerceptualVariants(source);
            try
            {
                for (var variantIndex = 0; variantIndex < variants.Count; variantIndex++)
                {
                    var variant = variants[variantIndex];
                    intendedVariants++;
                    var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(variant));
                    var results = ImageMatcher.RankCandidates(fingerprint, candidates, SimilarityProfile.Conservative);
                    var trueMeasurement = ImageMatcher.MeasureSimilarity(fingerprint, bases[seed].Fingerprint);
                    var trueScore = trueMeasurement.Score;
                    minimumTrueScore = Math.Min(minimumTrueScore, trueScore);
                    maximumFalseScore = Math.Max(
                        maximumFalseScore,
                        bases.Where((_, index) => index != seed)
                            .Max(item => ImageMatcher.MeasureSimilarity(fingerprint, item.Fingerprint).Score));
                    if (results.All(result => result.CandidateKey != $"base-{seed:D2}"))
                    {
                        missed.Add(
                            $"{seed}:{variantIndex}={trueScore:F3} "
                            + $"h{trueMeasurement.Components.HashScore:F3} "
                            + $"r{trueMeasurement.Components.ColorThumbnailScore:F3} "
                            + $"d{trueMeasurement.Components.LocalDetailScore:F3} "
                            + $"c{trueMeasurement.Components.ColorScore:F3}");
                    }

                    candidateCounts.Add(results.Count);
                    returnedCandidates += results.Count;
                    truePositives += results.Count(result => result.CandidateKey == $"base-{seed:D2}");
                }
            }
            finally
            {
                foreach (var variant in variants)
                {
                    variant.Dispose();
                }
            }
        }

        candidateCounts.Sort();
        var p95Index = (int)Math.Ceiling(candidateCounts.Count * 0.95) - 1;
        var recall = truePositives / (double)intendedVariants;
        var precision = truePositives / (double)returnedCandidates;
        var unrelatedComparisons = intendedVariants * (baseImageCount - 1);

        Assert.Equal(50, bases.Count);
        Assert.Equal(250, intendedVariants);
        Assert.True(unrelatedComparisons >= 2_500);
        Assert.True(
            recall == 1D,
            $"Recall was {recall:P2}; min true {minimumTrueScore:F3}, max false {maximumFalseScore:F3}; misses: {string.Join(", ", missed.Take(40))}.");
        Assert.True(precision >= 0.98, $"Precision was {precision:P2}.");
        Assert.True(candidateCounts[p95Index] <= 25, $"P95 candidate count was {candidateCounts[p95Index]}.");
    }

    private static Image<Rgba32> CreateSparseGraphic(int x, int y, Rgba32 color)
    {
        var image = new Image<Rgba32>(256, 256, new Rgba32(0, 0, 0, 0));
        PaintRectangle(image, x, y, 28, 12, color);
        PaintRectangle(image, x + 8, y - 8, 12, 28, color);
        return image;
    }

    private static void PaintRectangle(
        Image<Rgba32> image,
        int x,
        int y,
        int width,
        int height,
        Rgba32 color)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var currentY = y; currentY < y + height; currentY++)
            {
                var row = accessor.GetRowSpan(currentY);
                for (var currentX = x; currentX < x + width; currentX++)
                {
                    row[currentX] = color;
                }
            }
        });
    }

    private static ImageFingerprint CreateAnimationFingerprint(
        IReadOnlyList<Image<Rgba32>> frames,
        IReadOnlyList<int>? delays = null)
    {
        var decodedFrames = frames.Select((frame, index) =>
        {
            var pixels = new byte[checked(frame.Width * frame.Height * 4)];
            frame.CopyPixelDataTo(pixels);
            return new DecodedImageFrame(pixels, delays?[index] ?? 80 + (index * 10));
        }).ToArray();
        return ImageFingerprint.Create(new DecodedImage(
            frames[0].Width,
            frames[0].Height,
            "fixture",
            decodedFrames));
    }

    [Fact]
    public void SamePictureCoversAnExactMatch()
    {
        using var image = ImageFixtureFactory.CreatePattern(310);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var match = new ImageMatchResult("candidate", MatchKind.Exact, 1, ["Exact"]);

        Assert.True(ImageMatcher.IsSamePicture(match, fingerprint, fingerprint));
    }

    /// <summary>
    /// The audit's reproduction, as a test that fails while the defect is present.
    /// </summary>
    /// <remarks>
    /// The old eight-frame sampling scored these different animations at 100%. All-frame
    /// summaries must lower that score, and a forged perfect score must still not imply identity.
    /// </remarks>
    [Fact]
    public void ChangedUnsampledFramesLowerScoreAndNeverBecomeExact()
    {
        var redFrames = CreateHalfDifferingAnimation(new Rgba32(220, 30, 30, 255));
        var blueFrames = CreateHalfDifferingAnimation(new Rgba32(30, 30, 220, 255));
        try
        {
            var delays = Enumerable.Repeat(80, redFrames.Count).ToArray();
            var incoming = CreateAnimationFingerprint(redFrames, delays);
            var archived = CreateAnimationFingerprint(blueFrames, delays);

            Assert.NotEqual(incoming.ExactIdentity, archived.ExactIdentity);

            var results = ImageMatcher.RankCandidates(
                incoming,
                [new ImageCandidate("archived", archived)],
                SimilarityProfile.Strict);

            Assert.Empty(results);
            var reloaded = JsonSerializer.Deserialize<ImageFingerprint>(JsonSerializer.Serialize(archived))!;
            Assert.True(reloaded.HasCurrentFeatures);
            Assert.Equal(16, reloaded.AnimationFrameSummaries!.Count);
            Assert.True(ImageMatcher.MeasureSimilarity(incoming, reloaded).Score < .99);
            Assert.False(ImageMatcher.IsSamePicture(new("archived", MatchKind.Similar, 1, []), incoming, archived));
        }
        finally
        {
            foreach (var frame in redFrames.Concat(blueFrames))
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void SamePictureRejectsASmallStillImageChange()
    {
        using var image = ImageFixtureFactory.CreatePattern(311);
        using var nearly = ImageFixtureFactory.CreateNearDuplicate(image);
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(nearly));
        var archived = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var match = new ImageMatchResult("candidate", MatchKind.Similar, 1, ["reason"]);

        // Same size, same frame count, a perfect score handed to it - and still not the same
        // picture. Nothing short of the decoded content may put a file in the Recycle Bin.
        Assert.False(ImageMatcher.IsSamePicture(match, incoming, archived));
    }

    [Fact]
    public void SamePictureStillCoversTheSameDecodedContent()
    {
        using var image = ImageFixtureFactory.CreatePattern(314);
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var archived = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var results = ImageMatcher.RankCandidates(
            incoming,
            [new ImageCandidate("archived", archived)],
            SimilarityProfile.Strict);

        var match = Assert.Single(results);
        Assert.Equal(MatchKind.Exact, match.MatchKind);
        Assert.True(ImageMatcher.IsSamePicture(match, incoming, archived));
    }

    /// <summary>
    /// Sixteen frames whose odd-numbered ones are shared and whose even-numbered ones carry
    /// <paramref name="stripe"/>.
    /// </summary>
    /// <remarks>
    /// The fingerprint samples eight frames by playback time, and with equal delays those land on
    /// the odd indices. Building the difference into the even ones is what makes the pair invisible
    /// to the score - which is the whole point of the reproduction.
    /// </remarks>
    private static IReadOnlyList<Image<Rgba32>> CreateHalfDifferingAnimation(Rgba32 stripe)
    {
        var frames = new List<Image<Rgba32>>(16);
        for (var index = 0; index < 16; index++)
        {
            var frame = ImageFixtureFactory.CreatePattern(400 + (index % 2 == 0 ? 0 : index));
            if (index % 2 == 0)
            {
                PaintRectangle(frame, 0, 0, frame.Width, frame.Height, stripe);
            }

            frames.Add(frame);
        }

        return frames;
    }

    [Fact]
    public void SamePictureRejectsAHundredPercentScoreAtADifferentResolution()
    {
        using var image = ImageFixtureFactory.CreatePattern(312);
        using var larger = image.Clone(context => context.Resize(image.Width * 2, image.Height * 2));
        var incoming = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(larger));
        var archived = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var match = new ImageMatchResult("candidate", MatchKind.Similar, 1, ["reason"]);

        // Which resolution to keep is the user's decision, so this still goes to review.
        Assert.False(ImageMatcher.IsSamePicture(match, incoming, archived));
    }

    [Fact]
    public void SamePictureRejectsAScoreBelowTheDisplayedThreshold()
    {
        using var image = ImageFixtureFactory.CreatePattern(313);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var match = new ImageMatchResult(
            "candidate",
            MatchKind.Similar,
            ImageMatcher.DisplayedAsIdenticalThreshold - 0.001,
            ["reason"]);

        Assert.False(ImageMatcher.IsSamePicture(match, fingerprint, fingerprint));
    }
}
