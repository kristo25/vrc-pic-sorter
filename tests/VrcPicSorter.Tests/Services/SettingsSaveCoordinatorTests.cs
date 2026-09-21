using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Services;

public sealed class SettingsSaveCoordinatorTests
{
    [Fact]
    public async Task SupersededDuringPersistenceCannotPublishOldCompletion()
    {
        var saves = new SettingsSaveCoordinator();
        var started = Signal<bool>();
        var release = Signal<bool>();
        var old = saves.SaveAsync(saves.Begin(), () => Task.FromResult("old"), action => action(), async _ =>
        {
            started.SetResult(true);
            await release.Task;
        });
        await started.Task;
        saves.Begin(); // New invalid input still invalidates old completion.
        release.SetResult(true);
        Assert.False(await old);
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SettingsWaitForActiveScannerOperation()
    {
        using var directory = new TestDirectory();
        using var runtime = new AppRuntime(directory.Path, allowStartupRegistration: false);
        var entered = Signal<bool>();
        var release = Signal<bool>();
        var active = runtime.Scanner.RunExclusiveAsync(async () => { entered.SetResult(true); await release.Task; });
        await entered.Task;
        var saves = new SettingsSaveCoordinator();
        var saved = false;
        var pending = saves.SaveAsync(saves.Begin(), () => Task.FromResult("new"),
            action => runtime.Scanner.RunExclusiveAsync(action), _ => { saved = true; return Task.CompletedTask; });
        Assert.False(saved);
        release.SetResult(true);
        await active;
        Assert.True(await pending);
        Assert.True(saved);
    }

    [Fact]
    public void RelocationAcceptanceCannotUseAnOldDestination()
    {
        using var directory = new TestDirectory();
        var state = VrcPicSorter.Core.Models.AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("state"));
        var mapping = state.Settings.CategoryMappings[0];
        mapping.IsEnabled = true;
        var stale = new VrcPicSorter.Core.FileSystem.ArchiveRelocationStep(mapping.Category,
            mapping.ArchivePath, directory.GetPath("old-choice", mapping.Category.ToString()), 1, 1);
        Assert.Throws<InvalidOperationException>(() => VrcPicSorter.Core.FileSystem.ArchiveRelocation.EnsurePlanMatchesSettings(state.Settings, [stale]));
    }

    [Fact]
    public async Task SlowOlderPreparationCannotOverwriteNewerChoice()
    {
        var saves = new SettingsSaveCoordinator();
        var slow = Signal<string>();
        var applied = new List<string>();
        Task Apply(string value) { applied.Add(value); return Task.CompletedTask; }
        var older = saves.SaveAsync(saves.Begin(), () => slow.Task, action => action(), Apply);
        Assert.True(await saves.SaveAsync(saves.Begin(), () => Task.FromResult("B and checkbox"), action => action(), Apply));
        slow.SetResult("A");
        Assert.False(await older);
        Assert.Equal(["B and checkbox"], applied);
    }

    [Fact]
    public async Task SupersededWhileWaitingForOperationDoesNotApply()
    {
        var saves = new SettingsSaveCoordinator();
        var entered = Signal<bool>();
        var release = Signal<bool>();
        var applied = false;
        var older = saves.SaveAsync(saves.Begin(), () => Task.FromResult("A"), async action =>
        {
            entered.SetResult(true);
            await release.Task;
            await action();
        }, value => { applied = true; return Task.CompletedTask; });
        await entered.Task;
        saves.Begin();
        release.SetResult(true);
        Assert.False(await older);
        Assert.False(applied);
    }

    [Fact]
    public async Task FailedLatestRequestDoesNotReviveOlderIntent()
    {
        var saves = new SettingsSaveCoordinator();
        var slow = Signal<string>();
        var applied = false;
        var older = saves.SaveAsync(saves.Begin(), () => slow.Task, action => action(),
            value => { applied = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => saves.SaveAsync<string>(saves.Begin(),
            () => Task.FromException<string>(new InvalidOperationException("Cannot save B")), action => action(), _ => Task.CompletedTask));
        slow.SetResult("A");
        Assert.False(await older);
        Assert.False(applied);
    }
}
