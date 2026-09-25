using System.Text.Json;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Services;

public sealed class ScanWorkerSettingsTests
{
    [Fact]
    public void WorkerOnlySaveDoesNotHideOtherPendingChanges()
    {
        using var directory = new TestDirectory();
        var settings = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local")).Settings;
        var draft = new SettingsDraft(settings.CategoryMappings.Select(m =>
            new CategorySettingsDraft(m.Category, m.SourcePath, m.IsEnabled)).ToArray(),
            settings.OutputRootPath, settings.SimilarityProfile, settings.Automation.StartWithWindows,
            settings.BringReviewForwardWhenHeld, settings.Automation.WatchScanSeconds, settings.Automation.WatchMode,
            settings.OrganizationPolicy, settings.CustomSimilarityThresholds, ScanWorkers: 4);
        Assert.True(draft.ChangesOnlyScanWorkers(settings));
        Assert.False((draft with { OutputRootPath = directory.GetPath("other") }).ChangesOnlyScanWorkers(settings));
        Assert.False((draft with { StartWithWindows = !draft.StartWithWindows }).ChangesOnlyScanWorkers(settings));
        Assert.False((draft with { WatchScanSeconds = draft.WatchScanSeconds + 1 }).ChangesOnlyScanWorkers(settings));
        Assert.False((draft with { Categories = [] }).ChangesOnlyScanWorkers(settings));
    }

    [Fact]
    public void OldSettingsDefaultToConservativeAuto()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(0, settings.ScanWorkers);
        Assert.InRange(settings.ResolveScanWorkers(), 1, 2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(-1, 0)]
    [InlineData(999, 8)]
    public void DraftPersistsBoundedWorkerChoice(int requested, int expected)
    {
        using var directory = new TestDirectory();
        var state = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var draft = new SettingsDraft([], state.Settings.OutputRootPath,
            SimilarityProfile.Conservative, false, true, ScanWorkers: requested);
        draft.ApplyTo(state);
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(state.Settings))!;
        Assert.Equal(expected, restored.ScanWorkers);
        if (expected > 0) Assert.Equal(expected, restored.ResolveScanWorkers());
    }
}
