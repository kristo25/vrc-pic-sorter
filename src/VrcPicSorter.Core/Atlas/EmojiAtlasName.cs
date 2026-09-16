using System.Text.RegularExpressions;

namespace VrcPicSorter.Core.Atlas;

public enum AtlasLoopStyle
{
    Linear,
    PingPong,
}

/// <summary>
/// The animation VRChat recorded in an emoji's file name, for example
/// <c>..._stopanimationStyle_64frames_31fps_linearloopStyle.png</c>.
/// </summary>
/// <remarks>
/// Measured against 191 archived emoji, this is a better source of truth than examining the
/// pixels: deriving the layout from the stated frame count was right on 55 of 56 sheets, where
/// periodicity detection over the same images was right on 43. The name also carries the playback
/// rate and the loop direction, neither of which can be recovered from a still image at all.
/// </remarks>
public sealed record EmojiAtlasName(int FrameCount, int FramesPerSecond, AtlasLoopStyle LoopStyle)
{
    /// <summary>A single frame is a still emoji, not an animation.</summary>
    public const int MinimumFrameCount = 2;

    /// <summary>Far above anything observed (the largest seen is 64) and low enough that a
    /// malformed name cannot ask for an unbounded amount of work.</summary>
    public const int MaximumFrameCount = 1024;

    public const int MaximumFramesPerSecond = 240;

    /// <summary>
    /// What a sheet can be. A sheet is one still image; an already animated file is what this
    /// feature produces, not what it consumes - and VRChat's exported GIF carries the very same
    /// name as the sheet it came from, so the name alone cannot tell them apart.
    /// </summary>
    private static readonly HashSet<string> SheetExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    private static readonly Regex Pattern = new(
        @"_(?<frames>\d{1,4})frames_(?<fps>\d{1,3})fps(?:_(?<loop>[a-z]+)loopStyle)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads the animation out of a file name or path. Returns false for anything that does not
    /// carry the pattern, which includes every still emoji, so this doubles as the test for
    /// whether a file is a sprite sheet at all.
    /// </summary>
    public static bool TryParse(string? fileNameOrPath, out EmojiAtlasName result)
    {
        result = new EmojiAtlasName(0, 0, AtlasLoopStyle.Linear);
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        try
        {
            if (!SheetExtensions.Contains(Path.GetExtension(Path.GetFileName(fileNameOrPath))))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return TryReadAnimation(fileNameOrPath, out result);
    }

    /// <summary>
    /// Reads what VRChat wrote about the animation, whatever kind of file is carrying it.
    /// </summary>
    /// <remarks>
    /// The same pattern as <see cref="TryParse"/> without its question about sprite sheets.
    /// VRChat gives its own exported GIF the very same name as the sheet it came from, so the
    /// frame count, rate and loop direction are there to be read on an animation too - and two
    /// animations of one emoji that agree on all three are the same animation, however differently
    /// they were encoded. <see cref="TryParse"/> answers "is this a sheet" and rightly says no to
    /// a GIF; this answers "what does its name say it plays", which is a different question.
    /// </remarks>
    public static bool TryReadAnimation(string? fileNameOrPath, out EmojiAtlasName result)
    {
        result = new EmojiAtlasName(0, 0, AtlasLoopStyle.Linear);
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        Match match;
        try
        {
            match = Pattern.Match(Path.GetFileName(fileNameOrPath));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Path.GetFileName rejects nothing on modern .NET, but a caller passing a raw name
            // with invalid characters should be treated as "not a sheet" rather than throwing.
            return false;
        }

        if (!match.Success
            || !int.TryParse(match.Groups["frames"].Value, out var frames)
            || !int.TryParse(match.Groups["fps"].Value, out var fps)
            || frames < MinimumFrameCount
            || frames > MaximumFrameCount
            || fps < 1
            || fps > MaximumFramesPerSecond)
        {
            return false;
        }

        var loop = match.Groups["loop"].Success
            && match.Groups["loop"].Value.Equals("pingpong", StringComparison.OrdinalIgnoreCase)
                ? AtlasLoopStyle.PingPong
                : AtlasLoopStyle.Linear;
        result = new EmojiAtlasName(frames, fps, loop);
        return true;
    }
}
