using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.App.Services;
using System.Text.Json.Nodes;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class DurableArchiveRelocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterFailedVerifiedOrFinalWriteRestoresBothKeeps(bool failFinal)
    {
        using var directory = new TestDirectory();
        using var store = new JsonStateStore(directory.GetPath("state"));
        var step = await SeedAsync(directory, store);
        store.BeforeStateWrite = state =>
        {
            if (failFinal ? state.RelocationJournal.Count == 0 : state.RelocationJournal.Any(e => e.DestinationVerified))
                throw new IOException("Injected durable boundary failure");
        };
        var result = await ArchiveRelocation.RelocateAsync(store, [step]);
        Assert.NotEmpty(result.Errors);
        var receipt = Assert.Single((await store.LoadAsync()).RelocationJournal);
        Assert.Equal(failFinal, receipt.DestinationVerified);
        Assert.Equal(!failFinal, File.Exists(receipt.SourcePath));
        Assert.True(File.Exists(receipt.DestinationPath));
        Assert.Single(receipt.ManualKeeps);
        Assert.Single(receipt.FilterKeeps);
        using var runtime = new AppRuntime(store.StateDirectory, allowStartupRegistration: false);
        await runtime.InitializeAsync();
        var recovered = await runtime.StateStore.LoadAsync();
        Assert.Empty(recovered.RelocationJournal);
        Assert.False(File.Exists(receipt.SourcePath));
        Assert.Equal(receipt.DestinationPath, Assert.Single(recovered.ManualKeeps).Path);
        Assert.Equal(receipt.DestinationPath, Assert.Single(recovered.FilterKeeps).Path);
        Assert.Equal(File.GetLastWriteTimeUtc(receipt.DestinationPath), recovered.FilterKeeps[0].LastWriteUtc);
        var revision = recovered.Revision;
        Assert.Empty(await ArchiveRelocation.RecoverAsync(runtime.StateStore));
        Assert.Equal(revision, (await runtime.StateStore.LoadAsync()).Revision);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("missing")]
    [InlineData("offline")]
    [InlineData("corrupt")]
    public async Task AmbiguousRecoveryPreservesReceiptAndBlocksEveryCleanup(string fault)
    {
        using var directory = new TestDirectory();
        using var store = new JsonStateStore(directory.GetPath("state"));
        var step = await SeedAsync(directory, store);
        store.BeforeStateWrite = state =>
        {
            if (state.RelocationJournal.Any(e => e.DestinationVerified)) throw new IOException("Verified write failed");
        };
        await ArchiveRelocation.RelocateAsync(store, [step]);
        store.BeforeStateWrite = null;
        var receipt = Assert.Single((await store.LoadAsync()).RelocationJournal);
        if (fault is "source" or "destination")
        {
            var path = fault == "source" ? receipt.SourcePath : receipt.DestinationPath;
            var stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "DIFFERENT IMAGE"); // Same length and timestamp, different SHA256.
            File.SetLastWriteTimeUtc(path, stamp);
        }
        if (fault == "missing") { File.Delete(receipt.SourcePath); File.Delete(receipt.DestinationPath); }
        if (fault == "offline") Directory.Move(step.From, step.From + "-unavailable");
        if (fault == "corrupt") await store.UpdateAsync(state => { state.RelocationJournal[0].Version = 99; return true; });
        using var runtime = new AppRuntime(store.StateDirectory, allowStartupRegistration: false);
        await runtime.InitializeAsync();
        Assert.NotNull(Assert.Single((await runtime.StateStore.LoadAsync()).RelocationJournal).LastError);
        var cleanup = await runtime.Scanner.CleanArchiveDuplicatesAsync();
        Assert.Equal(0, cleanup.Removed);
        Assert.Contains(ArchiveRelocation.PendingMessage, cleanup.Warnings);
        var watched = await runtime.Scanner.RefreshAndCleanArchiveCategoryAsync(VrcImageCategory.Emoji);
        Assert.Equal(0, watched.Removed);
        Assert.Contains(ArchiveRelocation.PendingMessage, watched.Warnings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Scanner.RescanArchiveFiltersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Scanner.ScanCategoryAsync(VrcImageCategory.Emoji));
        Assert.False((await runtime.StateStore.TryClearLocalDataAsync()).WasCleared);
        Assert.Single((await runtime.StateStore.LoadAsync()).RelocationJournal);
        if (fault == "source") Assert.Equal("DIFFERENT IMAGE", File.ReadAllText(receipt.SourcePath));
    }

    [Fact]
    public async Task CollisionDestinationAndCancellationCommitOnlyCompletedFile()
    {
        using var directory = new TestDirectory();
        using var store = new JsonStateStore(directory.GetPath("state"));
        var step = await SeedAsync(directory, store);
        Directory.CreateDirectory(step.To);
        File.WriteAllText(Path.Combine(step.To, "a.png"), "unrelated");
        File.WriteAllText(Path.Combine(step.From, "b.png"), "untouched");
        using var stop = new CancellationTokenSource();
        var result = await ArchiveRelocation.RelocateAsync(store, [step with { FileCount = 2 }],
            new CallbackProgress(_ => stop.Cancel()), stop.Token);
        Assert.True(result.Stopped);
        Assert.Equal(1, result.Moved);
        var destination = Assert.Single(result.Moves).To;
        Assert.Contains(".relocated-", destination);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(step.To, "a.png")));
        Assert.True(File.Exists(Path.Combine(step.From, "b.png")));
        var state = await store.LoadAsync();
        Assert.Empty(state.RelocationJournal);
        Assert.Equal(destination, Assert.Single(state.ManualKeeps).Path);
        Assert.Equal(destination, Assert.Single(state.FilterKeeps).Path);
    }

    [Fact]
    public async Task OldStateWithoutRelocationCollectionRoundTripsProtections()
    {
        using var directory = new TestDirectory();
        using var store = new JsonStateStore(directory.GetPath("state"));
        await SeedAsync(directory, store);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath))!.AsObject();
        json.Remove("relocationJournal");
        await File.WriteAllTextAsync(store.StatePath, json.ToJsonString());
        using var reopened = new JsonStateStore(store.StateDirectory);
        var state = await reopened.LoadAsync();
        Assert.Empty(state.RelocationJournal);
        Assert.Single(state.ManualKeeps);
        Assert.Single(state.FilterKeeps);
        await reopened.SaveAsync(state);
        Assert.Single((await reopened.LoadAsync()).ManualKeeps);
    }

    private sealed class CallbackProgress(Action<ArchiveRelocationProgress> callback) : IProgress<ArchiveRelocationProgress>
    {
        public void Report(ArchiveRelocationProgress value) => callback(value);
    }

    private static async Task<ArchiveRelocationStep> SeedAsync(TestDirectory directory, JsonStateStore store)
    {
        var from = directory.GetPath("old");
        var to = directory.GetPath("new");
        Directory.CreateDirectory(from);
        var source = Path.Combine(from, "a.png");
        File.WriteAllText(source, "protected image");
        await store.LoadAsync();
        await store.UpdateAsync(state =>
        {
            state.Settings.CategoryMappings.Single(m => m.Category == VrcImageCategory.Emoji).ArchivePath = to;
            state.ManualKeeps.Add(new(source, "identity", new FileInfo(source).Length, File.GetLastWriteTimeUtc(source)));
            state.FilterKeeps.Add(new(source, "identity", new FileInfo(source).Length, File.GetLastWriteTimeUtc(source), RemovalFilterReason.PrintFrame));
            return true;
        });
        return new(VrcImageCategory.Emoji, from, to, 1, 15);
    }

    [Fact]
    public async Task FailedPersistenceCannotRemoveProtectedSource()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old");
        var to = directory.GetPath("new");
        Directory.CreateDirectory(from);
        var source = Path.Combine(from, "a.png");
        File.WriteAllText(source, "protected image");
        using var store = new JsonStateStore(directory.GetPath("state"));
        await store.LoadAsync();
        await store.UpdateAsync(state =>
        {
            state.ManualKeeps.Add(new(source, "identity", new FileInfo(source).Length, File.GetLastWriteTimeUtc(source)));
            return true;
        });
        Directory.CreateDirectory(store.TemporaryPath); // Deterministic state-write failure.
        var result = await ArchiveRelocation.RelocateAsync(store, [new(VrcImageCategory.Emoji, from, to, 1, 15)]);
        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(source), "A failed durable write must preserve the protected original.");
    }
}
