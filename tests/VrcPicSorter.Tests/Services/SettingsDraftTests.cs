using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Services;

public sealed class SettingsDraftTests
{
    [Fact]
    public void OutputNestedInExistingArchiveIsRejected()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var archive = state.Settings.CategoryMappings.Single(m => m.Category == VrcImageCategory.Emoji).ArchivePath;
        var draft = Draft(Path.Combine(archive, "new-output"), VrcImageCategory.Emoji, directory.GetPath("incoming"));
        Assert.NotNull(draft.DescribeBlockingProblem(state.Settings));
    }

    [Fact]
    public void ApplyUsesOneOutputRootAndOnlyStalesChangedCategories()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        foreach (var index in state.ArchiveIndex.Categories)
        {
            index.Status = IndexStatus.Current;
        }

        var originalOutput = state.Settings.OutputRootPath;
        var emoji = state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji);
        var changedSource = directory.GetPath("emoji-new");
        var draft = new SettingsDraft(
            state.Settings.CategoryMappings.Select(mapping => new CategorySettingsDraft(
                    mapping.Category,
                    mapping.Category == VrcImageCategory.Emoji ? changedSource : mapping.SourcePath,
                    mapping.Category == VrcImageCategory.Emoji))
                .ToArray(),
            originalOutput,
            SimilarityProfile.Broad,
            StartWithWindows: true,
            BringReviewForwardWhenHeld: false);

        draft.ApplyTo(state);

        Assert.Equal(Path.GetFullPath(changedSource), emoji.SourcePath);
        Assert.All(
            state.Settings.CategoryMappings,
            mapping => Assert.Equal(Path.Combine(originalOutput, mapping.Category.ToString()), mapping.ArchivePath));
        Assert.Equal(IndexStatus.Stale, state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji).Status);
        Assert.All(
            state.ArchiveIndex.Categories.Where(item => item.Category != VrcImageCategory.Emoji),
            index => Assert.Equal(IndexStatus.Current, index.Status));
        Assert.Equal(SimilarityProfile.Broad, state.Settings.SimilarityProfile);
        Assert.True(state.Settings.Automation.StartWithWindows);
        Assert.False(state.Settings.BringReviewForwardWhenHeld);
    }

    [Fact]
    public void ChangingOutputRootStalesAllIndexesAndRetainsOldArchivesAsLegacy()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        state.Settings.OutputRootIsSuggested = false; // Previously selected archive, possibly offline.
        foreach (var index in state.ArchiveIndex.Categories)
        {
            index.Status = IndexStatus.Current;
        }

        var oldArchives = state.Settings.CategoryMappings.ToDictionary(item => item.Category, item => item.ArchivePath);
        var newRoot = directory.GetPath("VRC Images");
        var draft = new SettingsDraft(
            state.Settings.CategoryMappings.Select(
                    mapping => new CategorySettingsDraft(mapping.Category, mapping.SourcePath, mapping.IsEnabled))
                .ToArray(),
            newRoot,
            SimilarityProfile.Conservative,
            StartWithWindows: false,
            BringReviewForwardWhenHeld: true);

        draft.ApplyTo(state);

        Assert.Equal(Path.GetFullPath(newRoot), state.Settings.OutputRootPath);
        Assert.All(state.ArchiveIndex.Categories, index => Assert.Equal(IndexStatus.Stale, index.Status));
        Assert.All(
            oldArchives,
            pair => Assert.Contains(
                state.Settings.LegacyArchiveMappings,
                legacy => legacy.Category == pair.Key && legacy.ArchivePath == pair.Value));
    }

    [Fact]
    public void PreviouslyUnusedOutputIsRetainedWhenItsParentBecomesUnavailable()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var firstOutput = directory.GetPath("first-output");
        Directory.CreateDirectory(firstOutput);
        Draft(firstOutput, VrcImageCategory.Emoji, directory.GetPath("incoming")).ApplyTo(state);
        var previousArchive = state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji).ArchivePath;
        Assert.True(state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji).ArchivePathKnownMissing);

        // Loss of access to the parent no longer proves the category folder is absent.
        Directory.Move(firstOutput, directory.GetPath("temporarily-offline-output"));
        Draft(directory.GetPath("second-output"), VrcImageCategory.Emoji, directory.GetPath("incoming")).ApplyTo(state);

        Assert.Contains(state.Settings.LegacyArchiveMappings,
            item => item.Category == VrcImageCategory.Emoji && item.ArchivePath == previousArchive);
    }

    private static SettingsDraft Draft(
        string outputRoot,
        VrcImageCategory enabledCategory,
        string enabledSource) =>
        new(
            AppStateDefaults.FixedCategories
                .Select(category => new CategorySettingsDraft(
                    category,
                    category == enabledCategory ? enabledSource : string.Empty,
                    category == enabledCategory))
                .ToArray(),
            outputRoot,
            SimilarityProfile.Conservative,
            StartWithWindows: false,
            BringReviewForwardWhenHeld: true);

    [Fact]
    public void DefaultLayoutPlacesTheArchiveBesideTheCategoryFoldersWithoutOverlapping()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = new SettingsDraft(
            state.Settings.CategoryMappings
                .Select(mapping => new CategorySettingsDraft(mapping.Category, mapping.SourcePath, true))
                .ToArray(),
            state.Settings.OutputRootPath,
            SimilarityProfile.Conservative,
            StartWithWindows: false,
            BringReviewForwardWhenHeld: true);

        Assert.Null(draft.DescribeBlockingProblem(state.Settings));
    }

    [Fact]
    public void SourceThatContainsTheOutputRootIsRefused()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var vrchatRoot = directory.GetPath("profile", "Images", "VRChat");
        var draft = Draft(
            Path.Combine(vrchatRoot, "Archived Images"),
            VrcImageCategory.Emoji,
            vrchatRoot);

        var problem = draft.DescribeBlockingProblem(state.Settings);

        Assert.NotNull(problem);
        Assert.Contains("cannot overlap", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledCategoryWithoutASourceFolderIsRefused()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = Draft(directory.GetPath("output"), VrcImageCategory.Prints, string.Empty);

        var problem = draft.DescribeBlockingProblem(state.Settings);

        Assert.NotNull(problem);
        Assert.Contains("Prints", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFoldersAreReportedWithoutBlockingTheSave()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = Draft(
            directory.GetPath("output-that-does-not-exist"),
            VrcImageCategory.Stickers,
            directory.GetPath("source-that-does-not-exist"));

        Assert.Null(draft.DescribeBlockingProblem(state.Settings));
        var missing = draft.DescribeMissingFolders();
        Assert.NotNull(missing);
        Assert.Contains("Stickers source", missing, StringComparison.Ordinal);

        // The output folder is not reported: a scan creates it on demand.
        Assert.DoesNotContain("output", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFoldersReportNothingMissing()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var source = directory.GetPath("source");
        var output = directory.GetPath("output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        var draft = Draft(output, VrcImageCategory.Emoji, source);

        Assert.Null(draft.DescribeBlockingProblem(state.Settings));
        Assert.Null(draft.DescribeMissingFolders());
    }

    [Fact]
    public void WatchModeAndIntervalArePersistedAndClamped()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = new SettingsDraft(
            state.Settings.CategoryMappings
                .Select(mapping => new CategorySettingsDraft(mapping.Category, mapping.SourcePath, false))
                .ToArray(),
            state.Settings.OutputRootPath,
            SimilarityProfile.Conservative,
            StartWithWindows: false,
            BringReviewForwardWhenHeld: true,
            WatchScanSeconds: 5,
            WatchMode: WatchMode.OnInterval);

        draft.ApplyTo(state);

        Assert.Equal(WatchMode.OnInterval, state.Settings.Automation.WatchMode);
        Assert.Equal(
            AutomationSettings.MinimumWatchScanSeconds,
            state.Settings.Automation.WatchScanSeconds);
    }

    [Fact]
    public void WatchModeDefaultsToAnalyzingOnDetection()
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));

        Assert.Equal(WatchMode.OnDetection, state.Settings.Automation.WatchMode);
    }
}
