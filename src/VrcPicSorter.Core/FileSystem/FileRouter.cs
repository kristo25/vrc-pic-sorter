using System.Globalization;
using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.FileSystem;

public sealed record FileRouteResult(Guid OperationId, string? DestinationPath, Guid? IndexedImageId = null);

public sealed record KeepIncomingResult(bool ReviewResolved, string? PreservedMatchPath);

public sealed record ReviewRestoreProgress(int Processed, int Total, int Restored);

public sealed record ReviewRestoreResult(
    int Requested,
    int Restored,
    IReadOnlyList<string> Failures);

public sealed class FileRouter
{
    private readonly JsonStateStore _stateStore;
    private readonly OperationJournal _journal;
    private readonly ImageDecoder _decoder;
    private readonly IRecycleBinService _recycleBin;
    private readonly TimeProvider _timeProvider;

    public FileRouter(
        JsonStateStore stateStore,
        ImageDecoder decoder,
        IRecycleBinService recycleBin,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _journal = new OperationJournal(stateStore, _timeProvider);
    }

    public async Task<FileRouteResult> HoldForReviewAsync(
        string sourcePath,
        ReviewItem reviewItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var destination = CreateCollisionSafePath(
            Path.Combine(state.Settings.HoldingRootPath, reviewItem.Category.ToString(), Path.GetFileName(sourcePath)));
        reviewItem.HeldFilePath = destination;
        var entry = CreateMoveEntry(
            sourcePath,
            destination,
            reviewItem.Category,
            reviewItem.IncomingFingerprint,
            JournalOperationPurpose.HoldForReview);
        entry.ReviewItemId = reviewItem.Id;
        entry.ReviewItemAfterCommit = reviewItem;
        entry.RoutingContext = reviewItem.RoutingContext;
        return await ExecuteMoveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public Task QueueForReviewAsync(
        ReviewItem reviewItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        if (!reviewItem.IsIncomingInPlace)
        {
            throw new InvalidOperationException("An in-place review must reference its original incoming file.");
        }

        return _stateStore.UpdateAsync(
            state =>
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == reviewItem.Category);
                if (index.Status != IndexStatus.Current || index.Generation != reviewItem.IndexGeneration)
                {
                    throw new InvalidOperationException("The archive index changed before the review was queued.");
                }

                if (state.ReviewQueue.Any(
                        item => item.Status != ReviewStatus.Resolved
                            && string.Equals(
                                item.IncomingOriginalPath,
                                reviewItem.IncomingOriginalPath,
                                StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                state.ReviewQueue.Add(reviewItem);
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = _timeProvider.GetUtcNow(),
                    Kind = ActivityKind.ReviewDecision,
                    Level = ActivityLevel.Information,
                    Category = reviewItem.Category,
                    Message = "Queued incoming image for review.",
                    SourcePath = reviewItem.IncomingOriginalPath,
                });
                return true;
            },
            cancellationToken);
    }

    public async Task<FileRouteResult> MoveUniqueAsync(
        string sourcePath,
        VrcImageCategory category,
        ImageFingerprint fingerprint,
        ScanRoutingContext? routingContext = null,
        Guid? reviewItemId = null,
        bool duplicateOverride = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var destination = await BuildArchiveDestinationAsync(sourcePath, category, routingContext, cancellationToken)
            .ConfigureAwait(false);
        var entry = CreateMoveEntry(
            sourcePath,
            destination,
            category,
            fingerprint.ExactIdentity,
            duplicateOverride
                ? JournalOperationPurpose.MoveDuplicateOverride
                : JournalOperationPurpose.MoveUnique);
        entry.ReviewItemId = reviewItemId;
        entry.RoutingContext = routingContext;
        entry.IndexedImageAfterCommit = CreateIndexedRecord(category, destination, sourcePath, fingerprint);
        return await ExecuteMoveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves an already archived sheet to <paramref name="destinationPath"/> and points its index
    /// record at the new place. Used once an animation has been written, to file the atlas beside
    /// it.
    /// </summary>
    /// <remarks>
    /// This is a second move of a file that is already safe, so it is journalled like any other:
    /// a crash between the move and the commit leaves an entry reconciliation can finish from what
    /// is on disk, rather than an index pointing at a path that no longer exists.
    /// </remarks>
    public async Task<FileRouteResult> FileAnimatedSheetAsync(
        Guid indexedImageId,
        string archivedPath,
        string destinationPath,
        VrcImageCategory category,
        ImageFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var entry = CreateMoveEntry(
            archivedPath,
            destinationPath,
            category,
            fingerprint.ExactIdentity,
            JournalOperationPurpose.FileAnimatedSheet);
        entry.IndexedImageId = indexedImageId;
        return await ExecuteMoveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recycles the held incoming image because the archived match at
    /// <paramref name="survivor"/> is the copy being kept.
    /// </summary>
    /// <param name="survivor">
    /// The archived copy this decision is in favour of. Recorded on the journal entry so a retry
    /// after a crash can re-check that it is still there, rather than discarding the incoming
    /// image on a precondition nobody has looked at since.
    /// </param>
    public async Task KeepExistingAsync(
        ReviewItem reviewItem,
        ReviewCandidate survivor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        ArgumentNullException.ThrowIfNull(survivor);
        var entry = CreateRecycleEntry(
            reviewItem.HeldFilePath,
            reviewItem.Category,
            reviewItem.IncomingFingerprint,
            JournalOperationPurpose.KeepExisting);
        entry.ReviewItemId = reviewItem.Id;
        entry.SurvivingPath = survivor.ArchivePath;
        entry.SurvivingFingerprint = survivor.ExpectedFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an exact duplicate without review: the archived image is kept and the incoming
    /// copy goes to the Recycle Bin. Throws <see cref="NotSupportedException"/> when the Recycle
    /// Bin is unavailable for the incoming path, so the caller can fall back to a review instead
    /// of ever deleting permanently.
    /// </summary>
    public async Task AutoKeepArchivedAsync(
        string incomingPath,
        VrcImageCategory category,
        ImageFingerprint incomingFingerprint,
        string archivePath,
        string archiveFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingFingerprint);

        // The archived copy must still be exactly what was indexed before the incoming one is
        // discarded, otherwise this would delete the only remaining copy.
        await VerifyImageFingerprintAsync(
                archivePath,
                archiveFingerprint,
                "The archive match changed after it was scanned. No file operation was performed.",
                cancellationToken)
            .ConfigureAwait(false);

        var entry = CreateRecycleEntry(
            incomingPath,
            category,
            incomingFingerprint.ExactIdentity,
            JournalOperationPurpose.AutoKeepArchived);
        entry.SurvivingPath = archivePath;
        entry.SurvivingFingerprint = archiveFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a second copy of a picture that is already waiting in Review: the held copy keeps
    /// the decision and this copy goes to the Recycle Bin. Throws
    /// <see cref="NotSupportedException"/> when the Recycle Bin is unavailable for the incoming
    /// path, so the caller can fall back to a review of its own instead of ever deleting
    /// permanently.
    /// </summary>
    public async Task AutoKeepHeldAsync(
        string incomingPath,
        VrcImageCategory category,
        ImageFingerprint incomingFingerprint,
        string heldPath,
        string heldFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingFingerprint);

        // The held copy must still be exactly what was scanned before this one is discarded,
        // otherwise this would delete the only remaining copy.
        await VerifyImageFingerprintAsync(
                heldPath,
                heldFingerprint,
                "The image waiting in Review changed after it was scanned. No file operation was performed.",
                cancellationToken)
            .ConfigureAwait(false);

        var entry = CreateRecycleEntry(
            incomingPath,
            category,
            incomingFingerprint.ExactIdentity,
            JournalOperationPurpose.AutoKeepHeld);
        entry.SurvivingPath = heldPath;
        entry.SurvivingFingerprint = heldFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task KeepMatchAsync(
        ReviewItem reviewItem,
        ReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        ArgumentNullException.ThrowIfNull(candidate);
        await VerifyImageFingerprintAsync(
                candidate.ArchivePath,
                candidate.ExpectedFingerprint,
                "The archive match changed after it was scanned. No file operation was performed.",
                cancellationToken)
            .ConfigureAwait(false);
        await KeepExistingAsync(reviewItem, candidate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KeepIncomingResult> KeepIncomingOverMatchAsync(
        ReviewItem reviewItem,
        ReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        ArgumentNullException.ThrowIfNull(candidate);
        if (reviewItem.IncomingImageFingerprint is null)
        {
            throw new InvalidOperationException("The incoming fingerprint is missing. Run a new scan.");
        }

        await VerifyImageFingerprintAsync(
                reviewItem.HeldFilePath,
                reviewItem.IncomingImageFingerprint.ExactIdentity,
                "The held incoming image changed after it was scanned. No file operation was performed.",
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyImageFingerprintAsync(
                candidate.ArchivePath,
                candidate.ExpectedFingerprint,
                "The archive match changed after it was scanned. No file operation was performed.",
                cancellationToken)
            .ConfigureAwait(false);

        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = state.ReviewQueue.Single(item => item.Id == reviewItem.Id);
        var currentCandidate = current.Candidates.Single(item => item.Id == candidate.Id);
        if (current.Candidates.Count > 1)
        {
            // One match of several: the incoming image stays held while the others are decided, so
            // the copy that justifies removing this match is the held one.
            var preservedPath = await RemoveArchiveCandidateAsync(
                    current,
                    currentCandidate,
                    current.HeldFilePath,
                    current.IncomingFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            return new KeepIncomingResult(false, preservedPath);
        }

        if (current.IncomingImageFingerprint is null)
        {
            throw new InvalidOperationException("The incoming fingerprint is missing. Run a new scan.");
        }

        // The move deliberately does not resolve the review: this decision has two halves, and
        // if the second one fails the queue entry has to survive so it can be retried. Resolving
        // inside the move left the archived duplicate in place with nothing left to act on.
        //
        // Which means the first half may already be done, and a failure in the second does not
        // undo it. The review records where the incoming image landed the moment that half
        // commits, so a retry resumes from there instead of putting a second copy in the archive.
        var archivedPath = ResumeArchivedIncomingPath(state, current);
        if (string.IsNullOrWhiteSpace(archivedPath))
        {
            var route = await MoveUniqueAsync(
                    current.HeldFilePath,
                    current.Category,
                    current.IncomingImageFingerprint,
                    current.RoutingContext,
                    reviewItemId: current.Id,
                    duplicateOverride: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            archivedPath = route.DestinationPath
                ?? throw new InvalidOperationException("The archived copy of the incoming image has no path.");
        }

        var finalPreservedPath = await RemoveArchiveCandidateAsync(
                current,
                currentCandidate,
                archivedPath,
                current.IncomingImageFingerprint.ExactIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        await ResolveKeepIncomingAsync(current, cancellationToken).ConfigureAwait(false);
        return new KeepIncomingResult(true, finalPreservedPath);
    }

    /// <summary>
    /// Where a Keep incoming decision already put the incoming image, or empty when its first half
    /// has not run.
    /// </summary>
    /// <remarks>
    /// The review carries the answer from the moment the move commits. State written by an earlier
    /// version does not, so the index holding a record at the very path the review points to is
    /// read as the same thing - that is what the first half leaves behind either way, and without
    /// it an old state document would be archived a second time.
    /// </remarks>
    private static string ResumeArchivedIncomingPath(AppStateDocument state, ReviewItem review)
    {
        if (!string.IsNullOrWhiteSpace(review.KeptIncomingArchivedPath))
        {
            return review.KeptIncomingArchivedPath;
        }

        if (string.IsNullOrWhiteSpace(review.HeldFilePath))
        {
            return string.Empty;
        }

        // Deliberately not the fail-closed path comparison used before a recycle. Here a path that
        // cannot be compared has to mean "no record found", so the move runs and is checked on its
        // own terms; treating it as a match would hand an unrelated archived file to the step that
        // removes the old one.
        var index = state.ArchiveIndex.Categories.SingleOrDefault(item => item.Category == review.Category);
        var archived = index?.Images.FirstOrDefault(
            item => !string.IsNullOrWhiteSpace(item.Path)
                && string.Equals(item.Path, review.HeldFilePath, StringComparison.OrdinalIgnoreCase));
        return archived?.Path ?? string.Empty;
    }

    /// <summary>Closes a Keep incoming decision once both halves of it have committed.</summary>
    /// <remarks>
    /// The history entry is written whether or not the review is still in the queue. Removing the
    /// last match completes the decision itself, and a resolved review is compacted out of the
    /// document on the very next write - so by the time this runs the queue entry is usually
    /// already gone, and gating the history on finding it meant the ordinary success path recorded
    /// nothing at all. What was decided is read from the review as it was, not from the queue.
    /// </remarks>
    private Task ResolveKeepIncomingAsync(ReviewItem decided, CancellationToken cancellationToken) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var review = state.ReviewQueue.SingleOrDefault(item => item.Id == decided.Id);
                if (review is not null)
                {
                    CompleteKeepIncoming(review);
                }

                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = _timeProvider.GetUtcNow(),
                    Kind = ActivityKind.ReviewDecision,
                    Level = ActivityLevel.Information,
                    Category = decided.Category,
                    Message = "Kept the incoming image and removed the archived match.",
                    SourcePath = decided.IncomingOriginalPath,
                });
                return true;
            },
            cancellationToken);

    private async Task<string?> RemoveArchiveCandidateAsync(
        ReviewItem reviewItem,
        ReviewCandidate candidate,
        string survivingPath,
        string survivingFingerprint,
        CancellationToken cancellationToken)
    {
        if (_recycleBin.CanRecycle(candidate.ArchivePath))
        {
            await DeleteArchiveCandidateAsync(
                    reviewItem,
                    candidate,
                    survivingPath,
                    survivingFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var archiveRoots = state.Settings.LegacyArchiveMappings
            .Where(item => item.Category == reviewItem.Category)
            .Select(item => item.ArchivePath)
            .Prepend(state.Settings.CategoryMappings.Single(item => item.Category == reviewItem.Category).ArchivePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathBoundary.Normalize)
            .Where(path => PathBoundary.Contains(path, candidate.ArchivePath))
            .OrderByDescending(path => path.Length)
            .ToArray();
        var archiveRoot = archiveRoots.FirstOrDefault();
        var recoveryBase = archiveRoot is null
            ? state.Settings.OutputRootPath
            : Path.GetDirectoryName(archiveRoot)
                ?? throw new InvalidOperationException("The archive folder has no parent folder.");
        var relativePath = archiveRoot is null
            ? Path.GetFileName(candidate.ArchivePath)
            : Path.GetRelativePath(archiveRoot, candidate.ArchivePath);
        var recoveryRoot = Path.Combine(recoveryBase, "VRC Pic Sorter Replaced", reviewItem.Category.ToString());
        var destination = CreateCollisionSafePath(Path.Combine(recoveryRoot, relativePath));
        PathBoundary.EnsureContained(recoveryRoot, destination, "Preserved archive match");
        var entry = CreateMoveEntry(
            candidate.ArchivePath,
            destination,
            reviewItem.Category,
            candidate.ExpectedFingerprint,
            JournalOperationPurpose.PreserveArchiveCandidate);
        entry.ReviewItemId = reviewItem.Id;
        entry.IndexedImageId = candidate.IndexedImageId;
        var result = await ExecuteMoveAsync(entry, cancellationToken).ConfigureAwait(false);
        return result.DestinationPath;
    }

    public async Task<FileRouteResult> RestoreReviewAsync(
        ReviewItem reviewItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        if (reviewItem.IsIncomingInPlace)
        {
            await DismissReviewAsync(reviewItem.Id, cancellationToken).ConfigureAwait(false);
            return new FileRouteResult(Guid.Empty, reviewItem.IncomingOriginalPath);
        }

        var requestedDestination = Path.GetFullPath(reviewItem.IncomingOriginalPath);
        if (!string.IsNullOrWhiteSpace(reviewItem.RoutingContext.SourceRootPath))
        {
            PathBoundary.EnsureContained(
                reviewItem.RoutingContext.SourceRootPath,
                requestedDestination,
                "Restored image destination");
        }

        var destination = CreateCollisionSafePath(requestedDestination);
        var entry = CreateMoveEntry(
            reviewItem.HeldFilePath,
            destination,
            reviewItem.Category,
            reviewItem.IncomingFingerprint,
            JournalOperationPurpose.RestoreReviewToSource);
        entry.ReviewItemId = reviewItem.Id;
        entry.RoutingContext = reviewItem.RoutingContext;
        return await ExecuteMoveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewRestoreResult> RestoreReviewsAsync(
        IReadOnlyCollection<ReviewItem> reviews,
        IProgress<ReviewRestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var pending = reviews.ToArray();
        var failures = new List<string>();
        var processed = 0;
        var restored = 0;
        progress?.Report(new ReviewRestoreProgress(processed, pending.Length, restored));

        var inPlace = pending.Where(review => review.IsIncomingInPlace).ToArray();
        if (inPlace.Length > 0)
        {
            try
            {
                var reviewIds = inPlace.Select(review => review.Id).ToHashSet();
                await _stateStore.UpdateAsync(
                        state =>
                        {
                            foreach (var review in state.ReviewQueue.Where(item => reviewIds.Contains(item.Id)))
                            {
                                review.Status = ReviewStatus.Resolved;
                                review.HeldFilePath = string.Empty;
                            }

                            state.History.Add(new ActivityEntry
                            {
                                Id = Guid.NewGuid(),
                                OccurredUtc = _timeProvider.GetUtcNow(),
                                Kind = ActivityKind.ReviewDecision,
                                Level = ActivityLevel.Information,
                                Message = $"Cleared {inPlace.Length} in-place review item(s) without changing their files.",
                            });
                            return true;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                restored += inPlace.Length;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                failures.AddRange(inPlace.Select(
                    review => $"{review.IncomingOriginalPath}: {exception.Message}"));
            }
            finally
            {
                foreach (var _ in inPlace)
                {
                    processed++;
                    progress?.Report(new ReviewRestoreProgress(processed, pending.Length, restored));
                }
            }
        }

        foreach (var review in pending.Where(review => !review.IsIncomingInPlace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RestoreReviewAsync(review, cancellationToken).ConfigureAwait(false);
                restored++;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                failures.Add($"{review.HeldFilePath}: {exception.Message}");
            }
            finally
            {
                processed++;
                progress?.Report(new ReviewRestoreProgress(processed, pending.Length, restored));
            }
        }

        return new ReviewRestoreResult(pending.Length, restored, failures);
    }

    public Task DismissReviewAsync(Guid reviewItemId, CancellationToken cancellationToken = default)
    {
        if (reviewItemId == Guid.Empty)
        {
            throw new ArgumentException("A review ID is required.", nameof(reviewItemId));
        }

        return _stateStore.UpdateAsync(
            state =>
            {
                var review = state.ReviewQueue.SingleOrDefault(item => item.Id == reviewItemId);
                if (review is null)
                {
                    return false;
                }

                review.Status = ReviewStatus.Resolved;
                review.HeldFilePath = string.Empty;
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = _timeProvider.GetUtcNow(),
                    Kind = ActivityKind.ReviewDecision,
                    Level = ActivityLevel.Information,
                    Category = review.Category,
                    Message = "Removed image from the review queue without changing its file.",
                    SourcePath = review.IncomingOriginalPath,
                });
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Recycles an archived match because <paramref name="survivingPath"/> is the copy being kept
    /// in its place.
    /// </summary>
    /// <remarks>
    /// The surviving copy is checked on disk before anything goes, and recorded on the journal
    /// entry so a retry after a crash checks it again. Without that, an interrupted decision could
    /// come back and remove the archived match after the copy it was replaced by had been moved
    /// away by hand - leaving neither.
    /// </remarks>
    public async Task DeleteArchiveCandidateAsync(
        ReviewItem reviewItem,
        ReviewCandidate candidate,
        string survivingPath,
        string survivingFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewItem);
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureDistinctSurvivor(candidate.ArchivePath, survivingPath, survivingFingerprint);
        await VerifyImageFingerprintAsync(
                survivingPath,
                survivingFingerprint,
                "The copy this match would be removed in favour of is no longer there, "
                    + "so nothing was removed.",
                cancellationToken)
            .ConfigureAwait(false);

        var entry = CreateRecycleEntry(
            candidate.ArchivePath,
            reviewItem.Category,
            candidate.ExpectedFingerprint,
            JournalOperationPurpose.DeleteArchiveCandidate);
        entry.ReviewItemId = reviewItem.Id;
        entry.IndexedImageId = candidate.IndexedImageId;
        entry.SurvivingPath = survivingPath;
        entry.SurvivingFingerprint = survivingFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes one copy of an image the archive is holding twice: the file goes to the Recycle Bin
    /// and its record leaves the index. Nothing about a review is involved, so this is the same
    /// operation as removing an archived match, without one.
    /// </summary>
    /// <param name="keep">
    /// The copy that stays. Removing one of two copies is only safe because the other one is
    /// there, so the other one is named, checked on disk, and recorded on the journal entry.
    /// </param>
    /// <remarks>
    /// Both files are verified before either is touched: the one going, against what the index
    /// says it was, and the one staying, against what the index says it is. The index alone was
    /// never enough - it describes the archive as the last scan saw it, and a keeper that has since
    /// been renamed, replaced or deleted leaves the removal taking the only copy there is. A group
    /// whose two records name the same file, or the same picture twice under one path, is refused
    /// outright.
    /// </remarks>
    public async Task RemoveArchivedDuplicateAsync(
        IndexedImageRecord duplicate,
        IndexedImageRecord keep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        ArgumentNullException.ThrowIfNull(keep);

        if (duplicate.Id == keep.Id)
        {
            throw new InvalidOperationException(
                "A copy cannot be the reason to discard itself. The duplicate group is stale; rescan the archive.");
        }

        if (!string.Equals(duplicate.ExactFingerprint, keep.ExactFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The copy being kept is not the same picture as the one being removed. "
                    + "The duplicate group is stale; rescan the archive.");
        }

        EnsureDistinctSurvivor(duplicate.Path, keep.Path, keep.ExactFingerprint);
        await VerifyImageFingerprintAsync(
                keep.Path,
                keep.ExactFingerprint,
                "The copy this one would be removed in favour of is no longer what the archive "
                    + "recorded, so nothing was removed.",
                cancellationToken)
            .ConfigureAwait(false);

        var entry = CreateRecycleEntry(
            duplicate.Path,
            duplicate.Category,
            duplicate.ExactFingerprint,
            JournalOperationPurpose.RemoveArchiveDuplicate);
        entry.IndexedImageId = duplicate.Id;
        entry.SurvivingPath = keep.Path;
        entry.SurvivingFingerprint = keep.ExactFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a second encoding of an animation the archive already holds: the file goes to the
    /// Recycle Bin and its record leaves the index.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same method as removing an identical copy, and it does not relax that
    /// method's rule. These two files are not the same bytes and never will be - one is VRChat's
    /// own GIF and the other is this app's export of the sheet it came from. What makes them the
    /// same animation is VRChat's own account of its own inventory item: the emoji's id, and the
    /// frame count, rate and loop direction it wrote into both names. That is checked here, on the
    /// names, before anything is opened; then the copy being kept is checked on disk like any
    /// other. Nothing reaches this method automatically - a person presses the button.
    /// </remarks>
    public async Task RemoveSupersededAnimationAsync(
        IndexedImageRecord superseded,
        IndexedImageRecord keep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        ArgumentNullException.ThrowIfNull(keep);

        if (superseded.Id == keep.Id)
        {
            throw new InvalidOperationException(
                "A copy cannot be the reason to discard itself. The duplicate group is stale; rescan the archive.");
        }

        if (!AtlasAnimationWriter.IsAnimation(superseded.Path) || !AtlasAnimationWriter.IsAnimation(keep.Path))
        {
            throw new InvalidOperationException("Only an animation can be removed in favour of another animation.");
        }

        if (!EmojiIdentity.IsSameEmoji(superseded.Path, keep.Path))
        {
            throw new InvalidOperationException(
                "These two files are not of the same emoji, so neither can stand in for the other.");
        }

        if (!EmojiAtlasName.TryReadAnimation(superseded.Path, out var supersededName)
            || !EmojiAtlasName.TryReadAnimation(keep.Path, out var keepName)
            || supersededName != keepName)
        {
            throw new InvalidOperationException(
                "These two animations do not agree on what they play, so neither can stand in for the other.");
        }

        EnsureDistinctSurvivor(superseded.Path, keep.Path, keep.ExactFingerprint);
        await VerifyImageFingerprintAsync(
                keep.Path,
                keep.ExactFingerprint,
                "The copy this one would be removed in favour of is no longer what the archive "
                    + "recorded, so nothing was removed.",
                cancellationToken)
            .ConfigureAwait(false);

        var entry = CreateRecycleEntry(
            superseded.Path,
            superseded.Category,
            superseded.ExactFingerprint,
            JournalOperationPurpose.RemoveArchiveDuplicate);
        entry.IndexedImageId = superseded.Id;
        entry.SurvivingPath = keep.Path;
        entry.SurvivingFingerprint = keep.ExactFingerprint;
        await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a removal whose surviving copy is missing, unnamed, or the very file being removed.
    /// </summary>
    /// <remarks>
    /// Two records can name one file - the same path written twice, or two spellings of it - and a
    /// removal justified by such a pair would recycle the file it promised to keep. Compared after
    /// normalizing, so "a\b.png" and "a\.\B.PNG" are recognised as one file rather than two.
    /// </remarks>
    private static void EnsureDistinctSurvivor(string removedPath, string survivingPath, string survivingFingerprint)
    {
        if (string.IsNullOrWhiteSpace(survivingPath) || string.IsNullOrWhiteSpace(survivingFingerprint))
        {
            throw new InvalidOperationException(
                "Nothing can be removed without naming the copy that stays and what it must still be.");
        }

        if (PathsMatch(removedPath, survivingPath))
        {
            throw new InvalidOperationException(
                "Both records name the same file, so removing one would remove the only copy.");
        }
    }

    /// <summary>True when two paths name the same file once spelling is taken out of it.</summary>
    private static bool PathsMatch(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                PathBoundary.Normalize(first),
                PathBoundary.Normalize(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path cannot be shown to be the same file as anything, and saying "not the
            // same" here would let a removal through on it. The caller's own verification refuses
            // it a moment later, on the file rather than on the spelling.
            return true;
        }
    }

    public bool CanRecycle(string path) => _recycleBin.CanRecycle(path);

    public async Task<IReadOnlyList<JournalReconciliationDecision>> RecoverPendingOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var decisions = new List<JournalReconciliationDecision>();
        foreach (var entry in state.OperationJournal.Where(item => item.Phase != JournalPhase.Completed).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await ObserveAsync(entry, cancellationToken).ConfigureAwait(false);
            var decision = OperationJournal.Reconcile(entry, observation);
            decisions.Add(decision);
            try
            {
                switch (decision.Action)
                {
                    case JournalReconciliationAction.RetrySideEffect:
                        await VerifySurvivingCopyAsync(entry, cancellationToken).ConfigureAwait(false);
                        if (entry.Phase is JournalPhase.IntentRecorded or JournalPhase.NeedsAttention)
                        {
                            await _journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await ApplySideEffectAsync(entry, cancellationToken).ConfigureAwait(false);
                        await RestoreCarriedFingerprintAsync(entry, cancellationToken).ConfigureAwait(false);
                        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
                        break;
                    case JournalReconciliationAction.CommitState:
                        await RestoreCarriedFingerprintAsync(entry, cancellationToken).ConfigureAwait(false);
                        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
                        break;
                    case JournalReconciliationAction.MarkCompleted:
                        await _journal.AdvanceAsync(entry.Id, JournalPhase.Completed, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case JournalReconciliationAction.NeedsAttention:
                        if (entry.Phase != JournalPhase.NeedsAttention)
                        {
                            await _journal.AdvanceAsync(
                                    entry.Id,
                                    JournalPhase.NeedsAttention,
                                    decision.Reason,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        break;
                }
            }
            // NotSupportedException belongs here for the same reason every other caller of the
            // Recycle Bin catches it: one entry whose drive no longer takes a recycle must land in
            // Needs attention, not abort startup recovery and leave every later entry - including
            // held files with no review item yet - unreconciled.
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                await MarkNeedsAttentionAsync(entry.Id, exception.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        return decisions;
    }

    public Task<int> DismissNeedsAttentionOperationsAsync(CancellationToken cancellationToken = default) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var entries = state.OperationJournal
                    .Where(item => item.Phase == JournalPhase.NeedsAttention)
                    .ToArray();
                foreach (var entry in entries)
                {
                    var index = state.ArchiveIndex.Categories.Single(item => item.Category == entry.Category);
                    index.Status = IndexStatus.Stale;
                    index.LastError = "A recovery operation was dismissed; rebuild required.";

                    // A hold whose file already reached the Holding folder carries its review item
                    // here on the journal entry and nowhere else. Completing the entry without it
                    // left the file on disk with nothing naming it: no review, no row in the queue,
                    // and Clear local data refusing forever over a file it could not point at. It
                    // goes into the queue for reconciliation instead.
                    var rescued = RescueHeldReview(state, entry);

                    entry.Phase = JournalPhase.Completed;
                    entry.UpdatedUtc = _timeProvider.GetUtcNow();
                    state.History.Add(new ActivityEntry
                    {
                        Id = Guid.NewGuid(),
                        OccurredUtc = _timeProvider.GetUtcNow(),
                        Kind = ActivityKind.Warning,
                        Level = ActivityLevel.Warning,
                        Category = entry.Category,
                        Message = rescued
                            ? "Dismissed an ambiguous recovery operation without changing either file. "
                                + "The image it had already moved is back in the review queue."
                            : "Dismissed an ambiguous recovery operation without changing either file.",
                        SourcePath = entry.SourcePath,
                        DestinationPath = entry.DestinationPath,
                        OperationId = entry.Id,
                    });
                }

                return entries.Length;
            },
            cancellationToken);

    private async Task<FileRouteResult> ExecuteMoveAsync(
        JournalEntry entry,
        CancellationToken cancellationToken)
    {
        await VerifyExpectedSourceAsync(entry, cancellationToken).ConfigureAwait(false);
        await _journal.RecordIntentAsync(entry, cancellationToken).ConfigureAwait(false);
        await _journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await VerifyExpectedSourceAsync(entry, cancellationToken).ConfigureAwait(false);
        await ApplySideEffectAsync(entry, cancellationToken).ConfigureAwait(false);

        // One durable write applies the mutation and completes the entry. Splitting it into
        // three cost three full rewrites of the state document per file operation, and the
        // intermediate phases were only reachable in the window this removes: a crash after the
        // side effect still leaves SideEffectStarted, which reconciliation resolves correctly
        // from what is actually on disk.
        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
        return new FileRouteResult(entry.Id, entry.DestinationPath, entry.IndexedImageAfterCommit?.Id);
    }

    private async Task ExecuteRecycleAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (!_recycleBin.CanRecycle(entry.SourcePath))
        {
            throw new NotSupportedException("Windows Recycle Bin behavior is unavailable for this path.");
        }

        await VerifyExpectedSourceAsync(entry, cancellationToken).ConfigureAwait(false);
        await _journal.RecordIntentAsync(entry, cancellationToken).ConfigureAwait(false);
        await _journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await VerifyExpectedSourceAsync(entry, cancellationToken).ConfigureAwait(false);
        await ApplySideEffectAsync(entry, cancellationToken).ConfigureAwait(false);
        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplySideEffectAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (entry.OperationType == JournalOperationType.Recycle)
        {
            await _recycleBin.RecycleAsync(entry.SourcePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        PathBoundary.EnsureNoReparsePoints(entry.DestinationPath!, "Image destination");
        Directory.CreateDirectory(Path.GetDirectoryName(entry.DestinationPath!)!);
        File.Move(entry.SourcePath, entry.DestinationPath!, overwrite: false);
    }

    /// <summary>
    /// Re-checks the copy an automatic duplicate resolution decided in favour of, before its
    /// recycle is retried.
    /// </summary>
    /// <remarks>
    /// The check that made the recycle safe ran before the crash. In between, the copy it was
    /// about can have been moved, renamed or deleted by hand - and retrying blind would then take
    /// the last one there is. An operation whose survivor is gone lands in Needs attention instead.
    /// </remarks>
    private async Task VerifySurvivingCopyAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var path = entry.SurvivingPath;
        var fingerprint = entry.SurvivingFingerprint;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fingerprint))
        {
            if (OperationJournal.RequiresSurvivingCopy(entry))
            {
                // Recorded by a version that kept no proof. Inheriting permission to recycle from
                // a check nobody can see is exactly the failure this exists to prevent, so it goes
                // to Needs attention with both paths named and waits for a person.
                throw new InvalidOperationException(
                    "This pending operation was recorded before the app kept a record of the copy "
                        + "it was discarding in favour of, so it cannot be retried on its own. "
                        + "Check both files and dismiss it from Needs attention.");
            }

            return;
        }

        if (PathsMatch(entry.SourcePath, path))
        {
            throw new InvalidOperationException(
                "This pending operation names the same file as the copy it would keep, so nothing was discarded.");
        }

        await VerifyImageFingerprintAsync(
                path,
                fingerprint,
                "The copy this one would have been discarded in favour of is no longer there, "
                    + "so nothing was discarded.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task VerifyExpectedSourceAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        VerifyImageFingerprintAsync(
            entry.SourcePath,
            entry.ExpectedSource.Fingerprint,
            "The source image changed after it was scanned. No file operation was performed.",
            cancellationToken);

    private async Task VerifyImageFingerprintAsync(
        string path,
        string expectedFingerprint,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        PathBoundary.EnsureNoReparsePoints(path, "Image");
        var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess
            || ImageFingerprint.Create(decoded.Image!).ExactIdentity != expectedFingerprint)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    /// <param name="source">
    /// The in-memory entry. Fingerprints are not persisted on journal entries, so the one held
    /// here has to be carried across; without it the archived record would land without its
    /// fingerprint and force a full index rebuild after every single move.
    /// </param>
    private Task CommitMutationAsync(JournalEntry source, CancellationToken cancellationToken) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var entry = state.OperationJournal.Single(item => item.Id == source.Id);
                if (entry.IndexedImageAfterCommit is not null
                    && source.IndexedImageAfterCommit?.Fingerprint is { } carried)
                {
                    entry.IndexedImageAfterCommit.Fingerprint = carried;
                }

                ApplyMutation(state, entry);
                entry.Phase = JournalPhase.Completed;
                entry.UpdatedUtc = _timeProvider.GetUtcNow();
                entry.LastError = null;
                state.History.Add(CreateActivity(entry));
                return true;
            },
            cancellationToken);

    private static void ApplyMutation(AppStateDocument state, JournalEntry entry)
    {
        var index = state.ArchiveIndex.Categories.Single(item => item.Category == entry.Category);
        switch (entry.Purpose)
        {
            case JournalOperationPurpose.HoldForReview:
                if (!state.ReviewQueue.Any(item => item.Id == entry.ReviewItemId))
                {
                    state.ReviewQueue.Add(entry.ReviewItemAfterCommit!);
                }

                break;
            case JournalOperationPurpose.MoveUnique:
            case JournalOperationPurpose.MoveDuplicateOverride:
                if (entry.IndexedImageAfterCommit is not null
                    && !index.Images.Any(item => item.Id == entry.IndexedImageAfterCommit.Id))
                {
                    index.Images.Add(entry.IndexedImageAfterCommit);
                    index.Generation++;
                }

                // Keep incoming has two halves and the override is the first of them. Resolving
                // here would strand the archived duplicate with nothing left to act on, so the
                // review stays open - but it has to follow the file it is about. Left pointing at
                // the path the image has moved out of, the review could not be retried, resolved
                // or restored; every one of those verifies the held file first, and it is gone.
                if (entry.Purpose == JournalOperationPurpose.MoveDuplicateOverride)
                {
                    RepointReview(state, entry.ReviewItemId, entry.DestinationPath);
                }
                else
                {
                    ResolveReview(state, entry.ReviewItemId);
                }

                break;
            case JournalOperationPurpose.FileAnimatedSheet:
                var filed = index.Images.SingleOrDefault(item => item.Id == entry.IndexedImageId);
                if (filed is not null && entry.DestinationPath is { } filedPath)
                {
                    filed.Path = filedPath;
                    index.Generation++;
                }

                break;
            case JournalOperationPurpose.KeepExisting:
            case JournalOperationPurpose.AutoKeepArchived:
            case JournalOperationPurpose.RestoreReviewToSource:
                ResolveReview(state, entry.ReviewItemId);
                break;
            case JournalOperationPurpose.AutoKeepHeld:
                // Deliberately nothing: the held copy keeps its place in the queue, and this
                // entry only recycled a second copy of the same picture.
                break;
            case JournalOperationPurpose.DeleteArchiveCandidate:
            case JournalOperationPurpose.PreserveArchiveCandidate:
            case JournalOperationPurpose.RemoveArchiveDuplicate:
                index.Images.RemoveAll(item => item.Id == entry.IndexedImageId);
                var review = state.ReviewQueue.SingleOrDefault(item => item.Id == entry.ReviewItemId);
                review?.Candidates.RemoveAll(item => item.IndexedImageId == entry.IndexedImageId);
                index.Generation++;

                // The second half of Keep incoming, whether it ran now or was finished by
                // recovery hours later. With the incoming image archived and the last match gone,
                // the decision is complete - and a review left open here pointed at an image the
                // archive already owns and a match that no longer exists, which nothing could act
                // on and only recovery could clear.
                if (review is { Candidates.Count: 0 }
                    && !string.IsNullOrWhiteSpace(review.KeptIncomingArchivedPath))
                {
                    CompleteKeepIncoming(review);
                }

                break;
        }
    }

    /// <summary>
    /// Puts back the review item of a dismissed hold whose file is already in the Holding folder.
    /// </summary>
    /// <returns>True when a review was put back, so the history entry can say so.</returns>
    private static bool RescueHeldReview(AppStateDocument state, JournalEntry entry)
    {
        if (entry.Purpose != JournalOperationPurpose.HoldForReview
            || entry.ReviewItemAfterCommit is not { } held
            || string.IsNullOrWhiteSpace(entry.DestinationPath)
            || !File.Exists(entry.DestinationPath)
            || state.ReviewQueue.Any(item => item.Id == held.Id))
        {
            return false;
        }

        // Reconciliation rather than pending: the file moved, the commit never happened, and what
        // the review says about it has not been checked against the disk since.
        held.Status = ReviewStatus.NeedsReconciliation;
        held.HeldFilePath = entry.DestinationPath;
        state.ReviewQueue.Add(held);
        return true;
    }

    /// <summary>
    /// Points a review that stays open at where its incoming image has moved to, and records that
    /// the first half of a Keep incoming decision is done.
    /// </summary>
    /// <remarks>
    /// Written in the same durable update as the index record, so the two can never disagree about
    /// whether the image was archived. It is what a retry reads to know it must not archive a
    /// second copy, and what tells the removal of the last match that the decision is finished.
    /// </remarks>
    private static void RepointReview(AppStateDocument state, Guid? reviewItemId, string? destinationPath)
    {
        if (reviewItemId is null || string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        var review = state.ReviewQueue.SingleOrDefault(item => item.Id == reviewItemId);
        if (review is not null)
        {
            review.HeldFilePath = destinationPath;
            review.KeptIncomingArchivedPath = destinationPath;
        }
    }

    /// <summary>Closes a Keep incoming decision whose two halves have both committed.</summary>
    private static void CompleteKeepIncoming(ReviewItem review)
    {
        review.Status = ReviewStatus.Resolved;
        review.HeldFilePath = string.Empty;
        review.KeptIncomingArchivedPath = null;
    }

    private static void ResolveReview(AppStateDocument state, Guid? reviewItemId)
    {
        if (reviewItemId is null)
        {
            return;
        }

        var review = state.ReviewQueue.SingleOrDefault(item => item.Id == reviewItemId);
        if (review is not null)
        {
            review.Status = ReviewStatus.Resolved;
            review.HeldFilePath = string.Empty;
            review.KeptIncomingArchivedPath = null;
        }
    }

    private ActivityEntry CreateActivity(JournalEntry entry) =>
        new()
        {
            Id = Guid.NewGuid(),
            OccurredUtc = _timeProvider.GetUtcNow(),
            Kind = entry.Purpose switch
            {
                JournalOperationPurpose.MoveUnique => ActivityKind.AutomaticMove,
                JournalOperationPurpose.FileAnimatedSheet => ActivityKind.AutomaticMove,
                JournalOperationPurpose.DeleteArchiveCandidate => ActivityKind.DeletionRequested,
                JournalOperationPurpose.AutoKeepArchived => ActivityKind.DeletionRequested,
                JournalOperationPurpose.AutoKeepHeld => ActivityKind.DeletionRequested,
                JournalOperationPurpose.RemoveArchiveDuplicate => ActivityKind.DeletionRequested,
                _ => ActivityKind.ReviewDecision,
            },
            Level = ActivityLevel.Information,
            Category = entry.Category,
            Message = entry.Purpose switch
            {
                JournalOperationPurpose.AutoKeepArchived =>
                    "Exact duplicate: kept the archived image and recycled the incoming copy.",
                JournalOperationPurpose.AutoKeepHeld =>
                    "Exact duplicate of an image already waiting in Review: recycled the extra copy.",
                JournalOperationPurpose.RemoveArchiveDuplicate =>
                    "The archive held this picture twice: recycled the extra copy.",
                _ => entry.Purpose.ToString(),
            },
            SourcePath = entry.SourcePath,
            DestinationPath = entry.DestinationPath,
            OperationId = entry.Id,
        };

    private async Task<string> BuildArchiveDestinationAsync(
        string sourcePath,
        VrcImageCategory category,
        ScanRoutingContext? routingContext,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mapping = state.Settings.CategoryMappings.Single(item => item.Category == category);
        if (!Directory.Exists(mapping.ArchivePath))
        {
            throw new DirectoryNotFoundException($"The {category} output folder is unavailable.");
        }

        if (routingContext is null
            || string.IsNullOrWhiteSpace(routingContext.SourceRootPath)
            || string.IsNullOrWhiteSpace(routingContext.OutputRootPath))
        {
            routingContext = new ScanRoutingContext
            {
                SourceRootPath = mapping.SourcePath,
                RelativeDirectory = GetRelativeDirectory(mapping.SourcePath, sourcePath),
                OutputRootPath = state.Settings.OutputRootPath,
            };
        }

        // Where an image is filed is decided by the settings as they stand now, not by the ones the
        // scan happened to see. A review can sit in the queue across a change of output folder, and
        // its stored context still names the old one; honouring that would file the image into the
        // folder the person has just stopped using, and the availability check above - which reads
        // the current mapping - would be guarding a folder nothing was written to. The context is
        // still what says which subfolder the image came from, which does not go stale.
        var outputRoot = PathBoundary.Normalize(state.Settings.OutputRootPath);
        var categoryRoot = Path.Combine(outputRoot, category.ToString());

        // Until now the incoming folder structure was always copied across, which meant VRCX's
        // dated folders were reproduced in the archive whether or not anyone wanted them. The
        // setting that was supposed to decide this existed but was never read.
        var relativeDirectory = state.Settings.OrganizationPolicy switch
        {
            OrganizationPolicy.CategoryRoot => string.Empty,
            OrganizationPolicy.CategoryYearMonth => YearMonthFolder(sourcePath),
            _ => routingContext.RelativeDirectory,
        };
        if (Path.IsPathRooted(relativeDirectory)
            || relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".."))
        {
            throw new InvalidOperationException("The stored relative image path is unsafe.");
        }

        // A file that is already an animation belongs with the animations, not among the stills.
        // VRCX hands over ready-made GIFs for some emoji, and filing those in the category root put
        // a moving picture in the middle of a folder of frames - and somewhere the app would never
        // look for the animation of a sheet it holds.
        var filingRoot = AtlasAnimationWriter.IsAnimation(sourcePath)
            ? Path.Combine(categoryRoot, AtlasAnimationWriter.AnimationFolderName)
            : categoryRoot;
        var destinationFolder = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
            ? filingRoot
            : Path.Combine(filingRoot, relativeDirectory);
        PathBoundary.EnsureContained(categoryRoot, destinationFolder, "Image destination");
        PathBoundary.EnsureContained(outputRoot, destinationFolder, "Image destination");
        PathBoundary.EnsureNoReparsePoints(destinationFolder, "Image destination");

        return CreateCollisionSafePath(Path.Combine(destinationFolder, Path.GetFileName(sourcePath)));
    }

    /// <summary>
    /// The month the image was written, taken from the file rather than the clock so that a
    /// re-scan of old images files them where they belong instead of under today.
    /// </summary>
    private static string YearMonthFolder(string sourcePath)
    {
        DateTime written;
        try
        {
            var info = new FileInfo(sourcePath);
            written = info.Exists ? info.LastWriteTime : DateTime.Now;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            written = DateTime.Now;
        }

        return written.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    private static string GetRelativeDirectory(string sourceRoot, string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The source file has no parent folder.");
        PathBoundary.EnsureContained(sourceRoot, sourcePath, "Source image");
        var relative = Path.GetRelativePath(PathBoundary.Normalize(sourceRoot), directory);
        return relative == "." ? string.Empty : relative;
    }

    private JournalEntry CreateMoveEntry(
        string source,
        string destination,
        VrcImageCategory category,
        string fingerprint,
        JournalOperationPurpose purpose) =>
        new()
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            OperationType = JournalOperationType.Move,
            Category = category,
            SourcePath = source,
            DestinationPath = destination,
            ExpectedSource = CreateExpectedIdentity(source, fingerprint),
        };

    private JournalEntry CreateRecycleEntry(
        string source,
        VrcImageCategory category,
        string fingerprint,
        JournalOperationPurpose purpose) =>
        new()
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            OperationType = JournalOperationType.Recycle,
            Category = category,
            SourcePath = source,
            ExpectedSource = CreateExpectedIdentity(source, fingerprint),
        };

    private static ExpectedFileIdentity CreateExpectedIdentity(string path, string fingerprint)
    {
        var info = new FileInfo(path);
        return new ExpectedFileIdentity
        {
            Fingerprint = fingerprint,
            FileSize = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
        };
    }

    private static IndexedImageRecord CreateIndexedRecord(
        VrcImageCategory category,
        string destination,
        string source,
        ImageFingerprint fingerprint)
    {
        var info = new FileInfo(source);
        return new IndexedImageRecord
        {
            Id = Guid.NewGuid(),
            Category = category,
            Path = destination,
            FileSize = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Width = fingerprint.Width,
            Height = fingerprint.Height,
            ExactFingerprint = fingerprint.ExactIdentity,
            PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
            Fingerprint = fingerprint,
        };
    }

    private static string CreateCollisionSafePath(string requestedPath)
    {
        if (!File.Exists(requestedPath))
        {
            return requestedPath;
        }

        var directory = Path.GetDirectoryName(requestedPath)!;
        var name = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Puts the fingerprint back on a journal entry that came off disk, by reading it from the file
    /// the entry has already moved into place.
    /// </summary>
    /// <remarks>
    /// Fingerprints are deliberately not persisted on journal entries, so <see cref="CommitMutationAsync"/>
    /// carries the one held in memory. After a crash there is no in-memory entry to carry from: the
    /// record would be committed without a fingerprint, and the next load would notice the gap and
    /// mark the whole category stale, re-fingerprinting an entire archive because of one interrupted
    /// move. Re-reading the single file that moved costs one decode and settles it.
    /// A failure here is not fatal - the entry commits without the fingerprint exactly as it did
    /// before, and the index rebuild that follows is correct, just slow.
    /// </remarks>
    private async Task RestoreCarriedFingerprintAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (entry.IndexedImageAfterCommit is not { Fingerprint: null } record
            || entry.DestinationPath is not { } destination)
        {
            return;
        }

        try
        {
            var decoded = await _decoder.DecodeAsync(destination, cancellationToken).ConfigureAwait(false);
            if (decoded.IsSuccess)
            {
                record.Fingerprint = ImageFingerprint.Create(decoded.Image!);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Left for the index rebuild to sort out.
        }
    }

    private async Task<JournalFileObservation> ObserveAsync(
        JournalEntry entry,
        CancellationToken cancellationToken)
    {
        var source = await ObservePathAsync(entry.SourcePath, entry.ExpectedSource.Fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (entry.OperationType == JournalOperationType.Recycle)
        {
            return new JournalFileObservation(source);
        }

        var destination = await ObservePathAsync(
                entry.DestinationPath!,
                entry.ExpectedSource.Fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        return new JournalFileObservation(source, destination);
    }

    private async Task<JournalPathState> ObservePathAsync(
        string path,
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return JournalPathState.Missing;
        }

        var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess)
        {
            return JournalPathState.DifferentFile;
        }

        return ImageFingerprint.Create(decoded.Image!).ExactIdentity == expectedFingerprint
            ? JournalPathState.ExpectedFile
            : JournalPathState.DifferentFile;
    }

    private Task MarkNeedsAttentionAsync(Guid operationId, string error, CancellationToken cancellationToken) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var entry = state.OperationJournal.Single(item => item.Id == operationId);
                entry.Phase = JournalPhase.NeedsAttention;
                entry.UpdatedUtc = _timeProvider.GetUtcNow();
                entry.LastError = error;
                return true;
            },
            cancellationToken);
}
