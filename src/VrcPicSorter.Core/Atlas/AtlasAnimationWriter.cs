using SixLabors.ImageSharp;
using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Core.Atlas;

/// <summary>
/// The outcome of trying to animate one sheet.
/// </summary>
/// <remarks>
/// <paramref name="Warning"/> is set only when the animation could not be written at all - an
/// unreadable file, a grid the canvas cannot hold, a folder that cannot be written. A sheet whose
/// art runs past the frame count in its name is not a failure: the name decides, the extra art is
/// left out, and <paramref name="Note"/> says so.
/// </remarks>
public sealed record AtlasAnimationResult(
    bool Exported,
    string? Path,
    string? Warning,
    string? Note = null)
{
    public static readonly AtlasAnimationResult NotASheet = new(false, null, null);
}

/// <summary>
/// Writes the animated version of an archived emoji sheet.
/// </summary>
/// <remarks>
/// Animations live in their own folder inside the archive rather than beside the sheets, so the
/// stills stay a folder of stills. Ready-made GIFs arriving from an incoming folder are filed there
/// too, which is what lets one be recognised as the animation of a sheet already held.
/// <para>
/// Once a sheet has been animated the sheet itself is filed under
/// <see cref="ReferenceFolderName"/>, so the folder holds the GIFs a person looks at and, one
/// level down, the atlases they came from. Everything in there is indexed, animations included:
/// they are archived images like any other, and leaving them out meant a second copy of one had
/// nothing to be compared against.
/// </para>
/// </remarks>
public sealed class AtlasAnimationWriter
{
    public const string AnimationFolderName = "Animated";

    /// <summary>Where an atlas is filed once its animation exists.</summary>
    public const string ReferenceFolderName = "Gif Ref";

    private readonly AtlasGifExporter _exporter = new();

    /// <summary>
    /// True for anything inside a folder holding generated animations. Matching on the folder name
    /// rather than one known path covers a scan that routed its output somewhere else.
    /// </summary>
    public static bool IsAnimationFolder(string path) => HasSegment(path, AnimationFolderName);

    /// <summary>True for an atlas filed alongside the animation made from it.</summary>
    public static bool IsReferenceFolder(string path) =>
        IsAnimationFolder(path) && HasSegment(path, ReferenceFolderName);

    /// <summary>Formats that carry an animation rather than a still.</summary>
    public static readonly IReadOnlySet<string> AnimationExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GifExtensionName };

    private const string GifExtensionName = ".gif";

    /// <summary>
    /// True for a file that is already an animation, whoever made it - the app's own export, or one
    /// that arrived in an incoming folder ready-made.
    /// </summary>
    public static bool IsAnimation(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && AnimationExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>
    /// The animation that would belong to <paramref name="atlasPath"/>.
    /// </summary>
    /// <remarks>
    /// Matched on the emoji itself rather than on the file name. Comparing names meant a ready-made
    /// GIF that arrived as "x (2).gif" was never paired with the sheet "x.png" it plainly came from,
    /// and both were kept. It still costs nothing - no file is opened to establish it.
    /// </remarks>
    public static bool IsAnimationOf(string animationPath, string atlasPath) =>
        IsAnimation(animationPath)
        && !IsAnimation(atlasPath)
        && EmojiIdentity.IsSameEmoji(animationPath, atlasPath);

    private static bool HasSegment(string path, string segmentName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in Split(path))
        {
            if (segment.Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Split(string path) => path.Split(
        [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Where the animation for an archived sheet belongs: under the archive's animation folder,
    /// keeping whatever subfolders the sheet itself sits in. A sheet that landed outside the
    /// archive root - a manual scan routed elsewhere - gets an animation folder beside it instead
    /// of being written somewhere the caller did not choose.
    /// </summary>
    public static string BuildDestination(string archivedPath, string archiveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);
        var fileName = System.IO.Path.GetFileNameWithoutExtension(archivedPath) + AtlasGifExporter.GifExtension;
        var (root, branch) = Locate(archivedPath, archiveRoot);
        return System.IO.Path.Combine(root, AnimationFolderName, branch, fileName);
    }

    /// <summary>
    /// The folder an animation for <paramref name="archivedPath"/> is allowed to be written inside.
    /// </summary>
    /// <remarks>
    /// The same folder <see cref="BuildDestination"/> measures from, handed to the exporter so a
    /// link cannot move the finished file out of it.
    /// </remarks>
    public static string DestinationRoot(string archivedPath, string archiveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);
        return Locate(archivedPath, archiveRoot).Root;
    }

    /// <summary>
    /// Where the atlas itself belongs once its animation exists: one level below the animation, so
    /// the folder reads as the GIFs plus the sheets they were cut from.
    /// </summary>
    public static string BuildReferenceDestination(string archivedPath, string archiveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);
        var fileName = System.IO.Path.GetFileName(archivedPath);
        var (root, branch) = Locate(archivedPath, archiveRoot);
        return System.IO.Path.Combine(root, AnimationFolderName, ReferenceFolderName, branch, fileName);
    }

    /// <summary>
    /// The folder both destinations are measured from, and the subfolders of it the sheet sits in.
    /// </summary>
    /// <remarks>
    /// A sheet that has already been filed as a reference is measured from the same place it was
    /// the first time. Without that, asking a second time where its animation belongs would answer
    /// Animated/Gif Ref/Animated, and every re-export would bury it a level deeper.
    /// </remarks>
    private static (string Root, string Branch) Locate(string archivedPath, string archiveRoot)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(archivedPath)) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(archiveRoot) && PathBoundary.Contains(archiveRoot, archivedPath))
        {
            var relative = System.IO.Path.GetRelativePath(archiveRoot, directory);
            var segments = relative == "." ? [] : Split(relative);
            return (archiveRoot, System.IO.Path.Combine(WithoutReferenceBranch(segments)));
        }

        // Outside the archive the sheet has no branch to keep, so only the reference folder itself
        // has to be climbed back out of.
        var loose = Split(directory);
        if (loose.Length >= 2
            && loose[^1].Equals(ReferenceFolderName, StringComparison.OrdinalIgnoreCase)
            && loose[^2].Equals(AnimationFolderName, StringComparison.OrdinalIgnoreCase))
        {
            directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(directory)!)!;
        }

        return (directory, string.Empty);
    }

    private static string[] WithoutReferenceBranch(string[] segments) =>
        segments.Length >= 2
        && segments[0].Equals(AnimationFolderName, StringComparison.OrdinalIgnoreCase)
        && segments[1].Equals(ReferenceFolderName, StringComparison.OrdinalIgnoreCase)
            ? segments[2..]
            : segments;

    /// <summary>
    /// Writes the animation for <paramref name="archivedPath"/> when its name says it is a sheet.
    /// Never throws: the image has already been archived safely by the time this runs, and a
    /// failure to animate it must not turn that into a failed scan.
    /// </summary>
    public async Task<AtlasAnimationResult> TryWriteAsync(
        string archivedPath,
        string archiveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!EmojiAtlasName.TryParse(archivedPath, out var name))
        {
            return AtlasAnimationResult.NotASheet;
        }

        return await TryWriteAsync(archivedPath, archiveRoot, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the animation using an explicit <paramref name="name"/> rather than the one in the
    /// file name. VRChat's name is right on all but a couple of sheets, and this is how those are
    /// corrected by hand.
    /// </summary>
    public async Task<AtlasAnimationResult> TryWriteAsync(
        string archivedPath,
        string archiveRoot,
        EmojiAtlasName name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            var destination = BuildDestination(archivedPath, archiveRoot);
            var destinationRoot = DestinationRoot(archivedPath, archiveRoot);
            var exported = await _exporter
                .ExportAsync(archivedPath, destination, name, destinationRoot, cancellationToken)
                .ConfigureAwait(false);
            return new AtlasAnimationResult(true, exported.Path, null, exported.Note);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException
                or ImageFormatException)
        {
            return new AtlasAnimationResult(false, null, $"could not be animated ({exception.Message})");
        }
    }
}
