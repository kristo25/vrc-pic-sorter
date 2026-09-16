using VrcPicSorter.Core.Atlas;

namespace VrcPicSorter.Tests.Atlas;

public sealed class EmojiIdentityTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("player_inv_11111111-2222-3333-4444-555555555555_stopanimationStyle_4frames_10fps.png")]
    [InlineData("player_inv_11111111-2222-3333-4444-555555555555_stopanimationStyle_4frames_10fps (2).gif")]
    [InlineData(@"C:\archive\Animated\player_inv_11111111-2222-3333-4444-555555555555_x (2) (3).gif")]
    public void TheInventoryIdIsReadWhateverElseHappenedToTheName(string path) =>
        Assert.Equal(Id, EmojiIdentity.TryReadInventoryId(path));

    [Fact]
    public void AFileWithNoInventoryIdHasNone() =>
        Assert.Null(EmojiIdentity.TryReadInventoryId("holiday-photo.png"));

    /// <summary>
    /// The whole point: a sheet and the animation made from it stay the same emoji after Windows,
    /// a sync client or the archiver itself has added a number to one of them.
    /// </summary>
    [Fact]
    public void ASheetAndItsNumberedAnimationAreTheSameEmoji() =>
        Assert.True(EmojiIdentity.IsSameEmoji(
            @"C:\archive\Emoji\Animated\p_inv_11111111-2222-3333-4444-555555555555_x (2).gif",
            @"C:\archive\Emoji\Animated\Gif Ref\p_inv_11111111-2222-3333-4444-555555555555_x.png"));

    [Fact]
    public void TwoDifferentEmojiAreNotTheSameEmoji() =>
        Assert.False(EmojiIdentity.IsSameEmoji(
            "p_inv_11111111-2222-3333-4444-555555555555_x.gif",
            "p_inv_99999999-2222-3333-4444-555555555555_x.gif"));

    /// <summary>Without an id there is only the name, so the copy numbers come off instead.</summary>
    [Theory]
    [InlineData("holiday.png", "holiday (2).gif", true)]
    [InlineData("holiday.png", "holiday (2) (3).gif", true)]
    [InlineData("holiday.png", "holiday-other.gif", false)]
    public void WithoutAnIdTheNameIsComparedWithItsCopyNumbersRemoved(
        string first,
        string second,
        bool expected) =>
        Assert.Equal(expected, EmojiIdentity.IsSameEmoji(first, second));

    [Fact]
    public void ANameThatIsNothingButACopyNumberStillHasAKey() =>
        Assert.False(string.IsNullOrWhiteSpace(EmojiIdentity.KeyFor("(2).png")));
}
