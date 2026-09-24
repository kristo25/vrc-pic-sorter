using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public sealed record ArchiveDuplicateSnapshot(
    IReadOnlyList<ArchiveDuplicateGroup> Groups,
    IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Warnings.Count == 0;
}

public sealed record ArchiveDuplicateCleanupResult(
    int Removed,
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
            return await RefreshArchiveDuplicatesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Refreshes every archive index and sends only proven identical extra copies to the Recycle
    /// Bin. Incomplete archive coverage disables all removal for that pass.
    /// </summary>
    public async Task<ArchiveDuplicateCleanupResult> CleanArchiveDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var categories = state.Settings.CategoryMappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ArchivePath)
                    || state.Settings.LegacyArchiveMappings.Any(legacy => legacy.Category == mapping.Category
                        && !string.IsNullOrWhiteSpace(legacy.ArchivePath)))
                .Select(mapping => mapping.Category)
                .ToArray();
            foreach (var category in categories)
            {
                await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
            }

            return await CleanIndexedArchiveDuplicatesCoreAsync(categories, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Refreshes one archive after a watcher event, then recycles only proven identical extras.
    /// </summary>
    public async Task<ArchiveDuplicateCleanupResult> RefreshAndCleanArchiveCategoryAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
            return await CleanIndexedArchiveDuplicatesCoreAsync([category], cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<ArchiveDuplicateCleanupResult> CleanIndexedArchiveDuplicatesCoreAsync(
        IReadOnlyCollection<VrcImageCategory> categories,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<ArchiveDuplicateGroup>();
        var warnings = new List<string>();
        foreach (var category in categories.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapping = state.Settings.CategoryMappings.Single(item => item.Category == category);
            var roots = state.Settings.LegacyArchiveMappings
                .Where(item => item.Category == category)
                .Select(item => item.ArchivePath)
                .Prepend(mapping.ArchivePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missing = roots.Where(path => !Directory.Exists(path)).ToArray();
            if (missing.Length > 0)
            {
                warnings.AddRange(missing.Select(path => $"{category}: archive folder is unavailable: {path}"));
                continue;
            }

            var index = state.ArchiveIndex.Categories.Single(item => item.Category == category);
            if (index.Status != IndexStatus.Current)
            {
                warnings.Add($"{category}: archive index is {index.Status}; it will refresh during the next scan.");
                continue;
            }
            groups.AddRange(ArchiveDuplicateFinder.Find(index).Where(group => group.IsRemovable));
        }

        // One incomplete category disables the pass. Cleanup is automatic, so it must never make
        // a partial-coverage decision without a person explicitly accepting that risk.
        if (warnings.Count > 0)
        {
            return new ArchiveDuplicateCleanupResult(0, warnings);
        }

        var removed = 0;
        foreach (var group in groups)
        {
            foreach (var extra in group.Extras)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _router.RemoveArchivedDuplicateAsync(extra, group.Keep, cancellationToken)
                        .ConfigureAwait(false);
                    removed++;
                }
                catch (Exception exception) when (
                    exception is NotSupportedException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    warnings.Add($"{extra.Path}: {exception.Message}");
                }
            }
        }

        return new ArchiveDuplicateCleanupResult(removed, warnings);
    }

    private async Task<ArchiveDuplicateSnapshot> RefreshArchiveDuplicatesCoreAsync(
        CancellationToken cancellationToken)
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

            // The indexer tolerates missing roots when another root exists. Automatic cleanup
            // requires complete coverage so it never removes a file while a possible survivor is
            // unavailable.
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
}
