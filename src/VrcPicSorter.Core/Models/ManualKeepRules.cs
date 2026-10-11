using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Core.Models;

/// <summary>Manual decisions apply to the selected file identity, not later copies.</summary>
public static class ManualKeepRules
{
    public static bool IsProtected(AppStateDocument state, IndexedImageRecord image) =>
        state.ManualKeeps.Any(keep => SamePath(keep.Path, image.Path)
            && keep.FileSize == image.FileSize && keep.LastWriteUtc == image.LastWriteUtc
            && (keep.DecoderMigrationUnresolved || (!string.IsNullOrWhiteSpace(keep.Fingerprint)
                && string.Equals(keep.Fingerprint, image.ExactFingerprint, StringComparison.Ordinal))));

    public static void Repoint(AppStateDocument state, string source, string destination)
    {
        FilterKeepRules.Repoint(state, source, destination);
        for (var i = 0; i < state.ManualKeeps.Count; i++)
        {
            if (SamePath(state.ManualKeeps[i].Path, source))
                state.ManualKeeps[i] = state.ManualKeeps[i] with { Path = destination };
        }
    }

    public static string? AnimationKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || (!AtlasAnimationWriter.IsAnimation(path) && !EmojiAtlasName.TryParse(path, out _))
            || !EmojiAtlasName.TryReadAnimation(path, out var name)) return null;
        return $"{EmojiIdentity.KeyFor(path).ToUpperInvariant()}|{name.FrameCount}|{name.FramesPerSecond}|{name.LoopStyle}";
    }

    public static bool IsAnimationSuppressed(AppStateDocument state, string path) =>
        AnimationKey(path) is { } key && state.SuppressedAnimations.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool SamePath(string first, string second)
    {
        try { return string.Equals(PathBoundary.Normalize(first), PathBoundary.Normalize(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
