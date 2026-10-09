using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

public sealed partial class FileRouter
{
    public sealed record ReviewDismissalEligibility(bool CanDismiss, string Reason);

    public async Task<ReviewDismissalEligibility> GetReviewDismissalEligibilityAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return ReviewDismissalEligibilityFor(state, reviewId);
    }

    private static ReviewDismissalEligibility ReviewDismissalEligibilityFor(AppStateDocument state, Guid reviewId)
    {
        var review = state.ReviewQueue.SingleOrDefault(r => r.Id == reviewId);
        if (review is null) return new(false, "Review is no longer queued.");
        return ReviewDismissalEligibilityFor(state, review);
    }

    private static ReviewDismissalEligibility ReviewDismissalEligibilityFor(AppStateDocument state, ReviewItem review)
    {
        var reviewId = review.Id;
        var paths = ReviewPaths(review).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        bool Owns(string? path) => !string.IsNullOrWhiteSpace(path) && paths.Any(p => PathsMatch(p, path));
        bool JournalOwns(JournalEntry entry) => entry.ReviewItemId == reviewId
            || Owns(entry.SourcePath) || Owns(entry.DestinationPath) || Owns(entry.SurvivingPath)
            || Owns(entry.IndexedImageAfterCommit?.Path)
            || entry.ReviewItemAfterCommit is { } payload
                && (payload.Id == reviewId || ReviewPaths(payload).Any(Owns));
        if (state.OperationJournal.Any(JournalOwns)
            || state.ReviewDecisions.Any(g => g.ReviewId == reviewId || g.Files.Any(f => Owns(f.Path))
                || g.Steps.Any(step => JournalOwns(step.Operation)))
            || state.RelocationJournal.Any(r => Owns(r.SourcePath) || Owns(r.DestinationPath)))
            return new(false, "Pending recovery or review actions own this review's paths.");
        if (review.FilterReview is not null)
        {
            try
            {
                PathBoundary.EnsureNoReparsePoints(review.HeldFilePath, "Filter review");
                if (!PathBoundary.IsConfirmedMissingPath(review.HeldFilePath))
                    return new(false, "File is present, changed, or its parent folder is unavailable. Keep or Remove requires an unchanged file.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                return new(false, $"File availability cannot be confirmed: {e.Message}");
            }
        }
        return new(true, "Remove review metadata without changing files.");
    }

    private static IEnumerable<string?> ReviewPaths(ReviewItem review) =>
        new[] { review.HeldFilePath, review.IncomingOriginalPath, review.KeptIncomingArchivedPath }
            .Concat(review.Candidates.Select(c => c.ArchivePath));
    private static void EnsureReviewCanBeDismissed(AppStateDocument state, Guid reviewId)
    {
        var eligibility = ReviewDismissalEligibilityFor(state, reviewId);
        if (!eligibility.CanDismiss) throw new InvalidOperationException(eligibility.Reason);

    }

    public async Task RestorePendingReviewVisibilityAsync(CancellationToken cancellationToken = default)
    {
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stateStore.UpdateAsync(state =>
            {
                var changed = false;
                foreach (var group in state.ReviewDecisions)
                {
                    var incoming = group.Files.Single(f => f.CandidateId is null);
                    var review = state.ReviewQueue.SingleOrDefault(r => r.Id == group.ReviewId);
                    if (review is not null && review.Status != ReviewStatus.Resolved && !string.IsNullOrWhiteSpace(review.HeldFilePath)) continue;
                    if (review is null)
                    {
                        review = new ReviewItem
                        {
                            Id = group.ReviewId,
                            Category = group.Category,
                            IncomingOriginalPath = incoming.Path,
                            CreatedUtc = _timeProvider.GetUtcNow()
                        };
                        review.Candidates = group.Files.Where(f => f.CandidateId is not null
                            && !group.Steps.Any(s => s.Completed && s.Operation.Purpose == JournalOperationPurpose.GroupRecycleFile
                                && PathsMatch(s.Operation.SourcePath, f.Path)))
                            .Select(f => new ReviewCandidate
                            {
                                Id = f.CandidateId!.Value,
                                ArchivePath = f.Path,
                                ExpectedFingerprint = f.Fingerprint,
                                IsStale = true
                            }).ToList();
                        state.ReviewQueue.Add(review);
                    }
                    review.Status = ReviewStatus.NeedsReconciliation;
                    review.HeldFilePath = incoming.Path;
                    review.IncomingFingerprint = incoming.Fingerprint;
                    var archived = group.Steps.FirstOrDefault(s => s.Completed && s.Operation.Purpose == JournalOperationPurpose.GroupArchiveIncoming);
                    if (archived is not null) review.KeptIncomingArchivedPath = incoming.Path;
                    changed = true;
                }
                return changed;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _groupGate.Release(); }
    }
}
