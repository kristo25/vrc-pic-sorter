using System.Collections.Concurrent;
using System.Diagnostics;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;
using ActivityKind = VrcPicSorter.Core.Models.ActivityKind;

namespace VrcPicSorter.Core.Scanning;

/// <summary>Measures pure preparation work. Routing and index publication stay with their callers.</summary>
public sealed class ScanConcurrencyController(JsonStateStore store, ImageDecoder decoder)
{
    public const int AlgorithmVersion = 1;
    private JsonStateStore Store => store;
    private readonly SemaphoreSlim _calibration = new(1, 1);
    private readonly ConcurrentDictionary<string, Cached> _samples = new(StringComparer.OrdinalIgnoreCase);
    internal DecodeMemoryBudget Memory { get; } = new();
    public event Action<string>? StatusChanged;
    public Func<double>? UiDelayMilliseconds { get; set; }
    private sealed record Cached(long Length, DateTime Written, ImageFingerprint Fingerprint);

    public static int[] Candidates(int processors)
    {
        processors = Math.Max(1, processors);
        var values = new List<int> { 1 };
        while (values[^1] <= processors / 2) values.Add(values[^1] * 2);
        if (values[^1] != processors) values.Add(processors);
        return values.ToArray();
    }

    public static int SelectWorkers(IEnumerable<WorkerMeasurement> measurements, int processors)
    {
        var valid = measurements.Where(m => m.Workers > 0 && m.Workers <= processors
            && double.IsFinite(m.ImagesPerSecond) && m.ImagesPerSecond > 0).ToArray();
        if (valid.Length == 0) return Math.Min(2, Math.Max(1, processors));
        var best = valid.Max(m => m.ImagesPerSecond);
        return valid.Where(m => m.ImagesPerSecond >= best * .95).Min(m => m.Workers);
    }

    internal static bool Matches(AutoScanProfile? profile, AppSettings settings, int processors) =>
        profile is not null && profile.ProcessorCount == processors && profile.AlgorithmVersion == AlgorithmVersion
        && profile.OutputRevision == settings.OutputFolderRevision
        && string.Equals(profile.OutputRoot, settings.OutputRootPath, StringComparison.OrdinalIgnoreCase);

    public async Task PrepareAsync(AppSettings settings, IEnumerable<string>? archivePaths,
        IEnumerable<string>? incomingPaths, CancellationToken token)
    {
        if (settings.ScanWorkers != 0 || !settings.OutputRootConfirmed) return;
        await _calibration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            settings = (await store.LoadAsync(token).ConfigureAwait(false)).Settings;
            if (settings.ScanWorkers != 0) return;
            var existing = Matches(settings.AutoScanProfile, settings, Environment.ProcessorCount) ? settings.AutoScanProfile : null;
            var needArchive = existing is null || !existing.Measurements.Any(m => m.Phase == "Archive");
            var needIncoming = existing is null || !existing.Measurements.Any(m => m.Phase == "Incoming");
            if (existing is not null && !(needArchive && archivePaths?.Take(8).Count() >= 8)
                && !(needIncoming && incomingPaths?.Take(8).Count() >= 8)) return;
            _samples.Clear();
            StatusChanged?.Invoke("Testing archive responsiveness…");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var profile = existing ?? new AutoScanProfile
            {
                OutputRoot = settings.OutputRootPath,
                OutputRevision = settings.OutputFolderRevision,
                ProcessorCount = Environment.ProcessorCount,
                AlgorithmVersion = AlgorithmVersion,
                ArchiveWorkers = Math.Min(2, Environment.ProcessorCount),
                IncomingWorkers = Math.Min(2, Environment.ProcessorCount),
                Learning = true,
                MeasuredUtc = DateTimeOffset.UtcNow,
            };
            try
            {
                if (existing is null)
                    profile.StorageMilliseconds = await ProbeAsync(settings.OutputRootPath, deadline.Token).ConfigureAwait(false);
                var archives = await Task.Run(() => Sample(archivePaths ?? Enumerate(settings.CategoryMappings.Select(m => m.ArchivePath)
                    .Concat(settings.LegacyArchiveMappings.Select(m => m.ArchivePath))), deadline.Token), deadline.Token).ConfigureAwait(false);
                var incoming = await Task.Run(() => Sample(incomingPaths ?? Enumerate(settings.CategoryMappings.Where(m => m.IsEnabled)
                    .Select(m => m.SourcePath)), deadline.Token), deadline.Token).ConfigureAwait(false);
                // Alternate phases so one slow storage location cannot consume all candidate rounds.
                foreach (var count in Candidates(Environment.ProcessorCount))
                {
                    foreach (var (phase, paths) in new[] { ("Archive", archives), ("Incoming", incoming) })
                    {
                        if (paths.Length < 8 || (phase == "Archive" ? !needArchive : !needIncoming)) continue;
                        var rate = await MeasureAsync(paths, count, deadline.Token).ConfigureAwait(false);
                        if ((UiDelayMilliseconds?.Invoke() ?? 0) <= 150)
                            profile.Measurements.Add(new WorkerMeasurement(phase, count, rate));
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Only completed candidate rounds are eligible; normal work continues learning.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or InvalidOperationException or NotSupportedException)
            {
                StatusChanged?.Invoke($"Auto calibration unavailable; using up to 2 workers. {error.Message}");
                return; // Do not persist failed storage/calibration as a valid measurement.
            }
            token.ThrowIfCancellationRequested();
            profile.ArchiveWorkers = SelectWorkers(profile.Measurements.Where(m => m.Phase == "Archive"), profile.ProcessorCount);
            profile.IncomingWorkers = SelectWorkers(profile.Measurements.Where(m => m.Phase == "Incoming"), profile.ProcessorCount);
            profile.Learning = !profile.Measurements.Any(m => m.Phase == "Archive" && m.Workers == profile.ProcessorCount)
                || !profile.Measurements.Any(m => m.Phase == "Incoming" && m.Workers == profile.ProcessorCount);
            await store.UpdateAsync(state =>
            {
                if (!SameOutput(state.Settings, settings)) return false;
                state.Settings.AutoScanProfile = profile;
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Scan,
                    Level = ActivityLevel.Information,
                    Message = $"Auto calibrated: archive {profile.ArchiveWorkers}, incoming {profile.IncomingWorkers} of {profile.ProcessorCount}; storage {profile.StorageMilliseconds:F1} ms; learning={profile.Learning}."
                });
                return true;
            }, token).ConfigureAwait(false);
        }
        finally { _calibration.Release(); }
    }

    private static bool SameOutput(AppSettings left, AppSettings right) => left.ScanWorkers == 0
        && left.OutputFolderRevision == right.OutputFolderRevision
        && string.Equals(left.OutputRootPath, right.OutputRootPath, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Enumerate(IEnumerable<string> roots)
    {
        // Bound each root independently so retained archives are represented too.
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                foreach (var path in PathBoundary.EnumerateFilesWithoutReparsePoints(root)
                    .Where(p => ArchiveIndexer.SupportedExtensions.Contains(Path.GetExtension(p))).Take(16)) yield return path;
    }

    private static string[] Sample(IEnumerable<string> paths, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var path in SpreadPaths(paths).Take(128))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) continue;
                var info = SixLabors.ImageSharp.Image.Identify(path);
                if (info is null || DecodeMemoryBudget.EstimateBytes(info.Width, info.Height,
                    Math.Max(1, info.FrameMetadataCollection.Count)) > 48L * 1024 * 1024) continue;
                result.Add(path);
                if (result.Count == 32) break;
            }
            catch (Exception e) when (IsReadFailure(e)) { }
        }
        return result.ToArray();
    }

    internal static IEnumerable<string> SpreadPaths(IEnumerable<string> paths)
    {
        var groups = paths.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<string>(group.Take(128))).ToArray();
        while (groups.Any(group => group.Count > 0))
            foreach (var group in groups)
                if (group.TryDequeue(out var path)) yield return path;
    }

    private async Task<double> MeasureAsync(string[] paths, int workers, CancellationToken token)
    {
        // One untimed warmup avoids rewarding later candidates for JIT/decoder initialization.
        if (workers == 1) await ReadFreshAsync(paths[0], token).ConfigureAwait(false);
        var rates = new double[3];
        for (var round = 0; round < rates.Length; round++)
        {
            var timer = Stopwatch.StartNew();
            await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
                async (path, ct) => { await ReadFreshAsync(path, ct).ConfigureAwait(false); }).ConfigureAwait(false);
            rates[round] = paths.Length / Math.Max(.000001, timer.Elapsed.TotalSeconds);
        }
        Array.Sort(rates);
        return rates[1];
    }

    internal async Task<ImageFingerprint> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_samples.TryRemove(path, out var cached))
        {
            var info = new FileInfo(path);
            if (info.Length == cached.Length && info.LastWriteTimeUtc == cached.Written) return cached.Fingerprint;
        }
        return await DecodeCheckedAsync(path, token).ConfigureAwait(false);
    }

    private async Task ReadFreshAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        var length = info.Length; var written = info.LastWriteTimeUtc;
        var fingerprint = await DecodeCheckedAsync(path, token).ConfigureAwait(false);
        _samples[path] = new Cached(length, written, fingerprint);
    }

    private async Task<ImageFingerprint> DecodeCheckedAsync(string path, CancellationToken token)
    {
        var before = new FileInfo(path);
        var length = before.Length; var written = before.LastWriteTimeUtc;
        var fingerprint = await decoder.FingerprintAsync(path, Memory, token).ConfigureAwait(false);
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc != written)
            throw new IOException("Image changed during analysis; retry the scan.");
        return fingerprint;
    }

    internal static bool IsReadFailure(Exception e) => e is IOException or UnauthorizedAccessException
        or InvalidDataException or InvalidOperationException or NotSupportedException
        or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.ImageFormatException;

    private static async Task<double> ProbeAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) throw new IOException("Output folder is unavailable.");
        var folder = Path.Combine(root, ".vrc-auto-probe-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "probe.tmp");
        PathBoundary.EnsureSafeDestination(root, path, "Calibration probe");
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(65536);
            var timings = new double[3];
            for (var i = 0; i < timings.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Position = 0;
                    var actual = new byte[bytes.Length];
                    await stream.ReadExactlyAsync(actual, token).ConfigureAwait(false);
                    if (!bytes.AsSpan().SequenceEqual(actual)) throw new IOException("Calibration probe verification failed.");
                }
                File.Delete(path); timings[i] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings); return timings[1];
        }
        finally
        {
            // Exact owned names only; no recursive cleanup on a user-selected output folder.
            try { File.Delete(path); Directory.Delete(folder, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal async Task<WorkerSession> StartAsync(string phase, CancellationToken token)
    {
        var settings = (await store.LoadAsync(token).ConfigureAwait(false)).Settings;
        var profile = Matches(settings.AutoScanProfile, settings, Environment.ProcessorCount) ? settings.AutoScanProfile : null;
        var count = settings.ScanWorkers != 0 ? settings.ScanWorkers
            : phase == "Archive" ? profile?.ArchiveWorkers ?? Math.Min(2, Environment.ProcessorCount)
            : profile?.IncomingWorkers ?? Math.Min(2, Environment.ProcessorCount);
        StatusChanged?.Invoke(settings.ScanWorkers == 0
            ? $"Auto: using {count} of {Environment.ProcessorCount} workers ({phase.ToLowerInvariant()})"
            : $"Using {count} workers ({phase.ToLowerInvariant()})");
        return new WorkerSession(this, settings, phase, count);
    }

    internal sealed class WorkerSession(ScanConcurrencyController owner, AppSettings settings, string phase, int count)
    {
        private readonly WorkerTuner _tuner = new(count, Environment.ProcessorCount);
        private readonly Stopwatch _window = Stopwatch.StartNew();
        private int _completed; private bool _errors; private long _waits = owner.Memory.Waits;
        public int Workers => settings.ScanWorkers == 0 ? _tuner.Workers : settings.ScanWorkers;
        public void Completed(bool success, bool backlog)
        {
            _completed++; _errors |= !success;
            if (settings.ScanWorkers != 0 || _window.Elapsed.TotalSeconds < 5 || _completed < 2) return;
            var before = Workers;
            _tuner.Observe(_completed / _window.Elapsed.TotalSeconds, _errors,
                (owner.UiDelayMilliseconds?.Invoke() ?? 0) > 150, owner.Memory.Waits != _waits, backlog);
            _completed = 0; _errors = false; _waits = owner.Memory.Waits; _window.Restart();
            if (before != Workers) owner.StatusChanged?.Invoke($"Auto: using {Workers} of {Environment.ProcessorCount} workers ({phase.ToLowerInvariant()})");
        }
        public async Task FinishAsync(CancellationToken token)
        {
            if (settings.ScanWorkers != 0 || _tuner.AcceptedWorkers == count) return;
            await owner.Store.UpdateAsync(state =>
            {
                if (!SameOutput(state.Settings, settings) || !Matches(state.Settings.AutoScanProfile, settings, Environment.ProcessorCount)) return false;
                if (phase == "Archive") state.Settings.AutoScanProfile!.ArchiveWorkers = _tuner.AcceptedWorkers;
                else state.Settings.AutoScanProfile!.IncomingWorkers = _tuner.AcceptedWorkers;
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Scan,
                    Level = ActivityLevel.Information,
                    Message = $"Auto {phase.ToLowerInvariant()} adjusted to {_tuner.AcceptedWorkers} workers from measured throughput/responsiveness."
                });
                return true;
            }, token).ConfigureAwait(false);
        }
    }
}

internal sealed class WorkerTuner(int initial, int maximum)
{
    public int Workers { get; private set; } = Math.Clamp(initial, 1, Math.Max(1, maximum));
    public int AcceptedWorkers { get; private set; } = Math.Clamp(initial, 1, Math.Max(1, maximum));
    private double _baseline;
    private bool _trial;
    private int _badWindows;
    private int _cooldown;
    public void Observe(double rate, bool errors, bool uiSlow, bool memoryLimited, bool backlog)
    {
        if (errors || uiSlow)
        {
            Workers = AcceptedWorkers = Math.Max(1, AcceptedWorkers / 2);
            _trial = false; _baseline = 0; _cooldown = 2; return;
        }
        if (!backlog || !double.IsFinite(rate) || rate <= 0) return;
        if (_trial)
        {
            if (rate >= _baseline * 1.05) AcceptedWorkers = Workers;
            else Workers = AcceptedWorkers;
            _trial = false; _baseline = rate; _cooldown = 2; return;
        }
        if (_baseline > 0 && rate < _baseline * .9) _badWindows++; else _badWindows = 0;
        if (_badWindows >= 2)
        {
            Workers = AcceptedWorkers = Math.Max(1, AcceptedWorkers / 2); _badWindows = 0; _cooldown = 2;
        }
        if (_baseline == 0 || _badWindows == 0) _baseline = rate;
        if (_cooldown > 0) { _cooldown--; return; }
        if (!memoryLimited && Workers < maximum)
        {
            Workers = Math.Min(maximum, Workers * 2); _trial = true;
        }
    }
}
