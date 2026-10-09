using System.IO;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;

namespace VrcPicSorter.App.Services;

public sealed class WatchService : IDisposable
{
    private readonly ScanCoordinator _scanner;
    private readonly ArchiveIndexer _indexer;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<VrcImageCategory, int> _retryAttempts = [];
    private readonly Dictionary<VrcImageCategory, PendingCategory> _processing = [];
    private long _generation;
    internal Func<VrcImageCategory, bool, string[], CancellationToken, Task<CategoryScanResult>>? ScanOverride { get; set; }
    private readonly TimeSpan _retryInterval;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<VrcImageCategory, PendingCategory> _pending = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<WatchTarget> _targets = [];
    private System.Threading.Timer? _retryTimer;
    private System.Threading.Timer? _sweepTimer;
    private CancellationTokenSource _runCancellation = new();
    /// <summary>
    /// Every scan this watcher has started and not yet finished. A second category is claimed
    /// before it waits for the scan gate, so a single field held the one that had not started
    /// rather than the one actually running, and Stop cancelled the wrong scan.
    /// </summary>
    private readonly HashSet<CancellationTokenSource> _activeScanCancellations = [];
    private bool _analyzeOnDetection = true;
    private bool _isRunning;
    private bool _disposed;

    public WatchService(
        ScanCoordinator scanner,
        ArchiveIndexer indexer,
        TimeSpan? debounce = null,
        TimeSpan? retryInterval = null,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _debounce = debounce ?? TimeSpan.FromSeconds(1.5);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(15);
    }

    public event EventHandler<CategoryScanResult>? ScanCompleted;

    public event EventHandler<WatcherFailureEventArgs>? ScanFailed;

    /// <summary>Raised as soon as the watcher reports a source file, before any scan runs.</summary>
    public event EventHandler<WatchDetectionEventArgs>? ChangesDetected;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>Cancels the scan the watcher is running right now, if any. Watching continues.</summary>
    public void CancelActiveScan()
    {
        lock (_sync)
        {
            ClearPending();
            foreach (var cancellation in _activeScanCancellations.ToArray())
            {
                cancellation.Cancel();
            }
        }
    }

    /// <param name="analyzeOnDetection">
    /// When true, each reported arrival is analyzed immediately. When false, arrivals are only
    /// announced and nothing is analyzed until <paramref name="sweepInterval"/> elapses.
    /// </param>
    public void Start(
        IEnumerable<CategoryMapping> mappings,
        IEnumerable<LegacyArchiveMapping>? legacyArchives = null,
        TimeSpan? sweepInterval = null,
        bool analyzeOnDetection = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replacements = new List<FileSystemWatcher>();
        var enabledMappings = mappings.Where(item => item.IsEnabled).ToArray();
        var enabledCategories = enabledMappings.Select(item => item.Category).ToHashSet();
        var retainedArchives = (legacyArchives ?? [])
            .Where(item => enabledCategories.Contains(item.Category))
            .ToArray();
        try
        {
            foreach (var mapping in enabledMappings)
            {
                AddWatcher(replacements, mapping.SourcePath, mapping.Category, archive: false);
                AddWatcher(replacements, mapping.ArchivePath, mapping.Category, archive: true);
            }


            foreach (var legacy in retainedArchives)
            {
                AddWatcher(replacements, legacy.ArchivePath, legacy.Category, archive: true);
            }
        }
        catch
        {
            DisposeWatchers(replacements);
            throw;
        }

        try
        {
            lock (_sync)
            {
                _generation++;
                ClearPending();
                _retryAttempts.Clear();
                _processing.Clear();
                _runCancellation.Cancel();
                _runCancellation = new CancellationTokenSource();
                DisposeWatchers(_watchers);
                _watchers.Clear();
                _watchers.AddRange(replacements);
                _targets.Clear();
                _targets.AddRange(enabledMappings.SelectMany(mapping => new[]
                {
                    new WatchTarget(mapping.SourcePath, mapping.Category, false),
                    new WatchTarget(mapping.ArchivePath, mapping.Category, true),
                }));
                _targets.AddRange(retainedArchives
                    .Select(item => new WatchTarget(item.ArchivePath, item.Category, true)));
                _isRunning = enabledMappings.Length > 0;
                _analyzeOnDetection = analyzeOnDetection;
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = true;
                }

                _retryTimer?.Dispose();
                var generation = _generation;
                _retryTimer = _isRunning
                    ? new System.Threading.Timer(_ => InSession(generation, RetryAttachNewlyAvailableFolders), null, _retryInterval, _retryInterval)
                    : null;

                // Periodic safety net: filesystem notifications can be dropped, so sweep the
                // whole category on a fixed interval as well.
                _sweepTimer?.Dispose();
                var categories = enabledMappings.Select(item => item.Category).ToArray();
                _sweepTimer = _isRunning && sweepInterval is { } interval && interval > TimeSpan.Zero
                    ? new System.Threading.Timer(
                        _ => InSession(generation, () => RequestPeriodicSweep(categories)),
                        null,
                        interval,
                        interval)
                    : null;
            }
        }
        catch
        {
            lock (_sync)
            {
                DisposeWatchers(_watchers);
                _watchers.Clear();
                _targets.Clear();
                _isRunning = false;
                _retryTimer?.Dispose();
                _retryTimer = null;
                _sweepTimer?.Dispose();
                _sweepTimer = null;
            }

            throw;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isRunning = false;
            _generation++;
            _retryAttempts.Clear();
            _processing.Clear();
            _runCancellation.Cancel();
            ClearPending();
            foreach (var cancellation in _activeScanCancellations.ToArray())
            {
                cancellation.Cancel();
            }

            _retryTimer?.Dispose();
            _retryTimer = null;
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            DisposeWatchers(_watchers);
            _watchers.Clear();
            _targets.Clear();
        }
    }

    public async Task StopAsync()
    {
        Stop();
        await _scanGate.WaitAsync().ConfigureAwait(false);
        _scanGate.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _sweepTimer?.Dispose();
        _runCancellation.Dispose();
        _scanGate.Dispose();
    }

    private static void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void AddWatcher(
        ICollection<FileSystemWatcher> watchers,
        string path,
        VrcImageCategory category,
        bool archive)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = false,
        };
        void QueueIfCurrent(string? filePath)
        {
            lock (_sync)
            {
                if (_watchers.Contains(watcher)) Queue(category, archive, filePath);
            }
        }
        FileSystemEventHandler changed = (_, e) => QueueIfCurrent(e.FullPath);
        FileSystemEventHandler removed = (_, _) => QueueIfCurrent(null);
        RenamedEventHandler renamed = (_, e) => QueueIfCurrent(e.FullPath);
        ErrorEventHandler error = (_, _) => HandleWatcherError(watcher, category, archive);
        watcher.Created += changed;
        watcher.Changed += changed;
        // A deleted file carries a path but is not an arrival, so it must not be announced.
        watcher.Deleted += removed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watchers.Add(watcher);
    }

    private void HandleWatcherError(FileSystemWatcher watcher, VrcImageCategory category, bool archive)
    {
        lock (_sync)
        {
            if (!_watchers.Remove(watcher)) return;
            DisposeWatchers([watcher]);

            // The watcher buffer overflowed or the handle died, so individual change
            // notifications were lost. A full sweep is the only safe recovery.
            Queue(category, archive, path: null, requestFullScan: true);
        }
    }

    internal void Queue(
        VrcImageCategory category,
        bool archive,
        string? path,
        bool requestFullScan = false)
    {
        bool announced;
        lock (_sync)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            if (_pending.TryGetValue(category, out var existing))
            {
                announced = Accumulate(existing, archive, path, requestFullScan);
                if (!existing.IsRetry) existing.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                var pending = CreatePending(category, archive);
                announced = Accumulate(pending, archive, path, requestFullScan);
                pending.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                _pending[category] = pending;
            }
        }

        // Raised outside the lock: handlers marshal to the UI thread and must never block it.
        if (announced)
        {
            ChangesDetected?.Invoke(
                this,
                new WatchDetectionEventArgs(category, Path.GetFileName(path!)));
        }
    }

    /// <summary>Returns true when this call added a source file that was not already pending.</summary>
    private static bool Accumulate(
        PendingCategory pending,
        bool archive,
        string? path,
        bool requestFullScan)
    {
        pending.ArchiveChanged |= archive;
        pending.FullScanRequested |= requestFullScan;
        var announced = !archive && !string.IsNullOrWhiteSpace(path)
            && !pending.FullScanRequested && pending.SourcePaths.Add(path);
        if (pending.SourcePaths.Count > 1024) pending.FullScanRequested = true;
        if (pending.FullScanRequested) pending.SourcePaths.Clear();
        return announced;
    }

    private void RequestPeriodicSweep(IReadOnlyList<VrcImageCategory> categories)
    {
        try
        {
            foreach (var category in categories)
            {
                Queue(category, archive: false, path: null, requestFullScan: true);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Periodic watch sweep could not be queued: {exception}");
        }
    }

    private void AttachNewlyAvailableFolders()
    {
        lock (_sync)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            for (var index = _watchers.Count - 1; index >= 0; index--)
            {
                var watcher = _watchers[index];
                if (Directory.Exists(watcher.Path))
                {
                    continue;
                }

                _watchers.RemoveAt(index);
                DisposeWatchers([watcher]);
            }

            foreach (var target in _targets)
            {
                if (!Directory.Exists(target.Path)
                    || _watchers.Any(watcher => string.Equals(
                        watcher.Path,
                        Path.GetFullPath(target.Path),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var additions = new List<FileSystemWatcher>();
                    AddWatcher(additions, target.Path, target.Category, target.Archive);
                    foreach (var watcher in additions)
                    {
                        _watchers.Add(watcher);
                        try
                        {
                            watcher.EnableRaisingEvents = true;
                        }
                        catch
                        {
                            _watchers.Remove(watcher);
                            DisposeWatchers([watcher]);
                            throw;
                        }
                    }

                    // The folder was gone and is back, so every change made while it was
                    // missing was lost. A full sweep is the only honest recovery.
                    Queue(target.Category, target.Archive, path: null, requestFullScan: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    ScanFailed?.Invoke(this, new WatcherFailureEventArgs(target.Category, exception));
                }
            }
        }
    }

    private void RetryAttachNewlyAvailableFolders()
    {
        try
        {
            AttachNewlyAvailableFolders();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reattach watched folders: {exception}");
        }
    }

    private async Task ProcessAsync(VrcImageCategory category, PendingCategory expected, long generation)
    {
        bool fullScanRequested;
        string[] scanSourcePaths;
        CancellationTokenSource scanCancellation;
        CancellationToken cancellationToken;
        lock (_sync)
        {
            if (!_isRunning || generation != _generation || _processing.ContainsKey(category)
                || !_pending.TryGetValue(category, out var pending) || !ReferenceEquals(pending, expected))
            {
                return;
            }

            _pending.Remove(category);
            pending.Timer.Dispose();
            fullScanRequested = pending.FullScanRequested;

            // In OnInterval mode an arrival is announced but not analyzed; the sweep picks it up.
            scanSourcePaths = _analyzeOnDetection ? [.. pending.SourcePaths] : [];
            if (!fullScanRequested && scanSourcePaths.Length == 0 && !pending.ArchiveChanged)
            {
                return;
            }

            _processing[category] = pending;
            // Linked so Stop cancels this scan while watching itself keeps running. Not disposed
            // here if it belongs to another in-flight category; each run disposes its own.
            scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_runCancellation.Token);
            _activeScanCancellations.Add(scanCancellation);
            cancellationToken = scanCancellation.Token;
        }

        var enteredScanGate = false;
        var retryRequired = false;
        var succeeded = false;
        try
        {
            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredScanGate = true;
            // Watching analyzes only images that arrived while it was running. A full sweep
            // happens on demand from Scan now, or here when a watcher error lost events.
            CategoryScanResult result;
            if (ScanOverride is { } scan)
            {
                result = await scan(category, fullScanRequested, scanSourcePaths, cancellationToken).ConfigureAwait(false);
            }
            else if (fullScanRequested)
            {
                result = await _scanner.ScanCategoryAsync(category, cancellationToken).ConfigureAwait(false);
            }
            else if (scanSourcePaths.Length > 0)
            {
                result = await _scanner.ScanIncomingPathsAsync(category, scanSourcePaths, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Archive-only change: refresh and clean the archive without re-analyzing
                // anything in the input folders.
                var cleanup = await _scanner.RefreshAndCleanArchiveCategoryAsync(category, cancellationToken)
                    .ConfigureAwait(false);
                result = new CategoryScanResult(
                    category,
                    0,
                    0,
                    0,
                    0,
                    cleanup.Warnings,
                    ArchiveDuplicatesRemoved: cleanup.Removed,
                    RetryRequired: cleanup.RetryRequired);
            }

            retryRequired = result.RetryRequired;
            succeeded = !retryRequired && result.Errors.Count == 0;
            ScanCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            // Either watching stopped or the user pressed Stop. Both are clean exits.
        }
        catch (Exception exception)
        {
            retryRequired = ScanRetry.IsTransient(exception);
            if (!_disposed)
            {
                Exception failure = exception;
                try
                {
                    await _indexer.MarkStaleAsync(category, "Watcher scan failed.").ConfigureAwait(false);
                }
                catch (Exception staleException)
                {
                    failure = new AggregateException(exception, staleException);
                }

                try
                {
                    ScanFailed?.Invoke(this, new WatcherFailureEventArgs(category, failure));
                }
                catch (Exception reportingException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Failed to report watcher scan exception: {reportingException}");
                }
            }
        }
        finally
        {
            if (enteredScanGate)
            {
                _scanGate.Release();
            }

            lock (_sync)
            {
                _activeScanCancellations.Remove(scanCancellation);
                if (generation == _generation && _processing.TryGetValue(category, out var current) && ReferenceEquals(current, expected))
                {
                    _processing.Remove(category);
                    if (!cancellationToken.IsCancellationRequested && _isRunning && !_disposed)
                    {
                        if (succeeded) _retryAttempts.Remove(category);
                        if (retryRequired) ScheduleRetry(category, _analyzeOnDetection || fullScanRequested);
                        else if (_pending.TryGetValue(category, out var next)) next.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                    }
                    else if (_isRunning && _pending.TryGetValue(category, out var next))
                        next.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                }
            }

            scanCancellation.Dispose();
        }
    }

    // All scheduling state is guarded by _sync. Timer identity and generation reject callbacks
    // already queued when a timer was disposed or a watch session was replaced.
    private PendingCategory CreatePending(VrcImageCategory category, bool archive)
    {
        var pending = new PendingCategory(archive);
        var generation = _generation;
        pending.Timer = _timeProvider.CreateTimer(_ => _ = ProcessAsync(category, pending, generation),
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return pending;
    }

    private void ClearPending()
    {
        foreach (var pending in _pending.Values) pending.Timer.Dispose();
        _pending.Clear();
    }

    private void InSession(long generation, Action callback)
    {
        lock (_sync)
        {
            if (_isRunning && generation == _generation) callback();
        }
    }

    private void ScheduleRetry(VrcImageCategory category, bool fullScan)
    {
        if (!_pending.TryGetValue(category, out var pending))
        {
            pending = CreatePending(category, archive: true);
            _pending[category] = pending;
        }
        // An archive-only failure in OnInterval mode must not start source analysis early.
        pending.FullScanRequested |= fullScan;
        pending.ArchiveChanged = true;
        pending.SourcePaths.Clear();
        pending.IsRetry = true;
        var attempt = _retryAttempts.GetValueOrDefault(category);
        int[] seconds = [2, 5, 15, 30, 60];
        _retryAttempts[category] = Math.Min(attempt + 1, seconds.Length - 1);
        pending.Timer.Change(TimeSpan.FromSeconds(seconds[attempt]), Timeout.InfiniteTimeSpan);
    }

    private sealed class PendingCategory(bool archiveChanged)
    {
        public bool ArchiveChanged { get; set; } = archiveChanged;

        public bool FullScanRequested { get; set; }

        /// <summary>Source files the watcher reported since the last debounce window.</summary>
        public HashSet<string> SourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ITimer Timer { get; set; } = null!;
        public bool IsRetry { get; set; }
    }

    private sealed record WatchTarget(string Path, VrcImageCategory Category, bool Archive);
}

public sealed record WatcherFailureEventArgs(VrcImageCategory Category, Exception Exception);

public sealed record WatchDetectionEventArgs(VrcImageCategory Category, string FileName);
