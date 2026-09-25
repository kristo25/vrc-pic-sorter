using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.App.Services;

public sealed record CategorySettingsDraft(
    VrcImageCategory Category,
    string SourcePath,
    bool IsEnabled);

public sealed record SettingsDraft(
    IReadOnlyList<CategorySettingsDraft> Categories,
    string OutputRootPath,
    SimilarityProfile SimilarityProfile,
    bool StartWithWindows,
    bool BringReviewForwardWhenHeld,
    int WatchScanSeconds = AutomationSettings.DefaultWatchScanSeconds,
    WatchMode WatchMode = WatchMode.OnDetection,
    OrganizationPolicy OrganizationPolicy = OrganizationPolicy.CategoryRoot,
    SimilarityThresholds? CustomSimilarityThresholds = null,
    int ScanWorkers = 0)
{
    public bool ChangesOnlyScanWorkers(AppSettings settings) =>
        ScanWorkers != settings.ScanWorkers
        && settings.OutputRootConfirmed
        && string.Equals(OutputRootPath, settings.OutputRootPath, StringComparison.OrdinalIgnoreCase)
        && SimilarityProfile == settings.SimilarityProfile
        && Equals(CustomSimilarityThresholds, settings.CustomSimilarityThresholds)
        && OrganizationPolicy == settings.OrganizationPolicy
        && StartWithWindows == settings.Automation.StartWithWindows
        && BringReviewForwardWhenHeld == settings.BringReviewForwardWhenHeld
        && WatchScanSeconds == settings.Automation.WatchScanSeconds
        && WatchMode == settings.Automation.WatchMode
        && !settings.Automation.WatchWhileOpen
        && Categories.Count == settings.CategoryMappings.Count
        && Categories.All(draft => settings.CategoryMappings.Any(mapping =>
            mapping.Category == draft.Category && mapping.IsEnabled == draft.IsEnabled
            && string.Equals(mapping.SourcePath, draft.SourcePath, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Returns the first invariant this draft would break, or <see langword="null"/> when it is
    /// safe to persist. Only overlap problems block a save, because they are the ones that could
    /// make the app treat its own archive as incoming. A folder that does not exist yet is
    /// reported by <see cref="DescribeMissingFolders"/> instead: watched folders are allowed to
    /// appear later.
    /// </summary>
    public string? DescribeBlockingProblem(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (CustomSimilarityThresholds is { } limits && !limits.IsValid())
            return "Similarity limits must be numbers from 0 to 100, with minimum no greater than maximum.";

        try
        {
            if (!string.Equals(PathBoundary.Normalize(OutputRootPath), PathBoundary.Normalize(settings.OutputRootPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                var archives = settings.CategoryMappings.Select(item => item.ArchivePath)
                    .Concat(settings.LegacyArchiveMappings.Select(item => item.ArchivePath))
                    .Where(path => !string.IsNullOrWhiteSpace(path));
                if (archives.Any(path => PathBoundary.Contains(path, OutputRootPath)))
                    return "The new output folder cannot be inside an existing or retained archive.";
            }
            foreach (var category in Categories.Where(item => item.IsEnabled))
            {
                if (string.IsNullOrWhiteSpace(category.SourcePath))
                {
                    return $"Choose a source folder for {category.Category} before enabling it.";
                }

                if (PathBoundary.Overlaps(category.SourcePath, OutputRootPath))
                {
                    return $"The {category.Category} source and the output folder cannot overlap.";
                }

                if (settings.LegacyArchiveMappings.Any(
                        legacy => !string.IsNullOrWhiteSpace(legacy.ArchivePath)
                            && PathBoundary.Overlaps(category.SourcePath, legacy.ArchivePath)))
                {
                    return $"The {category.Category} source cannot overlap a retained archive folder.";
                }
            }

            return !string.IsNullOrWhiteSpace(settings.HoldingRootPath)
                && PathBoundary.Overlaps(OutputRootPath, settings.HoldingRootPath)
                ? "The output and application holding folders cannot overlap."
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That folder path is not valid, so the change was not saved.";
        }
    }

    /// <summary>
    /// Describes enabled source folders that do not exist yet, or <see langword="null"/> when they
    /// all do. The output folder is not reported: a scan creates it on demand.
    /// </summary>
    public string? DescribeMissingFolders()
    {
        var missing = Categories
            .Where(item => item.IsEnabled
                && !string.IsNullOrWhiteSpace(item.SourcePath)
                && !Directory.Exists(item.SourcePath))
            .Select(item => $"{item.Category} source")
            .ToList();

        return missing.Count == 0
            ? null
            : $"Saved. These folders do not exist yet: {string.Join(", ", missing)}. "
                + "Scanning skips them until they appear.";
    }

    public void ApplyTo(AppStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        CustomSimilarityThresholds?.Validate();
        var outputRoot = Path.GetFullPath(OutputRootPath);
        var rootChanged = !string.Equals(
            state.Settings.OutputRootPath,
            outputRoot,
            StringComparison.OrdinalIgnoreCase);

        if (rootChanged)
        {
            state.Settings.OutputFolderRevision++;
            state.Settings.AutoScanProfile = null;
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                var previousIndex = state.ArchiveIndex.Categories.Single(item => item.Category == mapping.Category);
                if (mapping.ArchivePathKnownMissing
                    && PathBoundary.IsConfirmedMissingDirectory(mapping.ArchivePath)
                    && !previousIndex.Images.Any(image => PathBoundary.Contains(mapping.ArchivePath, image.Path))
                    && !state.OperationJournal.Any(item => item.Category == mapping.Category))
                    continue;
                // An unused generated suggestion is not an archive the user configured.
                // Never apply this exemption to older saved settings or previously indexed roots.
                if (state.Settings.OutputRootIsSuggested && previousIndex.LastCompletedUtc is null
                    && previousIndex.Images.Count == 0 && !Directory.Exists(mapping.ArchivePath)
                    && !state.History.Any(item => item.Category == mapping.Category)
                    && !state.OperationJournal.Any(item => item.Category == mapping.Category))
                    continue;
                if (string.IsNullOrWhiteSpace(mapping.ArchivePath)
                    || state.Settings.LegacyArchiveMappings.Any(
                        legacy => legacy.Category == mapping.Category
                            && string.Equals(legacy.ArchivePath, mapping.ArchivePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
                {
                    Category = mapping.Category,
                    ArchivePath = mapping.ArchivePath,
                });
            }
        }

        foreach (var draft in Categories)
        {
            var mapping = state.Settings.CategoryMappings.Single(item => item.Category == draft.Category);
            var normalizedSource = string.IsNullOrWhiteSpace(draft.SourcePath)
                ? string.Empty
                : Path.GetFullPath(draft.SourcePath);
            var sourceChanged = !string.Equals(
                mapping.SourcePath,
                normalizedSource,
                StringComparison.OrdinalIgnoreCase);
            mapping.SourcePath = normalizedSource;
            mapping.ArchivePath = Path.Combine(outputRoot, draft.Category.ToString());
            if (rootChanged) mapping.ArchivePathKnownMissing = PathBoundary.IsConfirmedMissingDirectory(mapping.ArchivePath);
            mapping.IsEnabled = draft.IsEnabled;

            if (sourceChanged || rootChanged)
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == draft.Category);
                index.Status = IndexStatus.Stale;
                index.LastError = sourceChanged && rootChanged
                    ? "Source folder and output root changed."
                    : sourceChanged
                        ? "Source folder changed."
                        : "Output root changed.";
            }
        }

        state.Settings.OutputRootPath = outputRoot;
        if (rootChanged) state.Settings.OutputRootIsSuggested = false;
        state.Settings.OutputRootConfirmed = true;
        state.Settings.SimilarityProfile = SimilarityProfile;
        state.Settings.ScanWorkers = ScanWorkers;
        state.Settings.CustomSimilarityThresholds = CustomSimilarityThresholds;
        state.Settings.OrganizationPolicy = OrganizationPolicy;
        state.Settings.Automation.WatchWhileOpen = false;
        state.Settings.Automation.StartWithWindows = StartWithWindows;
        state.Settings.Automation.WatchMode = WatchMode;
        state.Settings.Automation.WatchScanSeconds = Math.Clamp(
            WatchScanSeconds,
            AutomationSettings.MinimumWatchScanSeconds,
            AutomationSettings.MaximumWatchScanSeconds);
        state.Settings.BringReviewForwardWhenHeld = BringReviewForwardWhenHeld;
    }
}
