using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.FileSystem;

public sealed partial class FileRouter
{
    private readonly SemaphoreSlim _groupGate = new(1, 1);

    public async Task<ReviewDecisionResult> ApplyReviewDecisionAsync(ReviewDecisionRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.ReviewDecisions.Any(g => g.ReviewId == request.ReviewId))
                throw new InvalidOperationException("A decision is already pending. Use Retry remaining.");
            var review = ValidateDecisionSnapshot(state, request);
            if (state.ReviewDecisions.Any(g => g.Files.Any(saved => request.Files.Any(file => PathsMatch(saved.Path, file.Path)))))
                throw new InvalidOperationException("An overlapping review has a pending decision. Finish it before applying this group.");
            foreach (var file in request.Files.Where(f => f.Action != ReviewFileAction.Unchanged))
            {
                await VerifyImageFingerprintAsync(file.Path, file.Fingerprint, "A selected image changed or is unavailable. Refresh the review.", cancellationToken).ConfigureAwait(false);
                if (file.Action == ReviewFileAction.Remove && !_recycleBin.CanRecycle(file.Path))
                    throw new NotSupportedException("A selected file cannot be recycled. Nothing was removed.");
            }

            var group = new ReviewGroupDecision
            {
                ReviewId = review.Id,
                Category = review.Category,
                Files = request.Files.ToList(),
                DeleteIncomingOnly = request.DeleteIncomingOnly,
                MatchOnlyId = request.MatchOnlyId,
            };
            var incoming = group.Files.Single(f => f.CandidateId is null);
            if (incoming.Action == ReviewFileAction.Keep && !PathsMatch(incoming.Path, review.KeptIncomingArchivedPath ?? string.Empty))
            {
                var fingerprint = review.IncomingImageFingerprint
                    ?? throw new InvalidOperationException("Incoming image metadata is unavailable. Rescan before keeping it.");
                var destination = await BuildArchiveDestinationAsync(incoming.Path, review.Category, review.RoutingContext, cancellationToken).ConfigureAwait(false);
                var entry = CreateMoveEntry(incoming.Path, destination, review.Category, incoming.Fingerprint, JournalOperationPurpose.GroupArchiveIncoming);
                entry.IndexedImageAfterCommit = CreateIndexedRecord(review.Category, destination, incoming.Path, fingerprint);
                entry.RoutingContext = review.RoutingContext;
                AddGroupStep(group, entry);
            }
            foreach (var file in group.Files.Where(f => f.Action == ReviewFileAction.Remove).OrderBy(f => f.CandidateId is null))
            {
                var entry = CreateRecycleEntry(file.Path, review.Category, file.Fingerprint, JournalOperationPurpose.GroupRecycleFile);
                entry.IndexedImageId = file.CandidateId is { } candidateId
                    ? review.Candidates.Single(c => c.Id == candidateId).IndexedImageId : null;
                AddGroupStep(group, entry);
            }
            await _stateStore.UpdateAsync(current =>
            {
                ValidateDecisionSnapshot(current, request);
                if (current.ReviewDecisions.Any(g => g.ReviewId == request.ReviewId))
                    throw new InvalidOperationException("This review already has a pending decision.");
                current.ReviewDecisions.Add(group);
                foreach (var keep in group.Files.Where(f => f.Action == ReviewFileAction.Keep)) Protect(current, keep.Path, keep.Fingerprint);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return await ExecuteGroupAsync(group.Id, cancellationToken).ConfigureAwait(false);
        }
        finally { _groupGate.Release(); }
    }

    public async Task<ReviewDecisionResult> RetryReviewDecisionAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var group = state.ReviewDecisions.SingleOrDefault(g => g.ReviewId == reviewId)
                ?? throw new InvalidOperationException("No pending group decision remains. Refresh the review.");
            return await ExecuteGroupAsync(group.Id, cancellationToken).ConfigureAwait(false);
        }
        finally { _groupGate.Release(); }
    }

    public async Task CancelReviewDecisionAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var group = state.ReviewDecisions.SingleOrDefault(g => g.ReviewId == reviewId)
                ?? throw new InvalidOperationException("No pending decision remains. Refresh the review.");
            var applied = new List<JournalEntry>();
            // Observe everything first. Cancellation never executes a file side effect.
            foreach (var step in group.Steps.Where(s => !s.Completed))
            {
                var entry = state.OperationJournal.SingleOrDefault(e => e.Id == step.Operation.Id);
                if (entry is null) continue;
                var observation = await ObserveAsync(entry, cancellationToken).ConfigureAwait(false);
                var decision = OperationJournal.Reconcile(entry, observation);
                if (decision.Action is JournalReconciliationAction.CommitState or JournalReconciliationAction.MarkCompleted)
                    applied.Add(entry);
                else if (decision.Action != JournalReconciliationAction.RetrySideEffect)
                    throw new InvalidOperationException("The interrupted operation cannot yet be verified. Restore access and retry cancellation. " + decision.Reason);
            }
            foreach (var entry in applied)
            {
                await RestoreCarriedFingerprintAsync(entry, cancellationToken).ConfigureAwait(false);
                await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            group = state.ReviewDecisions.Single(g => g.Id == group.Id);
            var incoming = group.Files.Single(f => f.CandidateId is null);
            var incomingRemoved = group.Steps.Any(s => s.Completed
                && s.Operation.Purpose == JournalOperationPurpose.GroupRecycleFile
                && PathsMatch(s.Operation.SourcePath, incoming.Path));
            ImageFingerprint? fingerprint = null;
            var candidateStates = new Dictionary<Guid, bool>();
            var refreshedCandidates = new Dictionary<Guid, string>();
            var absentCandidates = new HashSet<Guid>();
            var pendingReview = state.ReviewQueue.SingleOrDefault(r => r.Id == reviewId);
            if (pendingReview is not null)
                foreach (var candidate in pendingReview.Candidates)
                {
                    var observation = await ObservePathAsync(candidate.ArchivePath, candidate.ExpectedFingerprint, cancellationToken).ConfigureAwait(false);
                    candidateStates[candidate.Id] = observation != JournalPathState.ExpectedFile;
                    if (observation == JournalPathState.Missing) absentCandidates.Add(candidate.Id);
                    if (observation == JournalPathState.DifferentFile)
                    {
                        try
                        {
                            var decoded = await _decoder.DecodeAsync(candidate.ArchivePath, cancellationToken).ConfigureAwait(false);
                            if (decoded.IsSuccess)
                            {
                                refreshedCandidates[candidate.Id] = ImageFingerprint.Create(decoded.Image!).ExactIdentity;
                                candidateStates[candidate.Id] = false;
                            }
                            else absentCandidates.Add(candidate.Id);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
                    }
                }
            if (!incomingRemoved)
            {
                try
                {
                    var decoded = await _decoder.DecodeAsync(incoming.Path, cancellationToken).ConfigureAwait(false);
                    if (decoded.IsSuccess) fingerprint = ImageFingerprint.Create(decoded.Image!);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            await _stateStore.UpdateAsync(current =>
            {
                var saved = current.ReviewDecisions.Single(g => g.Id == group.Id);
                var review = current.ReviewQueue.SingleOrDefault(r => r.Id == reviewId);
                if (review is not null)
                {
                    if (incomingRemoved) ResolveReview(current, reviewId);
                    else
                    {
                        review.HeldFilePath = incoming.Path;
                        review.Status = fingerprint is null ? ReviewStatus.NeedsReconciliation : ReviewStatus.Pending;
                        if (fingerprint is not null)
                        {
                            review.IncomingFingerprint = fingerprint.ExactIdentity;
                            review.IncomingImageFingerprint = fingerprint;
                        }
                        review.Candidates.RemoveAll(c => absentCandidates.Contains(c.Id));
                        foreach (var candidate in review.Candidates)
                        {
                            candidate.IsStale = candidateStates.GetValueOrDefault(candidate.Id, true);
                            if (refreshedCandidates.TryGetValue(candidate.Id, out var identity))
                            {
                                candidate.ExpectedFingerprint = identity;
                                candidate.IndexedImageId = Guid.Empty;
                                candidate.SimilarityScore = 0;
                                candidate.MatchKind = MatchKind.Similar;
                                candidate.MatchReasons = ["File changed since the previous decision. Compare it again before deciding."];
                            }
                        }
                    }
                }
                current.OperationJournal.RemoveAll(e => e.GroupDecisionId == saved.Id);
                current.ReviewDecisions.Remove(saved);
                current.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = _timeProvider.GetUtcNow(),
                    Kind = ActivityKind.ReviewDecision,
                    Level = ActivityLevel.Information,
                    Category = saved.Category,
                    Message = "Cancelled remaining review actions; completed changes were preserved."
                });
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _groupGate.Release(); }
    }

    private static void AddGroupStep(ReviewGroupDecision group, JournalEntry entry)
    {
        entry.GroupDecisionId = group.Id;
        entry.ReviewItemId = group.ReviewId;
        group.Steps.Add(new ReviewDecisionStep { Operation = entry });
    }

    private static ReviewItem ValidateDecisionSnapshot(AppStateDocument state, ReviewDecisionRequest request)
    {
        var review = state.ReviewQueue.SingleOrDefault(r => r.Id == request.ReviewId && r.Status != ReviewStatus.Resolved)
            ?? throw new InvalidOperationException("This review was already resolved. Refresh the queue.");
        if (review.FilterReview is not null)
            throw new InvalidOperationException("Use Keep or Remove to resolve this filter review.");
        var incoming = request.Files.Single(f => f.CandidateId is null);
        if (!PathsMatch(incoming.Path, review.HeldFilePath) || incoming.Fingerprint != review.IncomingFingerprint
            || request.Files.Count != review.Candidates.Count + 1)
            throw new InvalidOperationException("The review changed. Refresh before applying the decision.");
        foreach (var file in request.Files.Where(f => f.CandidateId is not null))
        {
            var candidate = review.Candidates.SingleOrDefault(c => c.Id == file.CandidateId);
            if (candidate is null || !PathsMatch(file.Path, candidate.ArchivePath) || file.Fingerprint != candidate.ExpectedFingerprint
                || (file.Action != ReviewFileAction.Unchanged && candidate.IsStale))
                throw new InvalidOperationException("An archive match changed. Refresh before applying the decision.");
        }
        return review;
    }

    private async Task<ReviewDecisionResult> ExecuteGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var group = state.ReviewDecisions.Single(g => g.Id == groupId);
                var step = group.Steps.FirstOrDefault(s => !s.Completed);
                if (step is null)
                {
                    foreach (var keep in group.Files.Where(f => f.Action == ReviewFileAction.Keep))
                        await VerifyImageFingerprintAsync(keep.Path, keep.Fingerprint, "A selected keeper is no longer available.", cancellationToken).ConfigureAwait(false);
                    var resolved = group.Files.Single(f => f.CandidateId is null).Action != ReviewFileAction.Unchanged;
                    await _stateStore.UpdateAsync(current =>
                    {
                        var saved = current.ReviewDecisions.Single(g => g.Id == groupId);
                        if (saved.Steps.Any(s => !s.Completed)) throw new InvalidOperationException("The group is not complete.");
                        if (saved.MatchOnlyId is { } matchId)
                            current.ReviewQueue.Single(r => r.Id == saved.ReviewId).Candidates.RemoveAll(c => c.Id == matchId);
                        if (resolved) ResolveReview(current, group.ReviewId);
                        current.ReviewDecisions.Remove(saved);
                        current.History.Add(new ActivityEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredUtc = _timeProvider.GetUtcNow(),
                            Kind = ActivityKind.ReviewDecision,
                            Level = ActivityLevel.Information,
                            Category = group.Category,
                            Message = new ReviewDecisionRequest(group.ReviewId, group.Files, group.DeleteIncomingOnly).Summary
                        });
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                    return new(resolved, group.Steps.Count, 0);
                }

                var entry = state.OperationJournal.SingleOrDefault(e => e.Id == step.Operation.Id);
                if (entry is null)
                {
                    await VerifyGroupBeforeEffectAsync(step.Operation, cancellationToken).ConfigureAwait(false);
                    if (step.Operation.OperationType == JournalOperationType.Move)
                        await ExecuteMoveAsync(step.Operation, cancellationToken).ConfigureAwait(false);
                    else await ExecuteRecycleAsync(step.Operation, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var recovery = OperationJournal.Reconcile(entry, await ObserveAsync(entry, cancellationToken).ConfigureAwait(false));
                switch (recovery.Action)
                {
                    case JournalReconciliationAction.RetrySideEffect:
                        await VerifyGroupBeforeEffectAsync(entry, cancellationToken).ConfigureAwait(false);
                        await VerifyExpectedSourceAsync(entry, cancellationToken).ConfigureAwait(false);
                        if (entry.Phase is JournalPhase.IntentRecorded or JournalPhase.NeedsAttention)
                            await _journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await ApplySideEffectAsync(entry, cancellationToken).ConfigureAwait(false);
                        await RestoreCarriedFingerprintAsync(entry, cancellationToken).ConfigureAwait(false);
                        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
                        break;
                    case JournalReconciliationAction.CommitState:
                    case JournalReconciliationAction.MarkCompleted:
                        await RestoreCarriedFingerprintAsync(entry, cancellationToken).ConfigureAwait(false);
                        await CommitMutationAsync(entry, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException(recovery.Reason);
                }
            }
        }
        catch (Exception error)
        {
            // Preserve the original failure/cancellation even if recording its explanation fails.
            try
            {
                await _stateStore.UpdateAsync(state =>
                {
                    var group = state.ReviewDecisions.SingleOrDefault(g => g.Id == groupId);
                    if (group is null) return false;
                    group.LastError = error is OperationCanceledException ? "Stopped. Retry remaining when ready." : error.Message;
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task VerifyGroupBeforeEffectAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (entry.GroupDecisionId is not { } id) return;
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var group = state.ReviewDecisions.SingleOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("The saved group decision is missing. No file was changed.");
        var next = group.Steps.FirstOrDefault(s => !s.Completed);
        if (next?.Operation.Id != entry.Id) throw new InvalidOperationException("The group operation is out of order.");
        if (group.MatchOnlyId is not null)
        {
            var incoming = group.Files.Single(f => f.CandidateId is null);
            await VerifyImageFingerprintAsync(incoming.Path, incoming.Fingerprint,
                "The incoming image changed or is unavailable. The archive match was not removed.", cancellationToken).ConfigureAwait(false);
        }
        foreach (var keep in group.Files.Where(f => f.Action == ReviewFileAction.Keep))
            await VerifyImageFingerprintAsync(keep.Path, keep.Fingerprint, "A selected keeper changed or is unavailable. No further files were removed.", cancellationToken).ConfigureAwait(false);
    }

    private static void Protect(AppStateDocument state, string path, string fingerprint)
    {
        var info = new FileInfo(path);
        state.ManualKeeps.RemoveAll(p => PathsMatch(p.Path, path));
        state.ManualKeeps.Add(new(path, fingerprint, info.Length, info.LastWriteTimeUtc));
    }

    private static void ApplyGroupMutation(AppStateDocument state, JournalEntry entry)
    {
        var group = state.ReviewDecisions.Single(g => g.Id == entry.GroupDecisionId);
        var step = group.Steps.Single(s => s.Operation.Id == entry.Id);
        if (step.Completed) return;
        var index = state.ArchiveIndex.Categories.Single(c => c.Category == entry.Category);
        if (entry.Purpose == JournalOperationPurpose.GroupArchiveIncoming)
        {
            if (!index.Images.Any(i => i.Id == entry.IndexedImageAfterCommit!.Id)) index.Images.Add(entry.IndexedImageAfterCommit!);
            RepointReview(state, group.ReviewId, entry.DestinationPath);
            ManualKeepRules.Repoint(state, entry.SourcePath, entry.DestinationPath!);
            var incoming = group.Files.FindIndex(f => f.CandidateId is null);
            group.Files[incoming] = group.Files[incoming] with { Path = entry.DestinationPath! };
        }
        else if (entry.Purpose == JournalOperationPurpose.GroupRecycleFile)
        {
            index.Images.RemoveAll(i => i.Id == entry.IndexedImageId || PathsMatch(i.Path, entry.SourcePath));
            foreach (var review in state.ReviewQueue)
            {
                review.Candidates.RemoveAll(c => PathsMatch(c.ArchivePath, entry.SourcePath));
                if (review.Id != group.ReviewId && PathsMatch(review.HeldFilePath, entry.SourcePath)) review.Status = ReviewStatus.NeedsReconciliation;
            }
            state.ManualKeeps.RemoveAll(p => PathsMatch(p.Path, entry.SourcePath));
            if (AtlasAnimationWriter.IsAnimation(entry.SourcePath) && ManualKeepRules.AnimationKey(entry.SourcePath) is { } key
                && !state.SuppressedAnimations.Contains(key, StringComparer.OrdinalIgnoreCase)) state.SuppressedAnimations.Add(key);
        }
        else throw new InvalidOperationException("Unsupported group operation.");
        index.Generation++;
        step.Completed = true;
        group.LastError = null;
    }
}
