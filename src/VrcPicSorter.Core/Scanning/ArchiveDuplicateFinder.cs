using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public enum ArchiveDuplicateKind
{
    /// <summary>The same picture twice, pixel for pixel. One copy is all there is to keep.</summary>
    Identical,

    /// <summary>
    /// The same emoji held more than once, but not the same picture - typically VRChat's own GIF
    /// beside the one this app exported from the sheet, at a different size. Which one is worth
    /// keeping is a judgement, so these are only ever reported.
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

        foreach (var identical in index.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Path))
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

            var ordered = sameEmoji.OrderBy(image => image, PreferByDetail).ToArray();
            groups.Add(new ArchiveDuplicateGroup(ArchiveDuplicateKind.SameEmoji, ordered[0], ordered[1..]));
        }

        return groups
            .OrderBy(group => group.Kind)
            .ThenBy(group => group.Keep.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
