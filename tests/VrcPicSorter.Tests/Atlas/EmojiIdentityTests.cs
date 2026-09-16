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

    /// <summary>
    /// "Is this a sheet" and "what does its name say it plays" are different questions, and a GIF
    /// answers no to the first and yes to the second.
    /// </summary>
    /// <remarks>
    /// Reading them as one question is what stopped the app noticing that VRChat's own GIF and its
    /// own export of the same sheet play the very same animation: every check gave up the moment
    /// it saw a .gif.
    /// </remarks>
    [Fact]
    public void AnAnimationCarriesWhatItPlaysEvenThoughItIsNotASheet()
    {
        const string sheet =
            "_ShadowRogue__inv_b191ba0d-fdcd-427b-8386-3daab2b8cff9"
            + "_stopanimationStyle_64frames_12fps_linearloopStyle.png";
        const string animation =
            "_ShadowRogue__inv_b191ba0d-fdcd-427b-8386-3daab2b8cff9"
            + "_stopanimationStyle_64frames_12fps_linearloopStyle (2).gif";

        Assert.True(EmojiAtlasName.TryParse(sheet, out var fromSheet));
        Assert.False(EmojiAtlasName.TryParse(animation, out _));
        Assert.True(EmojiAtlasName.TryReadAnimation(animation, out var fromAnimation));

        Assert.Equal(fromSheet, fromAnimation);
        Assert.Equal(new EmojiAtlasName(64, 12, AtlasLoopStyle.Linear), fromAnimation);
    }

    [Fact]
    public void AStillEmojiNameSaysNothingAboutPlayingAnything() =>
        Assert.False(EmojiAtlasName.TryReadAnimation(
            "Qwen3_inv_b680967b-2295-4555-b1ef-0b445b194085_stopanimationStyle.png",
            out _));
}
