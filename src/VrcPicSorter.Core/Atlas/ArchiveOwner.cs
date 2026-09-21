using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Atlas;

/// <summary>Resolves indexed sheets against configured roots without guessing from folder names.</summary>
public static class ArchiveOwner
{
    public static string Resolve(AppSettings settings, VrcImageCategory category, string path)
    {
        var roots = settings.CategoryMappings.Where(x => x.Category == category).Select(x => x.ArchivePath)
            .Concat(settings.LegacyArchiveMappings.Where(x => x.Category == category).Select(x => x.ArchivePath))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(PathBoundary.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(x => PathBoundary.Contains(x, path)).ToArray();
        if (roots.Length != 1)
            throw new IOException($"The archived sheet has {(roots.Length == 0 ? "no configured" : "ambiguous")} archive ownership: {path}");
        var root = roots[0];
        PathBoundary.EnsureNoReparsePoints(path, "Archived sheet", root);
        // Opening the directory establishes availability; File/Directory.Exists hides IO failures.
        using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
        _ = entries.MoveNext();
        return root;
    }
}
