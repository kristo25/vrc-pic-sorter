using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;

namespace VrcPicSorter.Tests.Services;

public sealed class WatchRetryTests
{
    [Fact]
    public async Task TransientResultsUseCappedBackoffAndSuccessResetsIt()
    {
        using var fixture = await Fixture.Create();
        var calls = 0;
        var fail = true;
        fixture.Watcher.ScanOverride = (category, full, paths, token) =>
        {
            calls++;
            if (calls > 1) Assert.True(full);
            return Task.FromResult(Result(fail));
        };
        fixture.Queue();
        foreach (var seconds in new[] { 2, 5, 15, 30, 60, 60 })
        {
            fixture.Clock.Fire();
            Assert.Equal(TimeSpan.FromSeconds(seconds), fixture.Clock.Next.Due);
            // A fresh notification merges into the retry; it cannot bypass the delay.
            fixture.Queue();
            Assert.Equal(TimeSpan.FromSeconds(seconds), fixture.Clock.Next.Due);
        }
        fail = false;
        fixture.Clock.Fire();
        Assert.Empty(fixture.Clock.Active);
        fail = true;
        fixture.Watcher.ScanOverride = (_, _, _, _) => Task.FromResult(Result(true));
        fixture.Queue();
        fixture.Clock.Fire();
        Assert.Equal(TimeSpan.FromSeconds(2), fixture.Clock.Next.Due);
    }

    [Fact]
    public async Task ExceptionMergesEventsArrivingDuringRunIntoOneFullRetry()
    {
        using var fixture = await Fixture.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Watcher.ScanOverride = async (_, full, _, token) =>
        {
            if (++calls == 1)
            {
                await release.Task;
                throw new IOException("temporary storage failure");
            }
            Assert.True(full);
            return Result(false);
        };
        fixture.Queue();
        fixture.Clock.Fire();
        fixture.Queue();
        fixture.Watcher.Queue(VrcImageCategory.Emoji, true, null);
        fixture.Clock.Fire(); // Callback while the category runs must not consume new work.
        release.SetResult();
        await Eventually(() => fixture.Clock.Active.Count == 1 && fixture.Clock.Next.Due == TimeSpan.FromSeconds(2));
        fixture.Clock.Fire();
        Assert.Equal(2, calls);
        Assert.Empty(fixture.Clock.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelBackoffAndStopRestartRejectOldCallbacks(bool restart)
    {
        using var fixture = await Fixture.Create();
        var calls = 0;
        fixture.Watcher.ScanOverride = (_, _, _, _) => { calls++; return Task.FromResult(Result(true)); };
        fixture.Queue();
        fixture.Clock.Fire();
        var obsolete = fixture.Clock.Next;
        if (restart)
        {
            fixture.Watcher.Stop();
            fixture.Watcher.Start(fixture.Mappings);
        }
        else fixture.Watcher.CancelActiveScan();
        obsolete.FireEvenIfDisposed();
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Clock.Active);
        fixture.Queue();
        fixture.Clock.Fire();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelDuringRunAndGateWaitDoesNotReplayButNewEventsStillRun()
    {
        using var fixture = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Watcher.ScanOverride = async (_, _, _, token) =>
        {
            calls++;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return Result(false);
        };
        fixture.Queue();
        fixture.Clock.Fire();
        await entered.Task;
        fixture.Watcher.Queue(VrcImageCategory.Stickers, false, "waiting.png");
        fixture.Clock.Fire();
        fixture.Watcher.CancelActiveScan();
        await cancelled.Task;
        await fixture.Watcher.StopAsync();
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Clock.Active);
        fixture.Watcher.Start(fixture.Mappings);
        fixture.Watcher.ScanOverride = (_, _, _, _) => { calls++; return Task.FromResult(Result(false)); };
        fixture.Queue();
        fixture.Clock.Fire();
        await Eventually(() => calls == 2);
        Assert.Empty(fixture.Clock.Active);
    }

    [Fact]
    public async Task OldCompletionCannotScheduleIntoRestartedSession()
    {
        using var fixture = await Fixture.Create();
        var release = new TaskCompletionSource<CategoryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Watcher.ScanOverride = (_, _, _, _) => release.Task;
        fixture.Queue();
        fixture.Clock.Fire();
        fixture.Watcher.Stop();
        fixture.Watcher.Start(fixture.Mappings);
        release.SetResult(Result(true));
        await fixture.Watcher.StopAsync();
        Assert.Empty(fixture.Clock.Active);
    }

    [Fact]
    public async Task WarningAndIntervalArrivalsDoNotScheduleRetry()
    {
        using var fixture = await Fixture.Create();
        var calls = 0;
        fixture.Watcher.ScanOverride = (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new CategoryScanResult(VrcImageCategory.Emoji, 0, 0, 0, 0, ["attention only"]));
        };
        fixture.Queue(); fixture.Clock.Fire();
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Clock.Active);
        fixture.Watcher.Start(fixture.Mappings, analyzeOnDetection: false);
        fixture.Queue(); fixture.Clock.Fire();
        Assert.Equal(1, calls);
        fixture.Watcher.Queue(VrcImageCategory.Emoji, true, null);
        fixture.Clock.Fire();
        Assert.Equal(2, calls);
        Assert.Empty(fixture.Clock.Active);
    }

    [Fact]
    public async Task MalformedArchiveRemainsBlockedWithoutRetryTimer()
    {
        using var fixture = await Fixture.Create();
        var mapping = fixture.Mappings.Single(m => m.Category == VrcImageCategory.Emoji);
        // The fixture started with missing paths, so only the explicit event below is delivered.
        Directory.CreateDirectory(mapping.SourcePath);
        Directory.CreateDirectory(mapping.ArchivePath);
        var malformed = Path.Combine(mapping.ArchivePath, "malformed.png");
        await File.WriteAllTextAsync(malformed, "not an image");
        var done = new TaskCompletionSource<CategoryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Watcher.ScanCompleted += (_, result) => done.TrySetResult(result);
        fixture.Watcher.Queue(VrcImageCategory.Emoji, true, null);
        fixture.Clock.Fire();
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Watcher.StopAsync();
        Assert.False(result.RetryRequired);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(fixture.Clock.Active);
        Assert.True(File.Exists(malformed));
        Assert.Equal(IndexStatus.Unavailable, (await fixture.Store.LoadAsync()).ArchiveIndex.Categories.Single(c => c.Category == VrcImageCategory.Emoji).Status);
    }

    private static CategoryScanResult Result(bool retry) => new(VrcImageCategory.Emoji, 0, 0, 0, 0,
        retry ? ["transient"] : [], RetryRequired: retry);

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public JsonStateStore Store { get; private set; } = null!;
        public WatchService Watcher { get; private set; } = null!;
        public CategoryMapping[] Mappings { get; private set; } = [];
        public ManualClock Clock { get; } = new();
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            fixture.Store = FileRouterTests.CreateStore(fixture._directory,
                fixture._directory.GetPath("source"), fixture._directory.GetPath("archive", "Emoji"));
            fixture.Mappings = (await fixture.Store.LoadAsync()).Settings.CategoryMappings.ToArray();
            var decoder = new ImageDecoder();
            var indexer = new ArchiveIndexer(fixture.Store, decoder);
            var scanner = new ScanCoordinator(fixture.Store, indexer, decoder,
                new FileRouter(fixture.Store, decoder, new FileRouterTests.FakeRecycleBinService()));
            fixture.Watcher = new WatchService(scanner, indexer, TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1), fixture.Clock);
            fixture.Watcher.Start(fixture.Mappings);
            return fixture;
        }
        public void Queue() => Watcher.Queue(VrcImageCategory.Emoji, false, "arrival.png");
        public void Dispose() { Watcher.Dispose(); Store.Dispose(); _directory.Dispose(); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public List<ManualTimer> Active { get { lock (_timers) return _timers.Where(t => t.Due >= TimeSpan.Zero).ToList(); } }
        public ManualTimer Next => Assert.Single(Active);
        public void Fire() => Next.FireEvenIfDisposed();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state) { Due = dueTime };
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private long _ticks = -TimeSpan.TicksPerMillisecond;
            public TimeSpan Due { get => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)); set => Interlocked.Exchange(ref _ticks, value.Ticks); }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return true; }
            public void Dispose() => Due = Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void FireEvenIfDisposed() { Due = Timeout.InfiniteTimeSpan; callback(state); }
        }
    }
}

