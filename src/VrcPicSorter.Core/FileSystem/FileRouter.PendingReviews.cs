using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

public sealed partial class FileRouter
{
    private static void EnsureReviewCanBeDismissed(AppStateDocument state, Guid reviewId)
    {
        if (state.ReviewDecisions.Any(g => g.ReviewId == reviewId))
            throw new InvalidOperationException("This review has unfinished actions. Retry or cancel remaining actions first.");
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
