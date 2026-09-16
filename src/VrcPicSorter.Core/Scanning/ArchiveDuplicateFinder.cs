using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public enum ArchiveDuplicateKind
{
    /// <summary>The same picture twice, pixel for pixel. One copy is all there is to keep.</summary>
    Identical,

    /// <summary>
    /// The same animation of the same emoji, encoded twice: VRChat's own GIF beside the one this
    /// app exported from the sheet. Not the same bytes, but the same emoji and the same frame
    /// count, rate and loop direction - which is VRChat's own account of its own inventory item,
    /// not a resemblance anything guessed at.
    /// </summary>
    SameAnimation,

    /// <summary>
    /// The same emoji held more than once, and nothing says the two are the same picture - a still
    /// beside another still, or animations whose names disagree about what they play. Which one is
    /// worth keeping is a judgement, so these are only ever reported.
    /// </summary>
    SameEmoji,
}

/// <param name="Keep">The copy worth keeping, by the rules in <see cref="ArchiveDuplicateFinder"/>.</param>
/// <param name="Extras">Everything else in the group, ordered the same way.</param>
public sealed record ArchiveDuplicateGroup(
    ArchiveDuplicateKind Kind,
    IndexedImageRecord Keep,
    IReadOnlyList<IndexedImageRecord> Extras)
{
    public long ReclaimableBytes => Extras.Sum(item => item.FileSize);
}

/// <summary>
/// What the archive is holding more than once.
/// </summary>
/// <remarks>
/// Deduplication has only ever run one way - an incoming image against the archive - so once two
/// copies are both inside, nothing notices them again. That is fine while every copy arrives
/// through a scan, and it stops being fine the moment anything else puts a file there: a rename, a
/// restored backup, a sync client, or the app writing an export beside one it failed to recognise.
/// This reads the index that is already built, so it costs no disk work at all.
/// </remarks>
public static class ArchiveDuplicateFinder
{
    /// <summary>
    /// Identical copies first, then what remains of the same emoji. A group of identical copies is
    /// collapsed to its survivor before the second pass, so an emoji held three times - twice
    /// identically - reports one identical pair and one same-emoji pair rather than a tangle.
    /// </summary>
    public static IReadOnlyList<ArchiveDuplicateGroup> Find(CategoryIndexState index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var groups = new List<ArchiveDuplicateGroup>();
        var survivors = new List<IndexedImageRecord>();

        // Two records naming the same file are one file. An index that has picked up a second
        // record for a path - two spellings of it, or the same one written twice after an
        // interrupted rebuild - would otherwise report a picture as its own duplicate, and
        // recycling the extra would take the very copy the group promised to keep.
        var distinct = new List<IndexedImageRecord>(index.Images.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in index.Images.Where(item => !string.IsNullOrWhiteSpace(item.Path)))
        {
            if (seen.Add(NormalizePath(image.Path)))
            {
                distinct.Add(image);
            }
        }

        foreach (var identical in distinct
            .GroupBy(image => image.ExactFingerprint, StringComparer.Ordinal))
        {
            var ordered = identical.OrderBy(image => image, PreferByName).ToArray();
            survivors.Add(ordered[0]);
            if (ordered.Length > 1 && !string.IsNullOrWhiteSpace(identical.Key))
            {
                groups.Add(new ArchiveDuplicateGroup(ArchiveDuplicateKind.Identical, ordered[0], ordered[1..]));
            }
            else if (ordered.Length > 1)
            {
                survivors.AddRange(ordered[1..]);
            }
        }

        // An animation and the sheet it was cut from share an emoji and are both meant to be here,
        // so they are never each other's duplicate. Comparing like with like keeps them apart.
        foreach (var sameEmoji in survivors.GroupBy(
            image => (Key: EmojiIdentity.KeyFor(image.Path), Animated: AtlasAnimationWriter.IsAnimation(image.Path))))
        {
            if (sameEmoji.Count() < 2)
            {
                continue;
            }

            // Animations whose names agree on every animation parameter are the same animation,
            // whatever their bytes say. Separated out because that is a thing the app can act on:
            // one of them is the file every other part of the app addresses by name, and the other
            // is the copy that could not have that name. Anything else stays a report.
            foreach (var byAnimation in sameEmoji.GroupBy(AnimationParameters))
            {
                if (byAnimation.Count() < 2)
                {
                    continue;
                }

                var sameAnimation = byAnimation.OrderBy(image => image, PreferByName).ToArray();
                groups.Add(new ArchiveDuplicateGroup(
                    ArchiveDuplicateKind.SameAnimation,
                    sameAnimation[0],
                    sameAnimation[1..]));
            }

            var remaining = sameEmoji
                .GroupBy(AnimationParameters)
                .Select(group => group.OrderBy(image => image, PreferByName).First())
                .ToArray();
            if (remaining.Length < 2)
            {
                continue;
            }

            var ordered = remaining.OrderBy(image => image, PreferByDetail).ToArray();
            groups.Add(new ArchiveDuplicateGroup(ArchiveDuplicateKind.SameEmoji, ordered[0], ordered[1..]));
        }

        return groups
            .OrderBy(group => group.Kind)
            .ThenBy(group => group.Keep.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// What a file's name says it plays, or null for anything that is not an animation with the
    /// parameters in its name.
    /// </summary>
    /// <remarks>
    /// Grouping by this is what separates "the same animation twice" from "the same emoji at two
    /// sizes". A null is its own group per file - a name that says nothing about what it plays
    /// cannot be used to say two files play the same thing.
    /// </remarks>
    private static object AnimationParameters(IndexedImageRecord image)
    {
        // TryParse asks "is this a sheet" and rightly says no to a GIF. The question here is what
        // the name says it plays, which VRChat writes into the animation's name as well.
        if (!AtlasAnimationWriter.IsAnimation(image.Path)
            || !EmojiAtlasName.TryReadAnimation(image.Path, out var name))
        {
            return image.Id;
        }

        return name;
    }

    /// <summary>
    /// One spelling per file, so a path written two ways counts once.
    /// </summary>
    /// <remarks>
    /// A path the filesystem will not accept cannot be normalized, and is left as written. Two
    /// such records would still be caught by the router, which checks the files themselves.
    /// </remarks>
    private static string NormalizePath(string path)
    {
        try
        {
            return PathBoundary.Normalize(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// For copies that are the same picture: keep the one with the tidier name. A copy number is
    /// what the archiver adds when the name it wanted was taken, so the file without one is the
    /// original and the numbered ones are what accumulated around it.
    /// </summary>
    private static readonly IComparer<IndexedImageRecord> PreferByName =
        Comparer<IndexedImageRecord>.Create((first, second) =>
        {
            var numbered = EmojiIdentity.HasCopySuffix(first.Path).CompareTo(EmojiIdentity.HasCopySuffix(second.Path));
            if (numbered != 0)
            {
                return numbered;
            }

            var length = first.Path.Length.CompareTo(second.Path.Length);
            return length != 0 ? length : string.CompareOrdinal(first.Path, second.Path);
        });

    /// <summary>
    /// For copies that are the same emoji but not the same picture: the larger one holds more of
    /// it, so that is the one put forward. Only a suggestion - nothing is removed on this basis.
    /// </summary>
    private static readonly IComparer<IndexedImageRecord> PreferByDetail =
        Comparer<IndexedImageRecord>.Create((first, second) =>
        {
            var area = ((long)second.Width * second.Height).CompareTo((long)first.Width * first.Height);
            return area != 0 ? area : PreferByName.Compare(first, second);
        });
}
