using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Tests.Imaging;
using SixLabors.ImageSharp;

namespace VrcPicSorter.Tests.Services;

public sealed class AppRuntimeTests
{
    [Fact]
    public async Task FirstOutputSelectionDoesNotRetainUnusedSuggestedArchives()
    {
        using var directory = new TestDirectory();
        using var runtime = new AppRuntime(directory.Path, allowStartupRegistration: false);
        await runtime.InitializeAsync();
        var source = directory.GetPath("incoming");
        Directory.CreateDirectory(source);
        using var image = ImageFixtureFactory.CreatePattern(210);
        await image.SaveAsPngAsync(Path.Combine(source, "unique.png"));
        var output = directory.GetPath("chosen-output");
        Directory.CreateDirectory(output);
        var draft = new SettingsDraft(AppStateDefaults.FixedCategories.Select(category =>
            new CategorySettingsDraft(category, source, category == VrcImageCategory.Emoji)).ToArray(),
            output, SimilarityProfile.Conservative, false, true);
        await runtime.StateStore.UpdateAsync(state => { draft.ApplyTo(state); return true; });
        var finalOutput = directory.GetPath("final-output");
        Directory.CreateDirectory(finalOutput);
        await runtime.StateStore.UpdateAsync(state => { (draft with { OutputRootPath = finalOutput }).ApplyTo(state); return true; });
        var result = Assert.Single(await runtime.Scanner.ScanAllAsync());
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(finalOutput, "Emoji", "unique.png")));
    }

    [Fact]
    public async Task IsolatedRuntimeKeepsEveryDefaultPathInsideItsDataDirectory()
    {
        using var directory = new TestDirectory();
        using var runtime = new AppRuntime(directory.Path, allowStartupRegistration: false);

        await runtime.InitializeAsync();
        var state = await runtime.StateStore.LoadAsync();

        Assert.False(runtime.AllowStartupRegistration);
        Assert.All(
            state.Settings.CategoryMappings,
            mapping =>
            {
                Assert.True(PathBoundary.Contains(directory.Path, mapping.SourcePath));
                Assert.True(PathBoundary.Contains(directory.Path, mapping.ArchivePath));
            });
        Assert.True(PathBoundary.Contains(directory.Path, state.Settings.OutputRootPath));
        Assert.True(PathBoundary.Contains(directory.Path, state.Settings.HoldingRootPath));
        Assert.False(runtime.Watcher.IsRunning);
    }
}
