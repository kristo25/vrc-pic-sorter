using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public sealed record ArchiveDuplicateSnapshot(
    IReadOnlyList<ArchiveDuplicateGroup> Groups,
    IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Warnings.Count == 0;
}

public sealed partial class ScanCoordinator
{
    /// <summary>
    /// Refreshes archive inventory before reporting duplicates. Source scanning can be disabled
    /// without hiding its archive. No images are moved, exported or recycled here.
    /// </summary>
    public async Task<ArchiveDuplicateSnapshot> RefreshArchiveDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var groups = new List<ArchiveDuplicateGroup>();
            var warnings = new List<string>();
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roots = state.Settings.LegacyArchiveMappings
                    .Where(item => item.Category == mapping.Category)
                    .Select(item => item.ArchivePath)
                    .Prepend(mapping.ArchivePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (roots.Length == 0)
                {
                    continue;
                }

                // The indexer tolerates missing roots when another root exists. This report
                // must disclose that missing coverage instead of presenting stale copies.
                var missing = roots.Where(path => !Directory.Exists(path)).ToArray();
                if (missing.Length > 0)
                {
                    warnings.AddRange(missing.Select(path => $"{mapping.Category}: archive folder is unavailable: {path}"));
                    continue;
                }

                try
                {
                    var refreshed = await _indexer.RefreshAsync(mapping.Category, cancellationToken).ConfigureAwait(false);
                    warnings.AddRange(refreshed.Errors.Select(error => $"{mapping.Category}: {error}"));
                    warnings.AddRange(refreshed.SkippedFiles.Select(error => $"{mapping.Category}: skipped {error}"));
                    if (refreshed.Status != IndexStatus.Current)
                    {
                        if (refreshed.Errors.Count == 0)
                        {
                            warnings.Add($"{mapping.Category}: archive index is {refreshed.Status}.");
                        }
                        continue;
                    }

                    missing = roots.Where(path => !Directory.Exists(path)).ToArray();
                    if (missing.Length > 0)
                    {
                        warnings.AddRange(missing.Select(path => $"{mapping.Category}: archive folder became unavailable: {path}"));
                        continue;
                    }

                    var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                    var index = current.ArchiveIndex.Categories.Single(item => item.Category == mapping.Category);
                    if (index.Status != IndexStatus.Current || index.Generation != refreshed.Generation)
                    {
                        warnings.Add($"{mapping.Category}: archive index changed during refresh; refresh again.");
                        continue;
                    }
                    groups.AddRange(ArchiveDuplicateFinder.Find(index));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException)
                {
                    warnings.Add($"{mapping.Category}: archive refresh failed: {exception.Message}");
                }
            }
            return new ArchiveDuplicateSnapshot(groups, warnings);
        }
        finally
        {
            _scanGate.Release();
        }
    }
}
