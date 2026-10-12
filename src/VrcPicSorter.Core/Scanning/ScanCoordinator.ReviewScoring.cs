using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public sealed partial class ScanCoordinator
{
    // Release metadata only, then use the normal scan pipeline to recompute against the full
    // current archive and perform verified, journaled routing. A crash here leaves the file intact.
    private async Task ReleaseQueuedReviewsOutsideCustomLimitsAsync(
        VrcImageCategory category, HashSet<string> requestedPaths, List<string> errors,
        CancellationToken cancellationToken)
    {
        var snapshot = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Settings.CustomSimilarityThresholds is not { } limits) return;
        limits.Validate();
        var index = snapshot.ArchiveIndex.Categories.Single(item => item.Category == category);
        if (index.Status != IndexStatus.Current) return;
        foreach (var review in snapshot.ReviewQueue.Where(item => item.Category == category
                     && requestedPaths.Contains(PathBoundary.Normalize(item.IncomingOriginalPath))
                     && item.ScoringVersion == ImageMatcher.CurrentScoringVersion
                     && !item.AwaitingDecisionAfterScoringRefresh && CanRefreshQueuedReview(snapshot, item)
                     && item.Candidates.Any(candidate => candidate.SimilarityScore < limits.MinimumPercent / 100
                         || candidate.SimilarityScore >= limits.MaximumPercent / 100)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(await _router.GetReviewDismissalEligibilityAsync(review.Id, cancellationToken).ConfigureAwait(false)).CanDismiss)
                continue;
            var read = await PrepareImageAsync(review.HeldFilePath, cancellationToken).ConfigureAwait(false);
            if (read.Fingerprint is not { } incoming || incoming.ExactIdentity != review.IncomingFingerprint)
            {
                errors.Add($"{review.HeldFilePath}: custom limits were not applied because the queued incoming image changed or could not be read ({read.Error ?? "identity changed"}).");
                continue;
            }
            var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var saved = current.ReviewQueue.SingleOrDefault(item => item.Id == review.Id);
            if (saved is null || !CanRefreshQueuedReview(current, saved)
                || saved.ScoringVersion != ImageMatcher.CurrentScoringVersion || saved.AwaitingDecisionAfterScoringRefresh
                || !Equals(current.Settings.CustomSimilarityThresholds, limits)
                || saved.IncomingFingerprint != review.IncomingFingerprint || saved.HeldFilePath != review.HeldFilePath
                || saved.Candidates.Count != review.Candidates.Count
                || saved.Candidates.Any(candidate => !review.Candidates.Any(old => old.Id == candidate.Id
                    && old.IndexedImageId == candidate.IndexedImageId && old.ArchivePath == candidate.ArchivePath
                    && old.ExpectedFingerprint == candidate.ExpectedFingerprint))
                || current.ArchiveIndex.Categories.Single(item => item.Category == category).Generation != index.Generation)
                continue;
            await _router.DismissReviewAsync(review.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    // Called under the scan gate, after archive indexing. An upgrade changes suggestions only;
    // reranking the displayed set also prevents rediscovering cards a person already handled.
    private async Task RefreshQueuedScoresAsync(
        VrcImageCategory category,
        HashSet<string> requestedPaths,
        bool reconcileAllPending,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var snapshot = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = snapshot.ArchiveIndex.Categories.Single(item => item.Category == category);
        if (index.Status != IndexStatus.Current) return;

        foreach (var review in snapshot.ReviewQueue.Where(item => item.Category == category
                     && (reconcileAllPending || requestedPaths.Contains(PathBoundary.Normalize(item.IncomingOriginalPath)))
                     && CanRefreshQueuedScore(snapshot, item)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparable = new Dictionary<string, IndexedImageRecord>(StringComparer.Ordinal);
            foreach (var candidate in review.Candidates)
            {
                var indexed = index.Images.FirstOrDefault(image => image.Id == candidate.IndexedImageId
                    && string.Equals(image.Path, candidate.ArchivePath, StringComparison.OrdinalIgnoreCase)
                    && image.ExactFingerprint == candidate.ExpectedFingerprint && image.Fingerprint is not null);
                if (indexed is not null) comparable[candidate.Id.ToString("N")] = indexed;
            }
            if (comparable.Count != review.Candidates.Count) continue;

            var read = await PrepareImageAsync(review.HeldFilePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Fingerprint is not { } incoming || incoming.ExactIdentity != review.IncomingFingerprint)
            {
                errors.Add($"{review.HeldFilePath}: queued match scores were not refreshed because the incoming image changed or could not be read ({read.Error ?? "identity changed"}).");
                continue;
            }

            var matches = ImageMatcher.RankCandidates(incoming,
                comparable.Select(pair => new ImageCandidate(pair.Key, pair.Value.Fingerprint!)),
                snapshot.Settings.SimilarityProfile, snapshot.Settings.CustomSimilarityThresholds?.MinimumPercent / 100);
            if (snapshot.Settings.CustomSimilarityThresholds is null)
                matches = WithSameEmojiAnimations(matches, comparable, review.HeldFilePath, incoming);
            await _stateStore.UpdateAsync(current =>
            {
                var saved = current.ReviewQueue.SingleOrDefault(item => item.Id == review.Id);
                var currentIndex = current.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (saved is null || !CanRefreshQueuedScore(current, saved)
                    || currentIndex.Generation != index.Generation
                    || saved.IncomingFingerprint != review.IncomingFingerprint
                    || saved.HeldFilePath != review.HeldFilePath
                    || saved.Candidates.Count != review.Candidates.Count
                    || saved.Candidates.Any(candidate => !review.Candidates.Any(old => old.Id == candidate.Id
                        && old.IndexedImageId == candidate.IndexedImageId && old.ArchivePath == candidate.ArchivePath
                        && old.ExpectedFingerprint == candidate.ExpectedFingerprint))) return false;

                var candidates = saved.Candidates.ToDictionary(candidate => candidate.Id.ToString("N"));
                saved.Candidates = matches.Select(match =>
                {
                    var candidate = candidates[match.CandidateKey];
                    candidate.MatchKind = match.MatchKind;
                    candidate.SimilarityScore = match.SimilarityScore;
                    candidate.MatchReasons = match.MatchReasons.ToList();
                    return candidate;
                }).ToList();
                saved.IncomingImageFingerprint = incoming;
                saved.IndexGeneration = currentIndex.Generation;
                saved.ScoringVersion = ImageMatcher.CurrentScoringVersion;
                saved.AwaitingDecisionAfterScoringRefresh = saved.Candidates.Count == 0;
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool CanRefreshQueuedScore(AppStateDocument state, ReviewItem review) =>
        review.ScoringVersion < ImageMatcher.CurrentScoringVersion && CanRefreshQueuedReview(state, review);

    private static bool CanRefreshQueuedReview(AppStateDocument state, ReviewItem review) =>
        review.FilterReview is null
        && review.Status == ReviewStatus.Pending && review.IsIncomingInPlace
        && review.Candidates.Count > 0 && string.IsNullOrWhiteSpace(review.KeptIncomingArchivedPath)
        && !review.Candidates.Any(candidate => candidate.IsSelected || candidate.IsStale
            || candidate.MatchKind == MatchKind.Exact || candidate.ExpectedFingerprint == review.IncomingFingerprint)
        && !state.ReviewDecisions.Any(group => group.ReviewId == review.Id
            || group.Files.Any(file => ReferencesFile(review, file.Path)))
        && !state.OperationJournal.Any(entry => entry.ReviewItemId == review.Id || ReferencesFile(review, entry.SourcePath))
        && !state.ManualKeeps.Any(keep => ReferencesFile(review, keep.Path));

    private static bool ReferencesFile(ReviewItem review, string path) =>
        string.Equals(review.HeldFilePath, path, StringComparison.OrdinalIgnoreCase)
        || review.Candidates.Any(candidate => string.Equals(candidate.ArchivePath, path, StringComparison.OrdinalIgnoreCase));
}
