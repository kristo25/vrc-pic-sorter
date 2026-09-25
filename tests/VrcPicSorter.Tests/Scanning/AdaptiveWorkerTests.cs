using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;
using SixLabors.ImageSharp;

namespace VrcPicSorter.Tests.Scanning;

public sealed class AdaptiveWorkerTests
{
    [Fact]
    public void SamplesIncludeLaterDirectoriesBeforeFillingLimit()
    {
        var paths = Enumerable.Range(0, 4).SelectMany(folder => Enumerable.Range(0, 40)
            .Select(i => Path.Combine($"folder{folder}", $"{i}.png")));
        var sample = ScanConcurrencyController.SpreadPaths(paths).Take(32).ToArray();
        Assert.Equal(4, sample.Select(Path.GetDirectoryName).Distinct().Count());
        Assert.All(sample.GroupBy(Path.GetDirectoryName), group => Assert.Equal(8, group.Count()));
    }

    [Fact]
    public void SustainedSlowdownBacksOffAgainstStableBaseline()
    {
        var tuner = new WorkerTuner(16, 16);
        tuner.Observe(100, false, false, false, true);
        tuner.Observe(80, false, false, false, true);
        tuner.Observe(80, false, false, false, true);
        Assert.Equal(8, tuner.Workers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(64)]
    public void CandidateRangeUsesAvailableProcessors(int processors)
    {
        var candidates = ScanConcurrencyController.Candidates(processors);
        Assert.Equal(1, candidates[0]); Assert.Equal(processors, candidates[^1]);
        Assert.Equal(candidates.Distinct().Count(), candidates.Length);
        Assert.All(candidates, value => Assert.InRange(value, 1, processors));
    }

    [Fact]
    public void SelectionPrefersThroughputAndAvoidsInsignificantExtraWorkers()
    {
        WorkerMeasurement[] rates = [new("Archive", 1, 100), new("Archive", 2, 190),
            new("Archive", 4, 195), new("Archive", 8, 180), new("Archive", 16, double.NaN)];
        Assert.Equal(2, ScanConcurrencyController.SelectWorkers(rates, 16));
        Assert.Equal(16, ScanConcurrencyController.SelectWorkers([.. rates, new("Archive", 16, 400)], 16));
        Assert.Equal(1, ScanConcurrencyController.SelectWorkers([], 1));
    }

    [Fact]
    public void LiveTrialsRequireBenefitAndRespectMemoryAndBackoff()
    {
        var tuner = new WorkerTuner(2, 16);
        tuner.Observe(100, false, false, true, true);
        Assert.Equal(2, tuner.Workers);
        tuner.Observe(100, false, false, false, true);
        Assert.Equal(4, tuner.Workers);
        tuner.Observe(103, false, false, false, true);
        Assert.Equal(2, tuner.Workers); Assert.Equal(2, tuner.AcceptedWorkers);
        var faster = new WorkerTuner(8, 64);
        faster.Observe(100, false, false, false, true);
        faster.Observe(120, false, false, false, true);
        Assert.Equal(16, faster.AcceptedWorkers);
        faster.Observe(120, true, false, false, true);
        Assert.Equal(8, faster.Workers);
        faster.Observe(120, false, true, false, true);
        Assert.Equal(4, faster.Workers);
    }

    [Fact]
    public void SavedProfileMustMatchOutputRevisionProcessorCountAndVersion()
    {
        var settings = new AppSettings { OutputRootPath = @"C:\archive", OutputFolderRevision = 2 };
        var profile = new AutoScanProfile
        {
            OutputRoot = settings.OutputRootPath,
            OutputRevision = 2,
            ProcessorCount = 64,
            AlgorithmVersion = ScanConcurrencyController.AlgorithmVersion
        };
        Assert.True(ScanConcurrencyController.Matches(profile, settings, 64));
        Assert.False(ScanConcurrencyController.Matches(profile, settings, 16));
        settings.OutputFolderRevision++;
        Assert.False(ScanConcurrencyController.Matches(profile, settings, 64));
        settings.OutputFolderRevision--;
        profile.AlgorithmVersion--;
        Assert.False(ScanConcurrencyController.Matches(profile, settings, 64));
    }

    [Fact]
    public async Task ChangingOutputAwayAndBackInvalidatesEveryTime()
    {
        using var directory = new TestDirectory();
        using var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), directory.GetPath("archive", "Emoji"));
        var state = await store.LoadAsync();
        var original = state.Settings.OutputRootPath;
        state.Settings.AutoScanProfile = new AutoScanProfile();
        var draft = new SettingsDraft([], directory.GetPath("other"), SimilarityProfile.Conservative, false, true);
        draft.ApplyTo(state);
        Assert.Null(state.Settings.AutoScanProfile); Assert.Equal(1, state.Settings.OutputFolderRevision);
        (draft with { OutputRootPath = original }).ApplyTo(state);
        Assert.Equal(2, state.Settings.OutputFolderRevision);
    }

    [Fact]
    public async Task CalibrationPersistsReusesAndCleansOwnedProbe()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("incoming"); var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        using var image = ImageFixtureFactory.CreatePattern(45);
        for (var i = 0; i < 8; i++) await image.SaveAsPngAsync(Path.Combine(archive, $"{i}.png"));
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var decoder = new ImageDecoder(); var indexer = new ArchiveIndexer(store, decoder);
        var state = await store.LoadAsync();
        await indexer.Concurrency.PrepareAsync(state.Settings, Directory.GetFiles(archive), [], default);
        state = await store.LoadAsync();
        var profile = Assert.IsType<AutoScanProfile>(state.Settings.AutoScanProfile);
        Assert.NotEmpty(profile.Measurements);
        Assert.InRange(profile.ArchiveWorkers, 1, Environment.ProcessorCount);
        Assert.Empty(Directory.GetDirectories(state.Settings.OutputRootPath, ".vrc-auto-probe-*"));
        var measured = profile.MeasuredUtc;
        await indexer.Concurrency.PrepareAsync(state.Settings, [], [], default);
        Assert.Equal(measured, (await store.LoadAsync()).Settings.AutoScanProfile!.MeasuredUtc);
        for (var i = 0; i < 8; i++) await image.SaveAsPngAsync(Path.Combine(source, $"{i}.png"));
        await indexer.Concurrency.PrepareAsync(state.Settings, [], Directory.GetFiles(source), default);
        var completed = (await store.LoadAsync()).Settings.AutoScanProfile!;
        Assert.Contains(completed.Measurements, m => m.Phase == "Incoming");
        Assert.Equal(profile.Measurements.Count(m => m.Phase == "Archive"),
            completed.Measurements.Count(m => m.Phase == "Archive"));
        Assert.Equal(IndexStatus.Current, (await indexer.RebuildAsync(VrcImageCategory.Emoji)).Status);
        Assert.Equal(8, (await store.LoadAsync()).ArchiveIndex.Categories[0].Images.Count);
    }

    [Fact]
    public async Task CancelledOrUnavailableCalibrationDoesNotPublishSuccess()
    {
        using var directory = new TestDirectory();
        using var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), directory.GetPath("absent", "Emoji"));
        var controller = new ScanConcurrencyController(store, new ImageDecoder());
        var settings = (await store.LoadAsync()).Settings;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.PrepareAsync(settings, [], [], new CancellationToken(true)));
        Assert.Null((await store.LoadAsync()).Settings.AutoScanProfile);
        await controller.PrepareAsync(settings, [], [], default);
        Assert.Null((await store.LoadAsync()).Settings.AutoScanProfile);
    }

    [Fact]
    public async Task ManualModeDoesNotProbeOrCalibrate()
    {
        using var directory = new TestDirectory();
        using var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), directory.GetPath("archive", "Emoji"));
        await store.UpdateAsync(state => state.Settings.ScanWorkers = 4);
        var controller = new ScanConcurrencyController(store, new ImageDecoder());
        await controller.PrepareAsync((await store.LoadAsync()).Settings, [], [], default);
        Assert.Null((await store.LoadAsync()).Settings.AutoScanProfile);
        Assert.Equal(4, (await controller.StartAsync("Archive", default)).Workers);
    }
}
