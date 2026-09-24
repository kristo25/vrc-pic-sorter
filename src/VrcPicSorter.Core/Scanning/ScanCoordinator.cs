using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.Scanning;

public sealed record CategoryScanResult(
    VrcImageCategory Category,
    int Examined,
    int MovedUnique,
    int HeldForReview,
    int Skipped,
    IReadOnlyList<string> Errors,
    int AutoKeptArchived = 0,
    int Animated = 0,
    int ArchiveDuplicatesRemoved = 0);

/// <summary><see cref="Activity"/> and <see cref="FileName"/> describe what the scan is doing
/// right now, so the UI can say more than a bare count.</summary>
public sealed record ScanProgress(
    VrcImageCategory Category,
    int ScannedImages,
    int TotalImages,
    string Activity = "",
    string? FileName = null);

public sealed record ScanProcessingProgress(
    int ProcessedImages,
    int TotalImages,
    string Activity = "",
    string? FileName = null);

/// <summary>An animation a person exported by hand, waiting for the rest of what a scan does.</summary>
/// <param name="SheetIndexedImageId">The index record of the sheet it was made from.</param>
/// <param name="Category">The archive the sheet belongs to.</param>
/// <param name="AtlasPath">Where that sheet is now.</param>
/// <param name="AnimationPath">The file the export just wrote.</param>
public sealed record ExportedAnimation(
    Guid SheetIndexedImageId,
    VrcImageCategory Category,
    string AtlasPath,
    string AnimationPath);

/// <summary>What became of the animations, once the archive had been told about them.</summary>
/// <param name="Filed">Sheets that were filed beside their animation, by the path they moved to.</param>
/// <param name="Duplicates">
/// Each export that turned out to be a picture the archive already held, and the copies it joins.
/// Exact extra copies are recycled after verification; distinct animation variants remain reported.
/// </param>
/// <param name="Warnings">Anything that could not be finished, in the words of the failure.</param>
public sealed record ExportedAnimationFollowUp(
    IReadOnlyList<string> Filed,
    IReadOnlyList<ArchivedAnimationDuplicate> Duplicates,
    IReadOnlyList<string> Warnings,
    int ArchiveDuplicatesRemoved = 0);

/// <param name="AnimationPath">The animation just exported.</param>
/// <param name="ExistingCopies">Archived files that decode to the very same picture.</param>
public sealed record ArchivedAnimationDuplicate(
    string AnimationPath,
    IReadOnlyList<string> ExistingCopies);

public sealed partial class ScanCoordinator
{
    /// <summary>
    /// How many images may be read and fingerprinted at once. Reading is pure - it touches no
    /// state and no file is moved - so it is the only part of a scan that can safely run ahead.
    /// Every decision still happens one image at a time, in path order.
    /// </summary>
    public const int MaximumConcurrentReads = 5;

    /// <summary>
    /// An encoded size above which an image is read on its own. A decoded image is allowed to
    /// reach <see cref="ImageResourceLimits.MaximumDecodedBytes"/>, so reading several large ones
    /// together could multiply that; large files are rare enough that serialising them is free.
    /// </summary>
    private const long LargeEncodedBytes = 16L * 1024 * 1024;

    /// <summary>
    /// How many animations one scan will add to an archive that already has some before it treats
    /// the run as a fault rather than a workload. Set well above any plausible batch of new emoji
    /// and far below the size of an archive worth protecting.
    /// </summary>
    private const int MaximumUnrecognisedAnimations = 25;

    private sealed record PreparedImage(ImageFingerprint? Fingerprint, string? Error);

    private readonly JsonStateStore _stateStore;
    private readonly ArchiveIndexer _indexer;
    private readonly ImageDecoder _decoder;
    private readonly FileRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _settleDelay;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>Runs a settings application or relocation between scanner operations.
    /// The callback must not call another scanner entry point or stop the watcher.</summary>
    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }
    private readonly AtlasAnimationWriter _animationWriter = new();

    public ScanCoordinator(
        JsonStateStore stateStore,
        ArchiveIndexer indexer,
        ImageDecoder decoder,
        FileRouter router,
        TimeSpan? settleDelay = null,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _settleDelay = settleDelay ?? TimeSpan.Zero;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(CancellationToken cancellationToken = default) =>
        ScanAllAsync(progress: null, processingProgress: null, cancellationToken);

    public Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default) =>
        ScanAllAsync(progress, processingProgress: null, cancellationToken);

    public async Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(
        IProgress<ScanProgress>? progress,
        IProgress<ScanProcessingProgress>? processingProgress,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var mappings = state.Settings.CategoryMappings.Where(item => item.IsEnabled).ToArray();
            if (mappings.Length == 0)
            {
                return [];
            }

            var snapshots = mappings.ToDictionary(
                mapping => mapping.Category,
                mapping => state.Settings.OutputRootConfirmed && Directory.Exists(mapping.SourcePath)
                    ? CreateSourceSnapshotSafe(mapping.SourcePath)
                    : SourceSnapshot.Empty);
            var totalImages = snapshots.Values.Sum(snapshot => snapshot.Paths.Count);
            var scannedImages = 0;
            var processedImages = 0;
            progress?.Report(new ScanProgress(mappings[0].Category, 0, totalImages));
            processingProgress?.Report(new ScanProcessingProgress(0, totalImages));
            var results = new List<CategoryScanResult>(mappings.Length);
            foreach (var mapping in mappings)
            {
                try
                {
                    results.Add(await ScanCategoryCoreAsync(
                            mapping.Category,
                            mapping.SourcePath,
                            requireEnabled: true,
                            snapshots[mapping.Category],
                            onImageScanned: (activity, file) =>
                            {
                                scannedImages++;
                                progress?.Report(new ScanProgress(
                                    mapping.Category,
                                    scannedImages,
                                    totalImages,
                                    activity,
                                    file));
                            },
                            onImageProcessed: (activity, file) =>
                            {
                                processedImages++;
                                processingProgress?.Report(
                                    new ScanProcessingProgress(processedImages, totalImages, activity, file));
                            },
                            cancellationToken,
                            reconcileAllPending: true)
                        .ConfigureAwait(false));
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or NotSupportedException
                        or ArgumentException)
                {
                    results.Add(new CategoryScanResult(mapping.Category, 0, 0, 0, 0, [exception.Message]));
                }
            }

            return results;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public Task<CategoryScanResult> ScanCategoryAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        ScanCategoryCoreWithLockAsync(category, cancellationToken);

    /// <summary>
    /// Scans only the given incoming paths, ignoring everything else in the source folder.
    /// Folder watching uses this so a newly added image costs one decode instead of a full
    /// sweep. Paths outside the category's configured source folder are never processed.
    /// </summary>
    public async Task<CategoryScanResult> ScanIncomingPathsAsync(
        VrcImageCategory category,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sourceRoot = state.Settings.CategoryMappings
                .Single(item => item.Category == category)
                .SourcePath;
            var snapshot = CreateSnapshotFromPaths(sourceRoot, paths);
            if (snapshot.Error is null && snapshot.Paths.Count == 0)
            {
                return new CategoryScanResult(category, 0, 0, 0, snapshot.UnsupportedFiles, []);
            }

            return await ScanCategoryCoreAsync(
                    category,
                    sourceRoot,
                    requireEnabled: true,
                    snapshot,
                    onImageScanned: null,
                    onImageProcessed: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<CategoryScanResult> ScanCategoryCoreWithLockAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sourceRoot = state.Settings.CategoryMappings.Single(item => item.Category == category).SourcePath;
            return await ScanCategoryCoreAsync(
                    category,
                    sourceRoot,
                    requireEnabled: true,
                    sourceSnapshot: null,
                    onImageScanned: null,
                    onImageProcessed: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        ScanFolderAsync(sourceRoot, category, progress: null, processingProgress: null, cancellationToken);

    public Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default) =>
        ScanFolderAsync(sourceRoot, category, progress, processingProgress: null, cancellationToken);

    public async Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        IProgress<ScanProgress>? progress,
        IProgress<ScanProcessingProgress>? processingProgress,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = PathBoundary.Normalize(sourceRoot);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            // Each candidate carries what it is as well as where it is. Naming the category of
            // folder alone left a person with three things to check and no way to tell which one
            // they had hit - most often the output folder, whose own archive this would re-ingest.
            var disallowed = state.Settings.CategoryMappings
                .Where(mapping => mapping.IsEnabled)
                .Select(mapping => (Description: $"the {mapping.Category} source folder", Path: mapping.SourcePath))
                .Append((Description: "the output folder", Path: state.Settings.OutputRootPath))
                .Append((Description: "the application holding folder", Path: state.Settings.HoldingRootPath))
                .Concat(state.Settings.LegacyArchiveMappings.Select(
                    mapping => (Description: $"the retained {mapping.Category} archive folder", Path: mapping.ArchivePath)))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
                .ToArray();

            var clash = Array.Find(disallowed, candidate => PathBoundary.Overlaps(normalizedSource, candidate.Path));
            if (clash.Path is not null)
            {
                // Being handed back the same path one has just chosen reads as a non-answer, so
                // the identical case says what that folder already is instead.
                throw new InvalidOperationException(
                    string.Equals(PathBoundary.Normalize(clash.Path), normalizedSource, StringComparison.OrdinalIgnoreCase)
                        ? $"That folder is already {clash.Description}, so it cannot also be scanned as a source."
                        : $"That folder cannot be scanned because it overlaps {clash.Description}:{Environment.NewLine}{clash.Path}");
            }

            var snapshot = state.Settings.OutputRootConfirmed
                ? CreateSourceSnapshotSafe(normalizedSource)
                : SourceSnapshot.Empty;
            var totalImages = snapshot.Paths.Count;
            var scannedImages = 0;
            var processedImages = 0;
            progress?.Report(new ScanProgress(category, scannedImages, totalImages));
            processingProgress?.Report(new ScanProcessingProgress(processedImages, totalImages));
            return await ScanCategoryCoreAsync(
                    category,
                    normalizedSource,
                    requireEnabled: false,
                    snapshot,
                    onImageScanned: (activity, file) =>
                    {
                        scannedImages++;
                        progress?.Report(new ScanProgress(
                            category,
                            scannedImages,
                            totalImages,
                            activity,
                            file));
                    },
                    onImageProcessed: (activity, file) =>
                    {
                        processedImages++;
                        processingProgress?.Report(
                            new ScanProcessingProgress(processedImages, totalImages, activity, file));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Holds fingerprint sidecar writes for the length of the scan. Every archived image adds one
    /// fingerprint and rewrites all of them, so a scan of an already large archive spends most of
    /// its time writing the same data over and over. Holding turns that into one write.
    /// </summary>
    private async Task<CategoryScanResult> ScanCategoryCoreAsync(
        VrcImageCategory category,
        string sourceRoot,
        bool requireEnabled,
        SourceSnapshot? sourceSnapshot,
        Action<string, string?>? onImageScanned,
        Action<string, string?>? onImageProcessed,
        CancellationToken cancellationToken,
        bool reconcileAllPending = false)
    {
        await _stateStore.HoldFingerprintWritesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCategoryUnheldAsync(
                    category,
                    sourceRoot,
                    requireEnabled,
                    sourceSnapshot,
                    onImageScanned,
                    onImageProcessed,
                    cancellationToken,
                    reconcileAllPending)
                .ConfigureAwait(false);
        }
        finally
        {
            // Not cancellable: a stopped scan still archived files, and their fingerprints belong
            // on disk so the next scan does not rebuild the whole index.
            await _stateStore.ReleaseFingerprintWritesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<CategoryScanResult> ScanCategoryUnheldAsync(
        VrcImageCategory category,
        string sourceRoot,
        bool requireEnabled,
        SourceSnapshot? sourceSnapshot,
        Action<string, string?>? onImageScanned,
        Action<string, string?>? onImageProcessed,
        CancellationToken cancellationToken,
        bool reconcileAllPending)
    {
        var initial = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!initial.Settings.OutputRootConfirmed)
        {
            return new CategoryScanResult(
                category,
                0,
                0,
                0,
                0,
                ["Confirm the migrated main output folder in Settings before scanning."]);
        }

        var mapping = initial.Settings.CategoryMappings.Single(item => item.Category == category);
        if ((requireEnabled && !mapping.IsEnabled) || !Directory.Exists(sourceRoot))
        {
            return new CategoryScanResult(category, 0, 0, 0, 0, ["Source folder is unavailable or disabled."]);
        }

        sourceSnapshot ??= CreateSourceSnapshotSafe(sourceRoot);
        if (sourceSnapshot.Error is not null)
        {
            return new CategoryScanResult(category, 0, 0, 0, 0, [sourceSnapshot.Error]);
        }

        // The output folder is where this scan puts files, so create it on demand instead of
        // failing. Source folders are never created: an empty one would have nothing to scan.
        // This runs after the snapshot so a failure can still complete both progress streams.
        try
        {
            Directory.CreateDirectory(mapping.ArchivePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportUnprocessed(
                sourceSnapshot.Paths.Count,
                "Output folder unavailable",
                onImageScanned,
                onImageProcessed);
            return new CategoryScanResult(
                category,
                0,
                0,
                0,
                0,
                [$"The {category} output folder could not be created: {exception.Message}"]);
        }

        var indexResult = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
        if (indexResult.Status != IndexStatus.Current)
        {
            ReportUnprocessed(
                sourceSnapshot.Paths.Count,
                "Archive index unavailable",
                onImageScanned,
                onImageProcessed);
            return new CategoryScanResult(category, 0, 0, 0, 0, indexResult.Errors);
        }

        var duplicateCleanup = await CleanIndexedArchiveDuplicatesCoreAsync([category], cancellationToken)
            .ConfigureAwait(false);

        // Individual unreadable archive files are surfaced as scan warnings, not as a
        // reason to abort the category.
        var errors = new List<string>(
            indexResult.SkippedFiles.Select(skipped => $"Archive file skipped - {skipped}"));
        errors.AddRange(duplicateCleanup.Warnings);

        // A sheet whose pixels disagree with its name is worth saying out loud, but it is not a
        // failure: the export did exactly what it was asked to. Kept apart from the errors so it
        // lands in the history as information rather than raising "scan completed with warnings"
        // over a sheet that animated perfectly well.
        var notes = new List<string>();
        var examined = 0;
        var moved = 0;
        var held = 0;
        var autoKept = 0;
        var skipped = 0;

        // Sheets already in the archive were archived before this existed, so animating only what
        // a scan newly archives would leave the whole existing archive untouched. This fills in
        // whatever is missing, which also means a deleted animation comes back on the next scan.
        // A sheet whose pixels contradict its name is left for the Animations tab rather than
        // reported here, or it would warn on every scan forever.
        var backfill = await AnimateArchivedSheetsAsync(
                category,
                mapping.ArchivePath,
                errors,
                notes,
                cancellationToken)
            .ConfigureAwait(false);
        var animated = backfill.Written;

        // Those animations are archived images, and they were written after the index was built,
        // so the index does not know about them. Left that way they are invisible to matching for
        // the rest of this scan, and an incoming copy of one of them is archived again as though
        // the app had never made it. Only the new files are decoded here; everything else is reused.
        // Filing a sheet moves it, so the index is out of date after that too - not only after an
        // animation is written.
        if (backfill.ChangedTheArchive)
        {
            var reindexed = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
            if (reindexed.Status != IndexStatus.Current)
            {
                ReportUnprocessed(
                    sourceSnapshot.Paths.Count,
                    "Archive index unavailable",
                    onImageScanned,
                    onImageProcessed);
                return new CategoryScanResult(category, 0, 0, 0, 0, [.. errors, .. reindexed.Errors]);
            }
        }

        var resolvedQueuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedPaths = sourceSnapshot.Paths.Select(PathBoundary.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refreshed = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var archivedImages = refreshed.ArchiveIndex.Categories.Single(item => item.Category == category).Images;
        // Archive relocation/retirement can invalidate every match. Let files included in this
        // scan go through fresh matching instead of being skipped forever as already queued.
        // Never release a partially applied decision, a legacy held file, or an unrelated path.
        foreach (var waiting in refreshed.ReviewQueue.Where(item =>
                     item.Category == category && item.Status != ReviewStatus.Resolved
                     && item.Candidates.Count == 0 && item.IsIncomingInPlace
                     && string.IsNullOrWhiteSpace(item.KeptIncomingArchivedPath)
                     && requestedPaths.Contains(PathBoundary.Normalize(item.IncomingOriginalPath))
                     && !refreshed.OperationJournal.Any(entry => entry.Phase != JournalPhase.Completed
                         && (entry.ReviewItemId == item.Id
                             || string.Equals(entry.SourcePath, item.HeldFilePath, StringComparison.OrdinalIgnoreCase)))))
        {
            await _router.DismissReviewAsync(waiting.Id, cancellationToken).ConfigureAwait(false);
        }
        foreach (var waiting in refreshed.ReviewQueue.Where(item =>
                     item.Category == category && item.Status == ReviewStatus.Pending
                     && (reconcileAllPending || requestedPaths.Contains(PathBoundary.Normalize(item.IncomingOriginalPath)))
                     && item.IncomingImageFingerprint is not null
                     && item.Candidates.Any(candidate => candidate.MatchKind == MatchKind.Exact)))
        {
            var duplicate = archivedImages.FirstOrDefault(image =>
                image.ExactFingerprint == waiting.IncomingFingerprint);
            if (duplicate is null)
            {
                continue;
            }

            try
            {
                await _router.AutoKeepArchivedAsync(
                    waiting.HeldFilePath, category, waiting.IncomingImageFingerprint!,
                    duplicate.Path, duplicate.ExactFingerprint, cancellationToken, waiting.Id).ConfigureAwait(false);
                resolvedQueuedPaths.Add(waiting.IncomingOriginalPath);
                autoKept++;
                if (sourceSnapshot.Paths.Contains(waiting.IncomingOriginalPath, StringComparer.OrdinalIgnoreCase))
                {
                    examined++;
                    var name = Path.GetFileName(waiting.IncomingOriginalPath);
                    onImageScanned?.Invoke("Resolved queued exact match", name);
                    onImageProcessed?.Invoke("Resolved queued exact match", name);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                 or InvalidOperationException or NotSupportedException)
            {
                errors.Add($"{waiting.HeldFilePath}: could not resolve queued exact match ({exception.Message}).");
            }
        }

        initial = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var paths = sourceSnapshot.Paths.Where(path => !resolvedQueuedPaths.Contains(path)).ToList();
        skipped = sourceSnapshot.UnsupportedFiles;
        var settledPaths = await FindSettledPathsAsync(paths, cancellationToken).ConfigureAwait(false);
        var queuedPaths = initial.ReviewQueue
            .Where(item => item.Status != ReviewStatus.Resolved)
            .Select(item => item.IncomingOriginalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An image held for review is not in the archive, so the index below never proposes it and
        // nothing compared a later file against it. Two copies of one picture therefore each
        // opened their own review card. Everything already waiting for this category counts here,
        // not only what this run queues.
        var heldByIdentity = new Dictionary<string, HeldImage>(StringComparer.Ordinal);
        foreach (var waiting in initial.ReviewQueue)
        {
            if (waiting.Status == ReviewStatus.Resolved
                || waiting.Category != category
                || waiting.IncomingImageFingerprint is null)
            {
                continue;
            }

            heldByIdentity.TryAdd(
                waiting.IncomingImageFingerprint.ExactIdentity,
                new HeldImage(waiting.HeldFilePath, waiting.IncomingImageFingerprint));
        }

        // Rebuilding the candidate list and the key lookup for every incoming image costs a pass
        // over the whole archive each time. The generation moves whenever an image is added to or
        // removed from the index, so keying on it rebuilds exactly when the archive changed - and
        // that includes an image archived earlier in this same scan.
        var candidateGeneration = -1L;
        var indexedCandidates = Array.Empty<ImageCandidate>();
        var indexedByKey = new Dictionary<string, IndexedImageRecord>(StringComparer.Ordinal);

        // Reading runs ahead of routing by up to MaximumConcurrentReads images. Reads produce a
        // fingerprint and nothing else, so running them early cannot change what any decision
        // sees; the loop below still consumes them strictly in path order.
        var readAhead = Math.Clamp(Environment.ProcessorCount, 1, MaximumConcurrentReads);
        using var readSlots = new SemaphoreSlim(readAhead, readAhead);
        using var largeReadSlot = new SemaphoreSlim(1, 1);
        var inFlight = new Dictionary<string, Task<PreparedImage>>(StringComparer.OrdinalIgnoreCase);
        var nextToRead = 0;

        void StartReadsAhead()
        {
            while (inFlight.Count < readAhead && nextToRead < paths.Count)
            {
                var upcoming = paths[nextToRead++];
                if (queuedPaths.Contains(upcoming) || !settledPaths.Contains(upcoming))
                {
                    continue;
                }

                inFlight[upcoming] = PrepareImageAsync(upcoming, readSlots, largeReadSlot, cancellationToken);
            }
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            var fileName = Path.GetFileName(path);
            var readingReported = false;
            var outcome = "Skipped";
            try
            {
                if (queuedPaths.Contains(path))
                {
                    skipped++;
                    outcome = "Already in the review queue";
                    continue;
                }

                if (!settledPaths.Contains(path))
                {
                    errors.Add($"{path}: file is still being written or locked.");
                    outcome = "Still being written";
                    continue;
                }

                StartReadsAhead();
                if (!inFlight.Remove(path, out var read))
                {
                    read = PrepareImageAsync(path, readSlots, largeReadSlot, cancellationToken);
                }

                var prepared = await read.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                onImageScanned?.Invoke("Reading", fileName);
                readingReported = true;
                if (prepared.Fingerprint is null)
                {
                    errors.Add($"{path}: {prepared.Error}");
                    outcome = "Could not be read";
                    continue;
                }

                var fingerprint = prepared.Fingerprint;

                // The same picture saved twice under different names matches the archive the same
                // way twice, and one review card per copy is one decision too many. The copy
                // already held keeps the decision; this one decodes to the very same pixels, so
                // whatever is decided there decides this too, and the Recycle Bin is what that
                // means for a copy nobody is going to keep.
                if (heldByIdentity.TryGetValue(fingerprint.ExactIdentity, out var twin))
                {
                    try
                    {
                        await _router.AutoKeepHeldAsync(
                                path,
                                category,
                                fingerprint,
                                twin.Path,
                                twin.Fingerprint.ExactIdentity,
                                cancellationToken)
                            .ConfigureAwait(false);
                        autoKept++;
                        outcome = "Same picture already in review; recycled";
                        continue;
                    }
                    catch (Exception exception) when (
                        exception is NotSupportedException
                            or InvalidOperationException
                            or IOException
                            or UnauthorizedAccessException)
                    {
                        // The Recycle Bin was unavailable, or the held copy changed since it was
                        // scanned. Never delete and never drop the image: fall through and let
                        // this copy get a review of its own.
                        errors.Add($"{path}: could not resolve automatically ({exception.Message}).");
                    }
                }

                var relativeDirectory = Path.GetRelativePath(
                    sourceRoot,
                    Path.GetDirectoryName(path) ?? sourceRoot);
                var routingContext = new ScanRoutingContext
                {
                    SourceRootPath = sourceRoot,
                    RelativeDirectory = relativeDirectory == "." ? string.Empty : relativeDirectory,
                    OutputRootPath = initial.Settings.OutputRootPath,
                };
                var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = current.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (index.Status != IndexStatus.Current)
                {
                    errors.Add($"{path}: archive index became stale; rescan required.");
                    outcome = "Archive index went stale";
                    continue;
                }

                // Rebuilt only when the index has actually moved on. Reading every archived
                // fingerprint back for every incoming image is what made a large archive crawl.
                if (candidateGeneration != index.Generation)
                {
                    indexedCandidates = index.Images
                        .Where(item => item.Fingerprint is not null)
                        .Select(item => new ImageCandidate(item.Id.ToString("N"), item.Fingerprint!))
                        .ToArray();
                    indexedByKey = index.Images.ToDictionary(
                        item => item.Id.ToString("N"),
                        item => item,
                        StringComparer.Ordinal);
                    candidateGeneration = index.Generation;
                }

                // Everything this incoming image may be compared against. Normally that is the
                // index alone, and the cached view above is used exactly as it stands. A ready-made
                // GIF is also compared against the animation the archive's own sheet produces -
                // generated now if it has never been made - and that companion belongs to this one
                // file, so it goes into a copy rather than into the cache every other image reads.
                var comparable = indexedByKey;
                var candidates = indexedCandidates;
                if (AtlasAnimationWriter.IsAnimation(path))
                {
                    var companion = await BuildCompanionAnimationAsync(
                            index,
                            path,
                            mapping.ArchivePath,
                            errors,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (companion is not null)
                    {
                        var companionKey = companion.Id.ToString("N");
                        comparable = new Dictionary<string, IndexedImageRecord>(
                            indexedByKey,
                            StringComparer.Ordinal)
                        {
                            [companionKey] = companion,
                        };
                        if (companion.Fingerprint is not null)
                        {
                            candidates =
                                [.. indexedCandidates, new ImageCandidate(companionKey, companion.Fingerprint)];
                        }
                    }
                }

                var thresholds = current.Settings.CustomSimilarityThresholds;
                thresholds?.Validate();
                var matches = ImageMatcher.RankCandidates(
                    fingerprint,
                    candidates,
                    current.Settings.SimilarityProfile,
                    thresholds?.MinimumPercent / 100);

                // VRChat hands over its own ready-made GIF for an emoji this app has already
                // animated. The two are the same animation encoded twice, so they are not the same
                // bytes and their score can land under any threshold - and the copy was then filed
                // as a brand new image, which is where a folder full of "x" beside "x (2)" came
                // from. Being the same emoji, with the same frame count, rate and loop direction in
                // its name, is not a resemblance to be scored: it is VRChat's own metadata about
                // its own inventory item. So it always earns a review card, however the score came
                // out. It never resolves one: discarding a file still needs the decoded content to
                // match exactly, and these do not.
                matches = WithSameEmojiAnimations(matches, comparable, path, fingerprint);

                if (matches.Count > 0)
                {
                    // Exact identity permits the existing permanent-removal fallback. Custom
                    // score-based decisions below use recycling only, never that fallback.
                    IndexedImageRecord? duplicate = null;
                    foreach (var match in matches)
                    {
                        var indexed = comparable[match.CandidateKey];
                        if (indexed.Fingerprint is not null
                            && ImageMatcher.IsSamePicture(match, fingerprint, indexed.Fingerprint))
                        {
                            duplicate = indexed;
                            break;
                        }
                    }

                    if (duplicate is not null)
                    {
                        try
                        {
                            await _router.AutoKeepArchivedAsync(
                                    path,
                                    category,
                                    fingerprint,
                                    duplicate.Path,
                                    duplicate.ExactFingerprint,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            autoKept++;
                            outcome = "Exact duplicate resolved; archived copy kept";
                            continue;
                        }
                        catch (Exception exception) when (
                            exception is NotSupportedException
                                or InvalidOperationException
                                or IOException
                                or UnauthorizedAccessException)
                        {
                            // A copy changed or removal failed. Keep the incoming file in review.
                            errors.Add($"{path}: could not resolve automatically ({exception.Message}).");
                        }
                    }

                    var review = new ReviewItem
                    {
                        Id = Guid.NewGuid(),
                        Category = category,
                        Status = ReviewStatus.Pending,
                        IncomingOriginalPath = path,
                        HeldFilePath = path,
                        IncomingFingerprint = fingerprint.ExactIdentity,
                        IncomingImageFingerprint = fingerprint,
                        IndexGeneration = index.Generation,
                        CreatedUtc = _timeProvider.GetUtcNow(),
                        RoutingContext = routingContext,
                        Candidates = matches.Select(
                                match =>
                                {
                                    var indexed = comparable[match.CandidateKey];
                                    return new ReviewCandidate
                                    {
                                        Id = Guid.NewGuid(),
                                        IndexedImageId = indexed.Id,
                                        ArchivePath = indexed.Path,
                                        ExpectedFingerprint = indexed.ExactFingerprint,
                                        MatchKind = match.MatchKind,
                                        SimilarityScore = match.SimilarityScore,
                                        MatchReasons = match.MatchReasons.ToList(),
                                    };
                                })
                            .ToList(),
                    };
                    await _router.QueueForReviewAsync(review, cancellationToken).ConfigureAwait(false);
                    var best = review.Candidates.OrderByDescending(candidate => candidate.SimilarityScore).First();
                    if (duplicate is null && thresholds is not null
                        && best.SimilarityScore >= thresholds.MaximumPercent / 100)
                    {
                        try
                        {
                            // Reuse verified, journaled review routing. Non-identical copies
                            // must remain recoverable even when their score rounds to 100%.
                            await _router.KeepMatchAsync(review, best, cancellationToken).ConfigureAwait(false);
                            autoKept++;
                            outcome = "Similarity limit reached; incoming recycled and archived copy kept";
                            continue;
                        }
                        catch (Exception exception) when (exception is NotSupportedException
                            or InvalidOperationException or IOException or UnauthorizedAccessException)
                        {
                            errors.Add($"{path}: automatic recycling unavailable or failed; check review/recovery ({exception.Message}).");
                        }
                    }
                    queuedPaths.Add(path);
                    heldByIdentity.TryAdd(fingerprint.ExactIdentity, new HeldImage(path, fingerprint));
                    held++;
                    outcome = "Queued for review";
                    continue;
                }

                var beforeMove = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var freshIndex = beforeMove.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (freshIndex.Status != IndexStatus.Current || freshIndex.Generation != index.Generation)
                {
                    errors.Add($"{path}: index generation changed before routing; retry required.");
                    outcome = "Index changed; retry needed";
                    continue;
                }

                var route = await _router.MoveUniqueAsync(
                        path,
                        category,
                        fingerprint,
                        routingContext,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                moved++;
                outcome = "Archived as unique";

                // VRChat writes the frame count, rate and loop direction into the name of an
                // animated emoji, so a sheet identifies itself and needs no detection. The image
                // is already archived safely by this point, so a failure to animate it is a
                // warning on the scan rather than a failure of the image.
                if (route.DestinationPath is { } archivedPath)
                {
                    // An animation may already be sitting there - VRCX hands over ready-made GIFs
                    // for some emoji, and one that arrived earlier is archived under exactly the
                    // name this sheet's export would take. Writing over it would destroy an
                    // archived file and leave the index describing pixels that no longer exist, so
                    // the existing animation is adopted instead. Re-exporting from the Animations
                    // tab still overwrites, because there a person has asked for it.
                    var animation = await AnimateOrAdoptAsync(
                            archivedPath,
                            mapping.ArchivePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (animation.Exported)
                    {
                        animated++;
                        outcome = "Archived as unique, animated";

                        // Said out loud rather than swallowed. The sheet was animated to its name
                        // and then filed away as finished, so the history is the only place a
                        // person would ever hear what the export made of it.
                        if (animation.Note is { } exportNote)
                        {
                            notes.Add($"{archivedPath}: {exportNote}");
                        }

                        // The sheet has served its purpose as a still, so it is filed with the
                        // animation rather than left among the images a person browses. Only a
                        // sheet that actually produced a GIF moves: one still waiting on review is
                        // unfinished work and stays where it can be seen.
                        if (route.IndexedImageId is { } indexedSheetId)
                        {
                            _ = await FileAnimatedSheetAsync(
                                    indexedSheetId,
                                    archivedPath,
                                    category,
                                    fingerprint,
                                    mapping.ArchivePath,
                                    errors,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else if (animation.Warning is { } warning)
                    {
                        errors.Add($"{archivedPath}: {warning}");
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                errors.Add($"{path}: {exception.Message}");
                outcome = "Failed";
            }
            finally
            {
                if (!readingReported)
                {
                    onImageScanned?.Invoke("Reading", fileName);
                }

                onImageProcessed?.Invoke(outcome, fileName);
            }
        }

        await _stateStore.UpdateAsync(
                state =>
                {
                    foreach (var note in notes)
                    {
                        state.History.Add(new ActivityEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredUtc = _timeProvider.GetUtcNow(),
                            Kind = ActivityKind.Scan,
                            Level = ActivityLevel.Information,
                            Category = category,
                            Message = note,
                            SourcePath = sourceRoot,
                        });
                    }

                    state.History.Add(new ActivityEntry
                    {
                        Id = Guid.NewGuid(),
                        OccurredUtc = _timeProvider.GetUtcNow(),
                        Kind = ActivityKind.Scan,
                        Level = errors.Count == 0 ? ActivityLevel.Information : ActivityLevel.Warning,
                        Category = category,
                        Message = $"Scanned {sourceRoot}: {examined} examined, {moved} moved, {animated} animated, {autoKept} exact duplicates resolved, {held} queued, {skipped} skipped, {errors.Count} failed.",
                        SourcePath = sourceRoot,
                    });
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

        return new CategoryScanResult(
            category,
            examined,
            moved,
            held,
            skipped,
            errors,
            autoKept,
            animated,
            duplicateCleanup.Removed);
    }

    /// <summary>
    /// Makes the animation for a freshly archived sheet, unless one already stands in its place.
    /// </summary>
    /// <remarks>
    /// The existing file is only adopted when it really is this sheet's animation, checked by frame
    /// count. Two different emoji can carry the same file name - the archive root disambiguates
    /// them, but the animation folder is reached by name alone - and adopting a stranger's GIF
    /// would leave this sheet reported as animated while no animation of it exists anywhere.
    /// </remarks>
    private async Task<AtlasAnimationResult> AnimateOrAdoptAsync(
        string archivedPath,
        string archiveRoot,
        CancellationToken cancellationToken)
    {
        if (!EmojiAtlasName.TryParse(archivedPath, out var name))
        {
            return AtlasAnimationResult.NotASheet;
        }

        try
        {
            if (AtlasAnimationWriter.FindExistingAnimation(archivedPath, archiveRoot) is { } destination)
            {
                return await AdoptAsync(destination, name, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Fall through and let the writer report the failure in its own words.
        }

        return await _animationWriter
            .TryWriteAsync(archivedPath, archiveRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the animation already sitting where this sheet's own would go, if it plays the
    /// number of frames this sheet's name promises.
    /// </summary>
    private async Task<AtlasAnimationResult> AdoptAsync(
        string destination,
        EmojiAtlasName name,
        CancellationToken cancellationToken)
    {
        var decoded = await _decoder.DecodeAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess)
        {
            return new AtlasAnimationResult(
                false,
                null,
                $"an animation already sits at {destination} but could not be read, so this sheet was left alone.");
        }

        // Ping-pong plays out and back without repeating either end, so 4 frames play as 6.
        var played = name.LoopStyle == AtlasLoopStyle.PingPong && name.FrameCount > 2
            ? (name.FrameCount * 2) - 2
            : name.FrameCount;
        if (decoded.Image!.Frames.Count != played)
        {
            return new AtlasAnimationResult(
                false,
                null,
                $"a different animation already sits at {destination} - it plays "
                    + $"{decoded.Image!.Frames.Count} frames where this sheet promises {played} - so this "
                    + "sheet was left alone rather than being reported as animated.");
        }

        return new AtlasAnimationResult(true, destination, null);
    }

    /// <summary>
    /// The animation the archive's own sheet produces for an incoming GIF, so the two can be
    /// compared, or null when the archive holds no sheet for it.
    /// </summary>
    /// <remarks>
    /// A ready-made GIF and a sheet are never alike as pixels - one is a frame playing, the other a
    /// grid of every frame - so comparing them directly would always say "different" and archive
    /// both. What can be compared is animation against animation, and the sheet can produce one.
    /// So the sheet's own GIF is made first, if it does not exist yet, and the incoming file is
    /// judged against that by exactly the same rules as any other pair of images: identical means
    /// the archived one wins and the incoming copy is recycled, anything short of identical goes to
    /// review.
    /// The record handed back is not written to the index. The next scan indexes the generated file
    /// properly; this one only needs something to hold the fingerprint while the decision is made.
    /// </remarks>
    private async Task<IndexedImageRecord?> BuildCompanionAnimationAsync(
        CategoryIndexState index,
        string incomingAnimationPath,
        string archiveRoot,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var sheet = index.Images.FirstOrDefault(
            item => AtlasAnimationWriter.IsAnimationOf(incomingAnimationPath, item.Path));
        if (sheet is null)
        {
            return null;
        }

        try
        {
            var ownerState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            archiveRoot = ArchiveOwner.Resolve(ownerState.Settings, index.Category, sheet.Path);
            var companionPath = AtlasAnimationWriter.FindExistingAnimation(sheet.Path, archiveRoot)
                ?? AtlasAnimationWriter.BuildDestination(sheet.Path, archiveRoot);

            // Animations are indexed, so the sheet's own is usually already a candidate. Adding a
            // second record for the same file would put two rows with one path into the review,
            // one of them carrying an id the index has never heard of.
            if (index.Images.Any(item => string.Equals(item.Path, companionPath, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (!File.Exists(companionPath))
            {
                var written = await _animationWriter
                    .TryWriteAsync(sheet.Path, archiveRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!written.Exported)
                {
                    // The sheet cannot be animated, so there is nothing to compare against and the
                    // incoming GIF is the only copy of this animation there is. Let it through.
                    return null;
                }

                companionPath = written.Path ?? companionPath;
            }

            var decoded = await _decoder.DecodeAsync(companionPath, cancellationToken).ConfigureAwait(false);
            if (!decoded.IsSuccess)
            {
                return null;
            }

            var fingerprint = ImageFingerprint.Create(decoded.Image!);
            var info = new FileInfo(companionPath);
            return new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = sheet.Category,
                Path = companionPath,
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Width = decoded.Image!.Width,
                Height = decoded.Image!.Height,
                ExactFingerprint = fingerprint.ExactIdentity,
                PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                Fingerprint = fingerprint,
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException)
        {
            errors.Add($"{incomingAnimationPath}: could not be compared against its sheet ({exception.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Moves a freshly animated sheet into the reference folder beside its animation.
    /// </summary>
    /// <remarks>
    /// A failure here is reported and nothing else: the image is archived, the animation is
    /// written, and the only cost of the sheet staying where it is is that it sits among the
    /// stills. Turning that into a failed scan would be out of proportion to it.
    /// </remarks>
    /// <summary>
    /// Puts animations exported by hand through the rest of what a scan does to one.
    /// </summary>
    /// <remarks>
    /// Writing the GIF was only the first step. A scan then tells the index about the new file,
    /// files the sheet beside it, and knows whether the archive already held that picture; the
    /// Animations tab did none of it, so an export was invisible to deduplication until the next
    /// full index - which is how the archive came to hold the same emoji twice. The same steps run
    /// here, from the same code, so the two paths cannot drift apart again.
    /// <para>
    /// Exact extra copies are verified and sent to the Recycle Bin automatically. Distinct
    /// animations of the same emoji are reported and left for review.
    /// </para>
    /// </remarks>
    public async Task<ExportedAnimationFollowUp> FinishExportedAnimationsAsync(
        IReadOnlyCollection<ExportedAnimation> exported,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FinishExportedAnimationsCoreAsync(exported, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<ExportedAnimationFollowUp> FinishExportedAnimationsCoreAsync(
        IReadOnlyCollection<ExportedAnimation> exported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exported);

        var filed = new List<string>();
        var duplicates = new List<ArchivedAnimationDuplicate>();
        var warnings = new List<string>();
        if (exported.Count == 0)
        {
            return new ExportedAnimationFollowUp(filed, duplicates, warnings);
        }

        // Once per category rather than once per file. A bulk export of fifty sheets would
        // otherwise read the whole archive fifty times over.
        foreach (var category in exported.Select(item => item.Category).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refreshed = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
            if (refreshed.Status != IndexStatus.Current)
            {
                warnings.Add(
                    $"{category}: the new animation could not be added to the archive index "
                        + $"({string.Join("; ", refreshed.Errors)}). Run a scan to pick it up.");
            }
            warnings.AddRange(refreshed.SkippedFiles.Select(error => $"{category}: {error}"));
        }

        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var animation in exported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = state.ArchiveIndex.Categories.SingleOrDefault(item => item.Category == animation.Category);
            if (index is null || index.Status != IndexStatus.Current)
            {
                continue;
            }

            var copies = FindArchivedCopies(index, animation.AnimationPath);
            if (copies.Count > 0)
            {
                duplicates.Add(new ArchivedAnimationDuplicate(animation.AnimationPath, copies));
            }

            var mapping = state.Settings.CategoryMappings
                .SingleOrDefault(item => item.Category == animation.Category);
            var sheet = FindRecord(index, animation.SheetIndexedImageId, animation.AtlasPath);
            if (mapping is null || sheet?.Fingerprint is not { } sheetFingerprint)
            {
                // Without the sheet's own fingerprint the move cannot verify what it is moving, and
                // a move that cannot check itself is not one this app makes.
                warnings.Add(
                    $"{animation.AtlasPath}: animated, but the sheet could not be filed beside it "
                        + "because the archive index does not describe it. Run a scan.");
                continue;
            }

            var reference = await FileAnimatedSheetAsync(
                    sheet.Id,
                    sheet.Path,
                    animation.Category,
                    sheetFingerprint,
                    mapping.ArchivePath,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);
            if (reference is not null)
            {
                filed.Add(reference);
            }

            state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        var duplicateCleanup = await CleanIndexedArchiveDuplicatesCoreAsync(
                exported.Select(item => item.Category).Distinct().ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        warnings.AddRange(duplicateCleanup.Warnings);
        return new ExportedAnimationFollowUp(filed, duplicates, warnings, duplicateCleanup.Removed);
    }

    /// <summary>
    /// Adds the archived animations of the same emoji to a ranked list that may have missed them.
    /// </summary>
    /// <remarks>
    /// Only ever adds candidates to a review, and only for animations whose names agree on every
    /// animation parameter VRChat recorded. The score beside each one is measured honestly rather
    /// than asserted, so the review card shows how alike they really are.
    /// </remarks>
    private static IReadOnlyList<ImageMatchResult> WithSameEmojiAnimations(
        IReadOnlyList<ImageMatchResult> matches,
        IReadOnlyDictionary<string, IndexedImageRecord> comparable,
        string incomingPath,
        ImageFingerprint incoming)
    {
        if (!AtlasAnimationWriter.IsAnimation(incomingPath)
            || !EmojiAtlasName.TryReadAnimation(incomingPath, out var incomingName))
        {
            return matches;
        }

        var ranked = matches.Select(match => match.CandidateKey).ToHashSet(StringComparer.Ordinal);
        var added = new List<ImageMatchResult>();
        foreach (var candidate in comparable)
        {
            var indexed = candidate.Value;
            if (ranked.Contains(candidate.Key)
                || indexed.Fingerprint is null
                || !AtlasAnimationWriter.IsAnimation(indexed.Path)
                || !EmojiIdentity.IsSameEmoji(incomingPath, indexed.Path)
                || !EmojiAtlasName.TryReadAnimation(indexed.Path, out var archivedName)
                || archivedName != incomingName)
            {
                continue;
            }

            var measured = ImageMatcher.MeasureSimilarity(incoming, indexed.Fingerprint);
            added.Add(new ImageMatchResult(
                candidate.Key,
                MatchKind.Similar,
                measured.Score,
                [
                    .. measured.Reasons,
                    "The same emoji, and the same frame count, rate and loop direction in its name.",
                ]));
        }

        if (added.Count == 0)
        {
            return matches;
        }

        return
        [
            .. matches
                .Concat(added)
                .OrderByDescending(match => match.SimilarityScore)
                .ThenBy(match => match.CandidateKey, StringComparer.Ordinal),
        ];
    }

    /// <summary>Archived files that decode to the very same picture as <paramref name="path"/>.</summary>
    /// <remarks>
    /// Exact identity only. Two sizes of one emoji are a judgement and belong in the archive
    /// duplicates list, where a person can look at both; this is for the case where the export
    /// turned out to be a file the archive already had, byte for decoded byte.
    /// </remarks>
    private static IReadOnlyList<string> FindArchivedCopies(CategoryIndexState index, string path)
    {
        var exported = index.Images.FirstOrDefault(
            item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (exported is null || string.IsNullOrWhiteSpace(exported.ExactFingerprint))
        {
            return [];
        }

        return
        [
            .. index.Images
                .Where(item => item.Id != exported.Id
                    && string.Equals(item.ExactFingerprint, exported.ExactFingerprint, StringComparison.Ordinal))
                .Select(item => item.Path)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// The sheet's index record, by id where the refresh kept it and by path where it did not.
    /// </summary>
    private static IndexedImageRecord? FindRecord(CategoryIndexState index, Guid id, string path) =>
        index.Images.FirstOrDefault(item => item.Id == id)
        ?? index.Images.FirstOrDefault(
            item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <returns>Where the sheet was filed, or null when it stayed where it was.</returns>
    private async Task<string?> FileAnimatedSheetAsync(
        Guid indexedImageId,
        string archivedPath,
        VrcImageCategory category,
        ImageFingerprint fingerprint,
        string archiveRoot,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            var ownerState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            archiveRoot = ArchiveOwner.Resolve(ownerState.Settings, category, archivedPath);
            var reference = AtlasAnimationWriter.BuildReferenceDestination(archivedPath, archiveRoot);
            if (string.Equals(Path.GetFullPath(archivedPath), Path.GetFullPath(reference), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Two different sheets can carry the same file name - the archive root disambiguates
            // them, but the first one filed vacates that name, so the second arrives thinking it is
            // unique. Moving onto an existing file throws inside the journal and leaves an entry
            // that reconciliation can never settle, so the collision is caught out here instead and
            // the sheet simply stays where it is.
            if (File.Exists(reference))
            {
                errors.Add(
                    $"{archivedPath}: animated, but a different sheet of the same name is already "
                    + "filed with its animation, so this one was left in place.");
                return null;
            }

            await _router
                .FileAnimatedSheetAsync(
                    indexedImageId,
                    archivedPath,
                    reference,
                    category,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            return reference;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException)
        {
            errors.Add($"{archivedPath}: animated, but could not be filed with its animation ({exception.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Writes the missing animations for sheets that were archived before the app could make them.
    /// </summary>
    /// <remarks>
    /// A sheet this animates is filed beside its animation, exactly as one arriving during a scan
    /// is. It used to be left where it was, on the grounds that rearranging an archive is a
    /// person's decision rather than a side effect of a scan - but the result was a handful of
    /// sprite sheets sitting among the single emoji, indistinguishable from them while browsing
    /// and offering a grid of thumbnails where an emoji was expected. A sheet whose animation
    /// exists has finished being a still; leaving it in the way was the side effect, not moving it.
    /// </remarks>
    /// <param name="Written">Animations this run created.</param>
    /// <param name="Filed">Sheets it moved to sit beside an animation.</param>
    private sealed record BackfillResult(int Written, int Filed)
    {
        /// <summary>True when the archive changed, so the index no longer describes it.</summary>
        public bool ChangedTheArchive => Written > 0 || Filed > 0;
    }

    private async Task<BackfillResult> AnimateArchivedSheetsAsync(
        VrcImageCategory category,
        string archiveRoot,
        List<string> errors,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = state.ArchiveIndex.Categories.Single(item => item.Category == category);
        var skipped = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);

        // Which emoji already have an animation, whatever their file happens to be called. Read
        // from the folder rather than from the index, because this is a question about what is on
        // disk and the index can be a scan behind it.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in index.Images.Where(image => EmojiAtlasName.TryParse(image.Path, out _)))
        {
            try
            {
                owners[image.Path] = ArchiveOwner.Resolve(state.Settings, category, image.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{image.Path}: could not resolve archive ownership ({exception.Message})");
                return new BackfillResult(0, 0);
            }
        }
        var animatedByRoot = owners.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(root => root, ReadAnimatedEmoji, StringComparer.OrdinalIgnoreCase);
        var animated = new HashSet<string>(animatedByRoot.SelectMany(pair =>
            pair.Value.Select(key => pair.Key + "|" + key)), StringComparer.OrdinalIgnoreCase);
        var existing = animated.Count;

        var pending = new List<IndexedImageRecord>();

        // Sheets whose animation already exists and which are still sitting among the stills. They
        // are the ones this used to leave behind: nothing to write for them, so the loop skipped
        // them outright and they stayed in the emoji folder for good.
        var strays = new List<IndexedImageRecord>();
        foreach (var image in index.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EmojiAtlasName.TryParse(image.Path, out _))
            {
                continue;
            }

            // A skipped sheet is one a person has already looked at and decided against. Without
            // this it would be decoded in full on every single scan forever: a sheet that cannot
            // be animated never produces the file whose absence is what puts it back on the list.
            if (skipped.Contains(image.ExactFingerprint))
            {
                continue;
            }

            // Add answers both questions at once: is this emoji already animated, and has an
            // earlier sheet in this same run already claimed it.
            if (!animated.Add(owners[image.Path] + "|" + EmojiIdentity.KeyFor(image.Path)))
            {
                if (!AtlasAnimationWriter.IsReferenceFolder(image.Path))
                {
                    strays.Add(image);
                }

                continue;
            }

            pending.Add(image);
        }

        // A run that wants to animate about as much as the folder already holds is not doing work,
        // it is failing to recognise what is there - and the cost of being wrong is a second copy
        // of every animation in the archive. A small run goes through, and so does the first run
        // over an archive with no animations at all; this shape stops and says so.
        if (existing > 0 && pending.Count > MaximumUnrecognisedAnimations && pending.Count >= existing)
        {
            errors.Add(
                $"Stopped before writing {pending.Count} animations into a folder that already holds "
                    + $"{existing}. The animations already there are not being recognised, and writing "
                    + "these would leave two of each. Nothing was written.");
            return new BackfillResult(0, 0);
        }

        var written = 0;
        foreach (var image in pending)
        {
            archiveRoot = owners[image.Path];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(AtlasAnimationWriter.BuildDestination(image.Path, archiveRoot)))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{image.Path}: could not be animated ({exception.Message})");
                continue;
            }

            var animation = await _animationWriter
                .TryWriteAsync(image.Path, archiveRoot, cancellationToken)
                .ConfigureAwait(false);
            if (animation.Exported)
            {
                written++;
                if (animation.Note is { } note)
                {
                    notes.Add($"{image.Path}: {note}");
                }

                // The sheet has finished being a still, so it goes to sit with its animation.
                // Without the fingerprint the move cannot verify what it is moving, and a move
                // this app cannot check is one it does not make.
                if (image.Fingerprint is { } sheetFingerprint)
                {
                    _ = await FileAnimatedSheetAsync(
                            image.Id,
                            image.Path,
                            category,
                            sheetFingerprint,
                            archiveRoot,
                            errors,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    errors.Add(
                        $"{image.Path}: animated, but the archive index does not describe it well "
                            + "enough to file it beside its animation. Rebuild the index.");
                }
            }
            else if (animation.Warning is { } warning)
            {
                errors.Add($"{image.Path}: {warning}");
            }
        }

        // The ones that were animated before this filed anything. Their animation is already
        // there, so there was nothing to write and nothing brought them along; they are caught up
        // with here rather than left among the emoji forever.
        var caughtUp = 0;
        foreach (var stray in strays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stray.Fingerprint is not { } strayFingerprint)
            {
                continue;
            }

            var filed = await FileAnimatedSheetAsync(
                    stray.Id,
                    stray.Path,
                    category,
                    strayFingerprint,
                    archiveRoot,
                    errors,
                    cancellationToken)
                .ConfigureAwait(false);
            if (filed is not null)
            {
                caughtUp++;
                notes.Add($"{stray.Path}: its animation already existed, so the sheet was filed beside it.");
            }
        }

        return new BackfillResult(written, caughtUp);
    }

    /// <summary>
    /// The emoji that already have an animation somewhere under the archive's animation folder,
    /// keyed by the emoji rather than by file name. An unreadable folder answers "none", which is
    /// what this did before it asked the question at all.
    /// </summary>
    private static HashSet<string> ReadAnimatedEmoji(string archiveRoot)
    {
        var animated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(archiveRoot))
        {
            return animated;
        }

        try
        {
            var root = Path.Combine(archiveRoot, AtlasAnimationWriter.AnimationFolderName);
            if (!Directory.Exists(root))
            {
                return animated;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (AtlasAnimationWriter.IsAnimation(path))
                {
                    animated.Add(EmojiIdentity.KeyFor(path));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            animated.Clear();
        }

        return animated;
    }

    /// <summary>
    /// Which pass an incoming file belongs to: emoji sheets, then ordinary images, then animations.
    /// </summary>
    /// <remarks>
    /// A ready-made GIF is judged against the animation the archive's own sheet produces, so that
    /// sheet has to be archived before the GIF's turn comes. One alphabetical run gave the opposite
    /// order every time - ".gif" sorts before ".png", so a sheet and its own GIF arriving together
    /// always had the GIF examined first, with nothing in the archive to match it. It was filed as
    /// unique, the sheet followed and was animated, and the archive ended up holding the same
    /// animation twice. Sheets go first now, and every animation waits until they are all done.
    /// Files within a pass keep their alphabetical order.
    /// </remarks>
    private static int ScanPass(string path)
    {
        if (AtlasAnimationWriter.IsAnimation(path))
        {
            return 2;
        }

        return EmojiAtlasName.TryParse(path, out _) ? 0 : 1;
    }

    private static SourceSnapshot CreateSourceSnapshotSafe(string sourceRoot)
    {
        try
        {
            var allPaths = PathBoundary.EnumerateFilesWithoutReparsePoints(sourceRoot);
            var paths = allPaths
                .Where(path => ArchiveIndexer.SupportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(ScanPass)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new SourceSnapshot(paths, allPaths.Count - paths.Length, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SourceSnapshot([], 0, exception.Message);
        }
    }

    private static SourceSnapshot CreateSnapshotFromPaths(
        string sourceRoot,
        IReadOnlyCollection<string> paths)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return SourceSnapshot.Empty;
        }

        try
        {
            var root = PathBoundary.Normalize(sourceRoot);
            var supported = new List<string>();
            var unsupported = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(path);
                    // Only files inside the configured source folder are ever processed, and
                    // never through a junction or symlink.
                    if (!PathBoundary.Contains(root, full) || !File.Exists(full))
                    {
                        continue;
                    }

                    PathBoundary.EnsureNoReparsePoints(full, "Incoming image");
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or NotSupportedException
                        or PathTooLongException
                        or InvalidOperationException)
                {
                    continue;
                }

                if (ArchiveIndexer.SupportedExtensions.Contains(Path.GetExtension(full)))
                {
                    supported.Add(full);
                }
                else
                {
                    unsupported++;
                }
            }

            return new SourceSnapshot(
                supported
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ScanPass)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                unsupported,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SourceSnapshot([], 0, exception.Message);
        }
    }

    private static void ReportUnprocessed(
        int count,
        string activity,
        Action<string, string?>? onImageScanned,
        Action<string, string?>? onImageProcessed)
    {
        for (var index = 0; index < count; index++)
        {
            onImageScanned?.Invoke(activity, null);
            onImageProcessed?.Invoke(activity, null);
        }
    }

    private sealed record SourceSnapshot(IReadOnlyList<string> Paths, int UnsupportedFiles, string? Error)
    {
        public static SourceSnapshot Empty { get; } = new([], 0, null);
    }

    /// <summary>
    /// Reads one image and reduces it to a fingerprint. The decoded pixels are dropped as soon as
    /// the fingerprint exists, so only the images actively being read hold real memory. Nothing
    /// here throws: a failure becomes an error on the result so a prefetched read that is never
    /// consumed cannot surface as an unobserved exception.
    /// </summary>
    private async Task<PreparedImage> PrepareImageAsync(
        string path,
        SemaphoreSlim readSlots,
        SemaphoreSlim largeReadSlot,
        CancellationToken cancellationToken)
    {
        var isLarge = false;
        try
        {
            isLarge = new FileInfo(path).Length > LargeEncodedBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The read below reports the failure properly.
        }

        try
        {
            if (isLarge)
            {
                await largeReadSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await readSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
                    return decoded.IsSuccess
                        ? new PreparedImage(ImageFingerprint.Create(decoded.Image!), null)
                        : new PreparedImage(null, decoded.Failure!.Message);
                }
                finally
                {
                    readSlots.Release();
                }
            }
            finally
            {
                if (isLarge)
                {
                    largeReadSlot.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new PreparedImage(null, "the scan was stopped before this image was read.");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or InvalidOperationException)
        {
            return new PreparedImage(null, exception.Message);
        }
    }

    private async Task<HashSet<string>> FindSettledPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var first = new Dictionary<string, FileObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = TryObserveReadableFile(path);
            if (observation is not null)
            {
                first[path] = observation;
            }
        }

        if (first.Count > 0 && _settleDelay > TimeSpan.Zero)
        {
            await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        }

        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, firstObservation) in first)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryObserveReadableFile(path) == firstObservation)
            {
                settled.Add(path);
            }
        }

        return settled;
    }

    private static FileObservation? TryObserveReadableFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            // Share everything: VRCX may be writing into this folder right now and must never
            // be blocked by this check. Whether a file has settled is decided by comparing two
            // observations, not by holding a lock.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return new FileObservation(stream.Length, file.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record FileObservation(long Length, DateTime LastWriteTimeUtc);

    /// <summary>An image waiting in Review. It is not in the archive, so it is not in the index
    /// either, and a later copy of it can only be recognised from here.</summary>
    private sealed record HeldImage(string Path, ImageFingerprint Fingerprint);
}
