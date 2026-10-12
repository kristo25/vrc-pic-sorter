using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class RemovalDetectorTests
{
    private const string Id = "de65e61a-b986-4072-ac4c-c4ed590d01f5";

    [Theory]
    [InlineData("Player_2026-10-03_20-23-10.000_prnt_", "Player")]
    [InlineData("Player_2026-10-03_20-23-10_inv_", "Player")]
    [InlineData("DemonWolF_1999_2026-10-03_20-23-10.001_inv_", "DemonWolF_1999")]
    [InlineData("ʚ moonlight ɞ_inv_", "ʚ moonlight ɞ")]
    [InlineData("_Player__inv_", "_Player_")]
    public void CompleteAuthorIsMatched(string prefix, string author)
    {
        Assert.True(FilenameAuthor.IsExactMatch(prefix + Id + ".png", author));
        Assert.False(FilenameAuthor.IsExactMatch(prefix + Id + ".png", author + "x"));
        Assert.False(FilenameAuthor.IsExactMatch(prefix + Id + ".png", author[..^1]));
    }

    [Theory]
    [InlineData("Player_inv_", "player")]
    [InlineData("Player_inv_", " Player")]
    [InlineData("Player_inv_", "")]
    [InlineData("Player_2026-99-03_20-23-10.000_prnt_", "Player")]
    [InlineData("Player_2026-10-03_20-23-10.000_unknown_", "Player")]
    public void CaseWhitespaceAndUnsupportedNamesDoNotMatch(string prefix, string author)
    {
        Assert.False(FilenameAuthor.IsExactMatch(prefix + Id + ".png", author));
    }

    [Fact]
    public void EmojiAnimationAndCopySuffixAreSupportedWithoutPartialAuthorMatch()
    {
        var path = $@"C:\images\A_B_inv_{Id}_snowanimationStyle_32frames_10fps_linearloopStyle (2).gif";
        Assert.True(FilenameAuthor.IsExactMatch(path, "A_B"));
        Assert.False(FilenameAuthor.IsExactMatch(path, "A"));
        Assert.False(FilenameAuthor.IsExactMatch($"A_inv_{Id}_B_inv_{Id}.png", "A"));
        Assert.False(FilenameAuthor.IsExactMatch($"A_inv_{Id}_B_inv_{Id}.png", $"A_inv_{Id}_B"));
        Assert.False(FilenameAuthor.IsExactMatch("A_inv_not-a-uuid.png", "A"));
        Assert.False(FilenameAuthor.IsExactMatch($"A_inv_{Id}.txt", "A"));
        Assert.False(FilenameAuthor.IsExactMatch($"A_inv_{Id}_unrelated-note.png", "A"));
    }

    [Theory]
    [InlineData(2048, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(1024, 720)]
    public void WhiteBackgroundAndDimensionsAloneNeverQualify(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var image = new DecodedImage(width, height, "PNG", [new(pixels, 0)]);
        Assert.Equal(PrintFrameMatch.None, PrintFrameDetector.Detect(image));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FooterSignalsWorkWithAnEntirelyWhitePicture(bool upperCase)
    {
        Assert.Equal(PrintFrameMatch.Confirmed, PrintFrameDetector.Detect(Frame(upperCase)));
    }

    [Theory]
    [InlineData(64, 1390, 206, 38, PrintFrameMatch.None)]
    [InlineData(282, 1390, 23, 38, PrintFrameMatch.Uncertain)]
    [InlineData(310, 1390, 790, 38, PrintFrameMatch.Uncertain)]
    [InlineData(1760, 1390, 36, 38, PrintFrameMatch.Uncertain)]
    [InlineData(1798, 1390, 188, 38, PrintFrameMatch.Uncertain)]
    public void EachFooterSignalIsRequired(int x, int y, int width, int height, PrintFrameMatch expected)
    {
        var image = Frame(false);
        Fill(image.Frames[0].RgbaPixels, x, y, width, height, 255);
        Assert.Equal(expected, PrintFrameDetector.Detect(image));
    }

    [Fact]
    public void NonwhiteOrTransparentBordersAndAnimatedImagesCannotAutoRemove()
    {
        var border = Frame(false);
        Fill(border.Frames[0].RgbaPixels, 0, 0, 2048, 60, 0);
        Assert.Equal(PrintFrameMatch.None, PrintFrameDetector.Detect(border));
        var transparent = Frame(false);
        for (var i = 3; i < 2048 * 60 * 4; i += 4) transparent.Frames[0].RgbaPixels[i] = 0;
        Assert.Equal(PrintFrameMatch.None, PrintFrameDetector.Detect(transparent));
        var animated = new DecodedImage(2048, 1440, "GIF", [Frame(false).Frames[0], Frame(false).Frames[0]]);
        Assert.Equal(PrintFrameMatch.None, PrintFrameDetector.Detect(animated));
    }

    [Fact]
    public void InvalidCalendarDateRequiresReview()
    {
        Assert.Equal(PrintFrameMatch.Uncertain, PrintFrameDetector.Detect(Frame(false, "02/40/2024")));
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1273)]
    [InlineData(1347)]
    public void WorldFooterCanMoveCalendarAndDateWithoutLosingConfirmation(int calendarX)
    {
        var image = Frame(true, calendarX: calendarX);
        Fill(image.Frames[0].RgbaPixels, calendarX + 235, 1394, 30, 26, 0);
        Assert.Equal(PrintFrameMatch.Confirmed, PrintFrameDetector.Detect(image));
    }

    [Fact]
    public void ShiftedDateCannotSubstituteForPhotographerName()
    {
        var image = Frame(true, calendarX: 700);
        Fill(image.Frames[0].RgbaPixels, 310, 1390, 374, 38, 255);
        Assert.Equal(PrintFrameMatch.Uncertain, PrintFrameDetector.Detect(image));
    }

    [Fact]
    public void CalendarAndDateMustRemainAdjacent()
    {
        var image = Frame(true);
        Fill(image.Frames[0].RgbaPixels, 1760, 1390, 36, 38, 255);
        Draw(image.Frames[0].RgbaPixels, PrintFrameTemplates.Calendar, 900, 1390, 36, 38);
        Assert.Equal(PrintFrameMatch.Uncertain, PrintFrameDetector.Detect(image));
    }

    [Theory]
    [InlineData('0', 0)]
    [InlineData('0', 1)]
    [InlineData('/', 0)]
    public void NativeSubpixelDateGlyphVariantsRemainRecognizable(char character, int variant)
    {
        const int calendarX = 1273;
        var image = Frame(true, calendarX: calendarX);
        var index = "12/11/2024".IndexOf(character);
        var left = calendarX + 40 + index * 18;
        Fill(image.Frames[0].RgbaPixels, left, 1392, 16, 32, 255);
        Draw(image.Frames[0].RgbaPixels, PrintFrameTemplates.DateGlyphVariants[character][variant], left, 1392, 16, 32);
        Assert.Equal(PrintFrameMatch.Confirmed, PrintFrameDetector.Detect(image));
    }

    [Theory]
    [InlineData("03/17/2094")]
    [InlineData("07/19/2034")]
    public void AllDateDigitsSupportValidDates(string date)
    {
        Assert.Equal(PrintFrameMatch.Confirmed, PrintFrameDetector.Detect(Frame(true, date, 1273)));
    }

    [Theory]
    [InlineData("03/37/2094")]
    [InlineData("07/39/2034")]
    public void InvalidCalendarDatesRequireReview(string date)
    {
        Assert.Equal(PrintFrameMatch.Uncertain, PrintFrameDetector.Detect(Frame(true, date, 1273)));
    }

    // Synthetic glyph composition tests the conjunction of required signals. Real-image
    // recognition and frozen held-out families are independently checked by tools/filter-eval.
    internal static DecodedImage Frame(bool upper, string date = "12/11/2024", int calendarX = 1760)
    {
        var pixels = Enumerable.Repeat((byte)255, 2048 * 1440 * 4).ToArray();
        Draw(pixels, upper ? PrintFrameTemplates.CaptionUpper : PrintFrameTemplates.CaptionLower, 64, 1390, 206, 38);
        Draw(pixels, upper ? PrintFrameTemplates.PersonUpper : PrintFrameTemplates.PersonLower, 282, 1390, 23, 38);
        Draw(pixels, PrintFrameTemplates.Calendar, calendarX, 1390, 36, 38);
        Fill(pixels, 318, 1400, 10, 20, 0);
        for (var i = 0; i < date.Length; i++) Draw(pixels, PrintFrameTemplates.DateGlyphs[date[i]], calendarX + 40 + i * 18, 1392, 16, 32);
        return new DecodedImage(2048, 1440, "PNG", [new(pixels, 0)]);
    }

    private static void Draw(byte[] pixels, byte[] mask, int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var bit = row * width + column;
                if ((mask[bit / 8] & (1 << (bit % 8))) != 0) Fill(pixels, x + column, y + row, 1, 1, 0);
            }
    }

    private static void Fill(byte[] pixels, int x, int y, int width, int height, byte color)
    {
        for (var row = y; row < y + height; row++)
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * 2048 + column) * 4;
                Array.Fill(pixels, color, offset, 3);
            }
    }
}
