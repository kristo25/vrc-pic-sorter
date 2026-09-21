using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Core.Atlas;

/// <summary>Existing GIF candidates. A matching name prevents an automatic export, not deletion.</summary>
internal sealed class ExistingAnimations
{
    private readonly ILookup<string, string> _paths;

    public ExistingAnimations(string root)
    {
        PathBoundary.EnsureNoReparsePoints(root, "Animation archive");
        var paths = Directory.Exists(root)
            ? PathBoundary.EnumerateFilesWithoutReparsePoints(root)
            : [];
        _paths = paths.Where(AtlasAnimationWriter.IsAnimation)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToLookup(EmojiIdentity.KeyFor, StringComparer.OrdinalIgnoreCase);
    }

    public string? Find(string sheetPath, string archiveRoot)
    {
        var destination = AtlasAnimationWriter.BuildDestination(sheetPath, archiveRoot);
        // An exact destination that has become a link must fail, not look absent.
        PathBoundary.EnsureSafeDestination(
            AtlasAnimationWriter.DestinationRoot(sheetPath, archiveRoot), destination, "Animation");
        var candidate = _paths[EmojiIdentity.KeyFor(sheetPath)]
            .Where(File.Exists)
            .OrderBy(path => !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
            .ThenBy(EmojiIdentity.HasCopySuffix)
            .FirstOrDefault();
        if (candidate is not null)
            PathBoundary.EnsureSafeDestination(
                AtlasAnimationWriter.DestinationRoot(sheetPath, archiveRoot), candidate, "Existing animation");
        return candidate;
    }
}
