using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;

namespace VrcPicSorter.Tests.Scanning;

public sealed class ArchiveDuplicateFinderTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void AnArchiveHoldingEachImageOnceHasNothingToReport() =>
        Assert.Empty(ArchiveDuplicateFinder.Find(Index(
            Record(@"C:\a\one.png", "AAA"),
            Record(@"C:\a\two.png", "BBB"))));

    /// <summary>
    /// The copy number is what the archiver adds when the name it wanted was taken, so the file
    /// without one is the original and the numbered ones are what gathered around it.
    /// </summary>
    [Fact]
    public void TheCopyWithoutANumberOnItIsTheOneKept()
    {
        var group = Assert.Single(ArchiveDuplicateFinder.Find(Index(
            Record(@"C:\a\emoji (2).png", "AAA", size: 10),
            Record(@"C:\a\emoji.png", "AAA", size: 10),
            Record(@"C:\a\emoji (3).png", "AAA", size: 10))));

        Assert.Equal(ArchiveDuplicateKind.Identical, group.Kind);
        Assert.Equal(@"C:\a\emoji.png", group.Keep.Path);
        Assert.Equal(
            [@"C:\a\emoji (2).png", @"C:\a\emoji (3).png"],
            group.Extras.Select(extra => extra.Path).Order(StringComparer.Ordinal));
        Assert.Equal(20, group.ReclaimableBytes);
    }

    /// <summary>
    /// VRChat's own GIF beside the one exported from the sheet: the same emoji, not the same
    /// picture. Reported, never removed, because which is worth keeping is a judgement.
    /// </summary>
    [Fact]
    public void TheSameEmojiAtTwoSizesIsReportedRatherThanCollapsed()
    {
        var group = Assert.Single(ArchiveDuplicateFinder.Find(Index(
            Record($@"C:\a\Animated\p_inv_{Id}_x (2).gif", "AAA", width: 128, height: 74),
            Record($@"C:\a\Animated\p_inv_{Id}_x.gif", "BBB", width: 256, height: 256))));

        Assert.Equal(ArchiveDuplicateKind.SameEmoji, group.Kind);

        // The larger one holds more of the picture, so that is the one put forward.
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x.gif", group.Keep.Path);
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x (2).gif", Assert.Single(group.Extras).Path);
    }

    /// <summary>
    /// A sheet and the animation cut from it share an emoji and are both meant to be here. Calling
    /// them duplicates would offer to throw away half the feature.
    /// </summary>
    [Fact]
    public void ASheetAndItsOwnAnimationAreNotDuplicatesOfEachOther() =>
        Assert.Empty(ArchiveDuplicateFinder.Find(Index(
            Record($@"C:\a\Animated\p_inv_{Id}_x_4frames_10fps.gif", "AAA"),
            Record($@"C:\a\Animated\Gif Ref\p_inv_{Id}_x_4frames_10fps.png", "BBB"))));

    /// <summary>
    /// Three copies, two of them identical: one identical pair, and the survivor of it measured
    /// against the odd one out. The alternative is reporting the same file in two groups at once.
    /// </summary>
    [Fact]
    public void IdenticalCopiesCollapseBeforeTheRestAreCompared()
    {
        var groups = ArchiveDuplicateFinder.Find(Index(
            Record($@"C:\a\Animated\p_inv_{Id}_x.gif", "AAA", width: 256, height: 256),
            Record($@"C:\a\Animated\p_inv_{Id}_x (2).gif", "AAA", width: 256, height: 256),
            Record($@"C:\a\Animated\p_inv_{Id}_x (3).gif", "BBB", width: 128, height: 74)));

        Assert.Equal(2, groups.Count);
        var identical = groups.Single(group => group.Kind == ArchiveDuplicateKind.Identical);
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x.gif", identical.Keep.Path);
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x (2).gif", Assert.Single(identical.Extras).Path);

        var sameEmoji = groups.Single(group => group.Kind == ArchiveDuplicateKind.SameEmoji);
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x.gif", sameEmoji.Keep.Path);
        Assert.Equal($@"C:\a\Animated\p_inv_{Id}_x (3).gif", Assert.Single(sameEmoji.Extras).Path);
    }

    /// <summary>A record with no fingerprint says nothing about being a copy of anything.</summary>
    [Fact]
    public void RecordsWithNoFingerprintAreNotCalledIdentical() =>
        Assert.Empty(ArchiveDuplicateFinder.Find(Index(
            Record(@"C:\a\one.png", string.Empty),
            Record(@"C:\a\two.png", string.Empty))));

    private static CategoryIndexState Index(params IndexedImageRecord[] images) =>
        new()
        {
            Category = VrcImageCategory.Emoji,
            Status = IndexStatus.Current,
            Images = [.. images],
        };

    private static IndexedImageRecord Record(
        string path,
        string fingerprint,
        int width = 64,
        int height = 64,
        long size = 1) =>
        new()
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Path = path,
            FileSize = size,
            Width = width,
            Height = height,
            ExactFingerprint = fingerprint,
        };
}
