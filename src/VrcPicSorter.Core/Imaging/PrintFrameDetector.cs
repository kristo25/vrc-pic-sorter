using System.Globalization;

namespace VrcPicSorter.Core.Imaging;

public enum PrintFrameMatch { None, Uncertain, Confirmed }

/// <summary>
/// Conservative recognition of VRChat's Print footer, independent of picture content.
/// Resized exports and damaged or unrecognized footer signals require review.
/// </summary>
public static class PrintFrameDetector
{
    public static PrintFrameMatch Detect(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Frames.Count != 1 || image.Width < 512 || image.Height < 360 ||
            Math.Abs((double)image.Width / image.Height - 2048d / 1440) > .005)
            return PrintFrameMatch.None;

        var pixels = new FramePixels(image);
        var caption = pixels.Mask(64, 1390, 206, 38);
        var captionScore = Math.Max(Similarity(caption, PrintFrameTemplates.CaptionLower),
            Similarity(caption, PrintFrameTemplates.CaptionUpper));
        if (captionScore < .85 || !HasFrameGeometry(pixels)) return PrintFrameMatch.None;

        // A damaged caption can suggest a frame, but never authorize removal.
        if (captionScore < .98 || image.Width != 2048 || image.Height != 1440)
            return PrintFrameMatch.Uncertain;

        var person = pixels.Mask(282, 1390, 23, 38);
        var personScore = Math.Max(Similarity(person, PrintFrameTemplates.PersonLower),
            Similarity(person, PrintFrameTemplates.PersonUpper));
        if (personScore < .98 || !HasDatedFooter(pixels)) return PrintFrameMatch.Uncertain;
        return PrintFrameMatch.Confirmed;
    }

    private static bool HasDatedFooter(FramePixels pixels)
    {
        // World metadata moves the date left; the icon and date must still form one block.
        for (var left = 700; left <= 1760; left++)
        {
            if (pixels.CalendarSimilarity(left) < .98 ||
                !HasPhotographerName(pixels, Math.Min(1100, left - 16)) ||
                !HasRecognizedDate(pixels, left + 38)) continue;
            return true;
        }
        return false;
    }

    private static bool HasPhotographerName(FramePixels pixels, int right)
    {
        var ink = 0;
        for (var y = 1390; y < 1428; y++)
            for (var x = 310; x < right; x++)
                if (pixels.Dark(x, y) && ++ink >= 8) return true;
        return false;
    }

    private static bool HasFrameGeometry(FramePixels pixels) =>
        pixels.White(0, 0, 2048, 60) && pixels.White(0, 60, 60, 1100) &&
        pixels.White(1988, 60, 60, 1100) && pixels.White(0, 1434, 2048, 6) &&
        pixels.White(64, 1260, 1920, 110);

    private static bool HasRecognizedDate(FramePixels pixels, int leftEdge)
    {
        var runs = new List<(int Left, int Right)>();
        for (var x = leftEdge; x < leftEdge + 188; x++)
        {
            var ink = false;
            for (var y = 1390; y < 1428; y++) ink |= pixels.Dark(x, y);
            if (!ink) continue;
            if (runs.Count != 0 && runs[^1].Right == x) runs[^1] = (runs[^1].Left, x + 1);
            else runs.Add((x, x + 1));
        }
        if (runs.Count != 10) return false;
        var recognized = new char[10];
        for (var index = 0; index < runs.Count; index++)
        {
            var (left, right) = runs[index];
            var top = 1428;
            var bottom = 1390;
            for (var y = 1390; y < 1428; y++)
                for (var x = left; x < right; x++)
                    if (pixels.Dark(x, y)) { top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1); }
            if (bottom - top < 15 || right - left > 20) return false;
            var glyph = new byte[64];
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 16; x++)
                    if (pixels.Dark(left + (int)((x + .5) * (right - left) / 16),
                            top + (int)((y + .5) * (bottom - top) / 32)))
                        Set(glyph, y * 16 + x);
            var scores = PrintFrameTemplates.DateGlyphs
                .Select(template => (Character: template.Key, Score: DateGlyphScore(glyph, template.Key, template.Value)))
                .OrderByDescending(result => result.Score).ToArray();
            if (scores[0].Score < .90 || scores[0].Score - scores[1].Score < .06) return false;
            recognized[index] = scores[0].Character;
        }
        return DateOnly.TryParseExact(new string(recognized), "MM/dd/yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static double DateGlyphScore(byte[] glyph, char character, byte[] template)
    {
        var score = Similarity(glyph, template);
        if (PrintFrameTemplates.DateGlyphVariants.TryGetValue(character, out var variants))
            foreach (var variant in variants) score = Math.Max(score, Similarity(glyph, variant));
        return score;
    }

    private static double Similarity(byte[] actual, byte[] expected)
    {
        var intersection = 0;
        var count = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            intersection += System.Numerics.BitOperations.PopCount((uint)(actual[i] & expected[i]));
            count += System.Numerics.BitOperations.PopCount((uint)actual[i]) +
                System.Numerics.BitOperations.PopCount((uint)expected[i]);
        }
        return count == 0 ? 0 : 2d * intersection / count;
    }

    private static void Set(byte[] mask, int bit) => mask[bit / 8] |= (byte)(1 << (bit % 8));

    private sealed class FramePixels(DecodedImage image)
    {
        private readonly byte[] data = image.Frames[0].RgbaPixels;
        private byte[]? calendarStrip;

        private int Offset(int x, int y) =>
            ((y * image.Height / 1440) * image.Width + x * image.Width / 2048) * 4;

        internal bool Dark(int x, int y)
        {
            var offset = Offset(x, y);
            return data[offset + 3] >= 250 &&
                (299 * data[offset] + 587 * data[offset + 1] + 114 * data[offset + 2]) < 180000;
        }

        internal double CalendarSimilarity(int left)
        {
            const int first = 700;
            const int stripWidth = 1760 + 36 - first;
            if (calendarStrip is null)
            {
                calendarStrip = new byte[stripWidth * 38];
                for (var y = 0; y < 38; y++)
                    for (var x = 0; x < stripWidth; x++)
                        calendarStrip[y * stripWidth + x] = Dark(first + x, 1390 + y) ? (byte)1 : (byte)0;
            }
            var intersection = 0;
            var count = 0;
            for (var y = 0; y < 38; y++)
                for (var x = 0; x < 36; x++)
                {
                    var bit = y * 36 + x;
                    var expected = (PrintFrameTemplates.Calendar[bit / 8] >> (bit % 8)) & 1;
                    var actual = calendarStrip[y * stripWidth + left - first + x];
                    intersection += actual & expected;
                    count += actual + expected;
                }
            return count == 0 ? 0 : 2d * intersection / count;
        }

        internal byte[] Mask(int left, int top, int width, int height)
        {
            var mask = new byte[(width * height + 7) / 8];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (Dark(left + x, top + y)) Set(mask, y * width + x);
            return mask;
        }

        internal bool White(int left, int top, int width, int height)
        {
            var rejected = 0;
            var maximum = width * height / 500; // At least 99.8% opaque white border pixels.
            for (var y = top; y < top + height; y++)
                for (var x = left; x < left + width; x++)
                {
                    var offset = Offset(x, y);
                    if (data[offset] < 245 || data[offset + 1] < 245 || data[offset + 2] < 245 || data[offset + 3] < 250)
                        if (++rejected > maximum) return false;
                }
            return true;
        }
    }
}
