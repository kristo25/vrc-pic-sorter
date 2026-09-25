using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.Scanning;

/// <summary>
/// The outcome of an archive index build.
/// <para><see cref="Errors"/> reports folder-level failures that make the index unusable.</para>
/// <para><see cref="SkippedFiles"/> reports individual files that could not be decoded. Skipped
/// files make coverage incomplete, so the category cannot safely route incoming images.</para>
/// </summary>
public sealed record ArchiveIndexResult(
    VrcImageCategory Category,
    IndexStatus Status,
    long Generation,
    int IndexedFiles,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> SkippedFiles);

public sealed class ArchiveIndexer
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".gif", ".jpg", ".jpeg", ".webp", ".bmp",
        };

    private readonly JsonStateStore _stateStore;
    private readonly ImageDecoder _decoder;
    private readonly TimeProvider _timeProvider;
    public ScanConcurrencyController Concurrency { get; }

    public ArchiveIndexer(
        JsonStateStore stateStore,
        ImageDecoder decoder,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Concurrency = new ScanConcurrencyController(stateStore, decoder);
    }

    public Task<ArchiveIndexResult> RefreshAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        BuildAsync(category, reuseUnchanged: true, attempt: 0, cancellationToken);

    public Task<ArchiveIndexResult> RebuildAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        BuildAsync(category, reuseUnchanged: false, attempt: 0, cancellationToken);

    private async Task<ArchiveIndexResult> BuildAsync(
        VrcImageCategory category,
        bool reuseUnchanged,
        int attempt,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mapping = state.Settings.CategoryMappings.Single(item => item.Category == category);
        var previousIndex = state.ArchiveIndex.Categories.Single(item => item.Category == category);
        var previousRecords = previousIndex.Images
            .Where(item => item.Fingerprint is not null)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var archiveRoots = state.Settings.LegacyArchiveMappings
            .Where(item => item.Category == category)
            .Select(item => item.ArchivePath)
            .Prepend(mapping.ArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archiveRoots.Length == 0)
        {
            await PublishUnavailableAsync(category, "Archive folder is unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return new ArchiveIndexResult(
                category,
                IndexStatus.Unavailable,
                0,
                0,
                ["Archive folder is unavailable."],
                []);
        }

        if (!reuseUnchanged)
        {
            await SetStatusAsync(category, IndexStatus.Building, null, cancellationToken).ConfigureAwait(false);
        }

        var skipped = new List<string>();
        var indexed = new List<IndexedImageRecord>();
        IEnumerable<string> paths;
        // Everything under an archive root is indexed, animations included. They were left out
        // once, on the grounds that a GIF would become a duplicate candidate of the sheet it came
        // from - but it does not: a sheet is a grid of every frame at full size and the animation
        // is one frame playing, so they resemble each other about as much as a contact sheet
        // resembles a film. Leaving them out cost far more than it saved, because a ready-made GIF
        // arriving in an incoming folder then had nothing to be compared against and was archived
        // again every time.
        try
        {
            paths = archiveRoots.SelectMany(PathBoundary.EnumerateFilesWithoutReparsePoints)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await PublishUnavailableAsync(category, exception.Message, cancellationToken).ConfigureAwait(false);
            return new ArchiveIndexResult(
                category,
                IndexStatus.Unavailable,
                0,
                0,
                [exception.Message],
                []);
        }

        await Concurrency.PrepareAsync(state.Settings, paths, null, cancellationToken).ConfigureAwait(false);
        var session = await Concurrency.StartAsync("Archive", cancellationToken).ConfigureAwait(false);
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Queue<Task<(IndexedImageRecord? Record, string? Error)>>();
        using var iterator = paths.GetEnumerator();
        var exhausted = false;
        async Task<(IndexedImageRecord? Record, string? Error)> PrepareRecord(string path)
        {
            try
            {
                readers.Token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                var length = info.Length; var written = info.LastWriteTimeUtc;
                if (reuseUnchanged && previousRecords.TryGetValue(path, out var previous)
                    && previous.Fingerprint!.HasCurrentFeatures && previous.FileSize == length && previous.LastWriteUtc == written)
                    return (previous, null);
                var fingerprint = await Concurrency.ReadAsync(path, readers.Token).ConfigureAwait(false);
                var after = new FileInfo(path);
                if (length != after.Length || written != after.LastWriteTimeUtc)
                    throw new IOException("Image changed during indexing; retry required.");
                previousRecords.TryGetValue(path, out var priorRecord);
                return (new IndexedImageRecord
                {
                    Id = priorRecord?.Id ?? Guid.NewGuid(),
                    Category = category,
                    Path = path,
                    FileSize = length,
                    LastWriteUtc = written,
                    Width = fingerprint.Width,
                    Height = fingerprint.Height,
                    ExactFingerprint = fingerprint.ExactIdentity,
                    PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                    Fingerprint = fingerprint,
                }, null);
            }
            catch (Exception error) when (ScanConcurrencyController.IsReadFailure(error))
            {
                return (null, $"{path}: {error.Message}");
            }
        }
        try
        {
            while (!exhausted || pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!exhausted && pending.Count < session.Workers)
                {
                    exhausted = !iterator.MoveNext();
                    if (!exhausted) { var path = iterator.Current; pending.Enqueue(Task.Run(() => PrepareRecord(path))); }
                }
                if (pending.Count == 0) break;
                var prepared = await pending.Dequeue().ConfigureAwait(false);
                session.Completed(prepared.Error is null, !exhausted);
                if (prepared.Record is not null) indexed.Add(prepared.Record);
                else skipped.Add(prepared.Error!);
            }
        }
        finally { readers.Cancel(); await Task.WhenAll(pending).ConfigureAwait(false); }
        await session.FinishAsync(cancellationToken).ConfigureAwait(false);

        if (skipped.Count > 0)
        {
            var error = "Archive coverage is incomplete. Incoming files were left unchanged. "
                + string.Join(Environment.NewLine, skipped.Take(10));
            await PublishUnavailableAsync(category, error, cancellationToken).ConfigureAwait(false);
            return new ArchiveIndexResult(category, IndexStatus.Unavailable, previousIndex.Generation,
                previousIndex.Images.Count, [error], skipped);
        }

        if (reuseUnchanged
            && previousIndex.Status == IndexStatus.Current
            && HaveSameFileSet(previousIndex.Images, indexed))
        {
            var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var currentIndex = current.ArchiveIndex.Categories.Single(item => item.Category == category);
            if (currentIndex.Generation != previousIndex.Generation)
            {
                if (attempt < 3)
                {
                    return await BuildAsync(category, reuseUnchanged, attempt + 1, cancellationToken).ConfigureAwait(false);
                }

                throw new ArchiveIndexChangedException();
            }

            return new ArchiveIndexResult(
                category,
                IndexStatus.Current,
                previousIndex.Generation,
                indexed.Count,
                [],
                skipped);
        }

        ArchiveIndexResult result;
        try
        {
            result = await _stateStore.UpdateAsync(
                current =>
                {
                    var index = current.ArchiveIndex.Categories.Single(item => item.Category == category);
                    if (index.Generation != previousIndex.Generation)
                    {
                        throw new ArchiveIndexChangedException();
                    }

                    var changed = !HaveSameFileSet(index.Images, indexed);
                    if (!reuseUnchanged || changed)
                    {
                        index.Generation++;
                    }

                    index.Images = indexed;
                    current.Settings.CategoryMappings.Single(item => item.Category == category).ArchivePathKnownMissing = false;
                    index.Status = IndexStatus.Current;
                    index.LastCompletedUtc = _timeProvider.GetUtcNow();
                    index.LastError = null;
                    if (!reuseUnchanged || changed)
                    {
                        current.History.Add(new ActivityEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredUtc = _timeProvider.GetUtcNow(),
                            Kind = ActivityKind.Scan,
                            Level = ActivityLevel.Information,
                            Category = category,
                            Message = $"Indexed {indexed.Count} archive images.",
                        });
                    }

                    return new ArchiveIndexResult(
                        category,
                        index.Status,
                        index.Generation,
                        indexed.Count,
                        [],
                        skipped);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveIndexChangedException) when (attempt < 3)
        {
            return await BuildAsync(category, reuseUnchanged, attempt + 1, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static bool HaveSameFileSet(
        IReadOnlyCollection<IndexedImageRecord> left,
        IReadOnlyCollection<IndexedImageRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightByPath = right.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        return left.All(
            item => rightByPath.TryGetValue(item.Path, out var other)
                && item.Id == other.Id
                && item.FileSize == other.FileSize
                && item.LastWriteUtc == other.LastWriteUtc
                && item.Fingerprint is not null
                && other.Fingerprint is not null
                && item.Fingerprint.HasCurrentFeatures
                && other.Fingerprint.HasCurrentFeatures);
    }

    public Task MarkStaleAsync(
        VrcImageCategory category,
        string reason,
        CancellationToken cancellationToken = default) =>
        SetStatusAsync(category, IndexStatus.Stale, reason, cancellationToken);

    private Task SetStatusAsync(
        VrcImageCategory category,
        IndexStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == category);
                index.Status = status;
                index.LastError = error;
                return true;
            },
            cancellationToken);

    private Task PublishUnavailableAsync(
        VrcImageCategory category,
        string error,
        CancellationToken cancellationToken) =>
        SetStatusAsync(category, IndexStatus.Unavailable, error, cancellationToken);

    private sealed class ArchiveIndexChangedException : InvalidOperationException;
}
