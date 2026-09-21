using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Storage;

public enum JournalPathState
{
    NotApplicable,
    Missing,
    ExpectedFile,
    DifferentFile,
    Unavailable,
}

public enum JournalReconciliationAction
{
    None,
    RetrySideEffect,
    CommitState,
    MarkCompleted,
    NeedsAttention,
}

public sealed record JournalFileObservation(
    JournalPathState Source,
    JournalPathState Destination = JournalPathState.NotApplicable);

public sealed record JournalReconciliationDecision(
    Guid OperationId,
    JournalReconciliationAction Action,
    string Reason);

public sealed class OperationJournal
{
    private readonly JsonStateStore _stateStore;
    private readonly TimeProvider _timeProvider;

    public OperationJournal(JsonStateStore stateStore, TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<JournalEntry> RecordIntentAsync(
        JournalEntry intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateIntent(intent);

        return _stateStore.UpdateAsync(
            state =>
            {
                if (intent.Id == Guid.Empty)
                {
                    intent.Id = Guid.NewGuid();
                }

                if (state.OperationJournal.Any(entry => entry.Id == intent.Id))
                {
                    throw new InvalidOperationException($"Journal operation {intent.Id} already exists.");
                }

                var now = _timeProvider.GetUtcNow();
                intent.Phase = JournalPhase.IntentRecorded;
                intent.CreatedUtc = now;
                intent.UpdatedUtc = now;
                intent.LastError = null;
                state.OperationJournal.Add(intent);
                return intent;
            },
            cancellationToken);
    }

    public Task<JournalEntry> AdvanceAsync(
        Guid operationId,
        JournalPhase nextPhase,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        return _stateStore.UpdateAsync(
            state =>
            {
                var entry = state.OperationJournal.SingleOrDefault(candidate => candidate.Id == operationId)
                    ?? throw new KeyNotFoundException($"Journal operation {operationId} was not found.");

                ValidateTransition(entry.Phase, nextPhase);
                entry.Phase = nextPhase;
                entry.UpdatedUtc = _timeProvider.GetUtcNow();
                entry.LastError = error;
                return entry;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<JournalReconciliationDecision>> BuildStartupPlanAsync(
        Func<JournalEntry, JournalFileObservation> observe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observe);

        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.OperationJournal
            .Where(entry => entry.Phase is not JournalPhase.Completed)
            .Select(entry => Reconcile(entry, observe(entry)))
            .ToArray();
    }

    public static JournalReconciliationDecision Reconcile(
        JournalEntry entry,
        JournalFileObservation observation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(observation);

        if (entry.Phase == JournalPhase.Completed)
        {
            return Decision(entry, JournalReconciliationAction.None, "The operation is already complete.");
        }

        if (observation.Source == JournalPathState.Unavailable || observation.Destination == JournalPathState.Unavailable)
        {
            return Decision(entry, JournalReconciliationAction.NeedsAttention,
                "Storage could not be read reliably. The operation remains pending until its paths can be verified.");
        }

        return entry.OperationType switch
        {
            JournalOperationType.Move => ReconcileMove(entry, observation),
            JournalOperationType.Recycle or JournalOperationType.DeleteExactIncoming => ReconcileRemoval(entry, observation),
            _ => Decision(entry, JournalReconciliationAction.NeedsAttention, "The operation type is unknown."),
        };
    }

    private static JournalReconciliationDecision ReconcileMove(
        JournalEntry entry,
        JournalFileObservation observation)
    {
        if (string.IsNullOrWhiteSpace(entry.DestinationPath)
            || observation.Destination == JournalPathState.NotApplicable)
        {
            return Decision(entry, JournalReconciliationAction.NeedsAttention, "A move requires a destination observation.");
        }

        if (observation.Source == JournalPathState.ExpectedFile
            && observation.Destination == JournalPathState.Missing)
        {
            return entry.Phase is JournalPhase.IntentRecorded or JournalPhase.SideEffectStarted or JournalPhase.NeedsAttention
                ? Decision(entry, JournalReconciliationAction.RetrySideEffect, "The expected source is intact and the destination is absent.")
                : Decision(entry, JournalReconciliationAction.NeedsAttention, "The journal says the move was applied, but the source is still present.");
        }

        if (observation.Source == JournalPathState.Missing
            && observation.Destination == JournalPathState.ExpectedFile)
        {
            if (entry.Phase == JournalPhase.IntentRecorded)
            {
                return Decision(
                    entry,
                    JournalReconciliationAction.NeedsAttention,
                    "The source changed before the journal recorded that the move started.");
            }

            return entry.Phase == JournalPhase.StateCommitted
                ? Decision(entry, JournalReconciliationAction.MarkCompleted, "The destination is intact and durable state was committed.")
                : Decision(entry, JournalReconciliationAction.CommitState, "The move is present on disk and durable state still needs to catch up.");
        }

        if (observation.Source == JournalPathState.ExpectedFile
            && observation.Destination == JournalPathState.ExpectedFile)
        {
            return Decision(entry, JournalReconciliationAction.NeedsAttention, "Both paths contain the expected file; automatic cleanup could cause data loss.");
        }

        return Decision(entry, JournalReconciliationAction.NeedsAttention, "The observed move paths do not prove a safe retry or completion.");
    }

    private static JournalReconciliationDecision ReconcileRemoval(
        JournalEntry entry,
        JournalFileObservation observation)
    {
        if (observation.Destination != JournalPathState.NotApplicable)
        {
            return Decision(entry, JournalReconciliationAction.NeedsAttention, "Removal operations do not have a destination path.");
        }

        if (observation.Source == JournalPathState.ExpectedFile)
        {
            return entry.Phase is JournalPhase.IntentRecorded or JournalPhase.SideEffectStarted or JournalPhase.NeedsAttention
                ? Decision(entry, JournalReconciliationAction.RetrySideEffect, "The expected source is intact; removal can be retried after verifying the retained copy.")
                : Decision(entry, JournalReconciliationAction.NeedsAttention, "The journal says removal was applied, but the source is still present.");
        }

        if (observation.Source == JournalPathState.Missing)
        {
            if (entry.Phase == JournalPhase.IntentRecorded)
            {
                return Decision(
                    entry,
                    JournalReconciliationAction.NeedsAttention,
                    "The source changed before the journal recorded that removal started.");
            }

            return entry.Phase == JournalPhase.StateCommitted
                ? Decision(entry, JournalReconciliationAction.MarkCompleted, "The source is absent and durable state was committed.")
                : Decision(entry, JournalReconciliationAction.CommitState, "The source is absent, so removal must not be repeated.");
        }

        return Decision(entry, JournalReconciliationAction.NeedsAttention, "The source path contains a different file and cannot be reconciled automatically.");
    }

    private static JournalReconciliationDecision Decision(
        JournalEntry entry,
        JournalReconciliationAction action,
        string reason) =>
        new(entry.Id, action, reason);

    private static void ValidateIntent(JournalEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourcePath);
        ArgumentNullException.ThrowIfNull(entry.ExpectedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ExpectedSource.Fingerprint);

        if (!Enum.IsDefined(entry.Purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "A recognized journal purpose is required.");
        }

        if (entry.Purpose == JournalOperationPurpose.HoldForReview
            && (entry.ReviewItemId is null || entry.ReviewItemAfterCommit is null))
        {
            throw new ArgumentException("Holding for review requires the durable review item transition.", nameof(entry));
        }

        if ((entry.Purpose is JournalOperationPurpose.DeleteArchiveCandidate
                or JournalOperationPurpose.PreserveArchiveCandidate)
            && (entry.ReviewItemId is null || entry.IndexedImageId is null))
        {
            throw new ArgumentException(
                "Deleting an archive candidate requires review and indexed-image IDs.",
                nameof(entry));
        }

        // Removing a duplicate has no review behind it, so it carries the indexed image alone -
        // but it must carry that, or the commit would recycle a file and leave its record in the
        // index pointing at nothing.
        if (entry.Purpose == JournalOperationPurpose.RemoveArchiveDuplicate && entry.IndexedImageId is null)
        {
            throw new ArgumentException(
                "Removing an archived duplicate requires the indexed-image ID.",
                nameof(entry));
        }

        // Every recycle below discards one copy of a picture because another copy exists. Naming
        // that copy is what makes the operation safe, and recording it is what keeps it safe
        // across a crash: recovery re-checks it before retrying rather than trusting a check that
        // ran before the interruption. An entry that cannot name it must never reach the journal.
        if (RequiresSurvivingCopy(entry)
            && (string.IsNullOrWhiteSpace(entry.SurvivingPath)
                || string.IsNullOrWhiteSpace(entry.SurvivingFingerprint)))
        {
            throw new ArgumentException(
                $"Recycling for {entry.Purpose} requires the retained copy's path and expected identity.",
                nameof(entry));
        }

        if (entry.OperationType == JournalOperationType.Move
            && string.IsNullOrWhiteSpace(entry.DestinationPath))
        {
            throw new ArgumentException("Move journal entries require a destination path.", nameof(entry));
        }

        if (entry.OperationType == JournalOperationType.DeleteExactIncoming
            && (entry.Purpose != JournalOperationPurpose.AutoKeepArchived
                || !string.Equals(entry.ExpectedSource.Fingerprint, entry.SurvivingFingerprint, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Permanent deletion requires an exact archived survivor.", nameof(entry));
        }

        if (entry.OperationType is JournalOperationType.Recycle or JournalOperationType.DeleteExactIncoming
            && entry.DestinationPath is not null)
        {
            throw new ArgumentException("Recycle journal entries cannot have a destination path.", nameof(entry));
        }
    }

    /// <summary>
    /// True when this operation destroys one copy of a picture only because another copy exists.
    /// </summary>
    /// <remarks>
    /// The same list the router re-checks before a destructive retry. Kept here as well so the
    /// requirement is structural: an entry without its proof cannot be recorded in the first place,
    /// and one already on disk from an older version fails into Needs attention rather than being
    /// retried on trust.
    /// </remarks>
    public static bool RequiresSurvivingCopy(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.OperationType is JournalOperationType.Recycle or JournalOperationType.DeleteExactIncoming
            && entry.Purpose is JournalOperationPurpose.AutoKeepArchived
                or JournalOperationPurpose.AutoKeepHeld
                or JournalOperationPurpose.RemoveArchiveDuplicate
                or JournalOperationPurpose.DeleteArchiveCandidate
                or JournalOperationPurpose.KeepExisting;
    }

    private static void ValidateTransition(JournalPhase current, JournalPhase next)
    {
        if (next == JournalPhase.NeedsAttention)
        {
            return;
        }

        if (current == JournalPhase.NeedsAttention && next == JournalPhase.SideEffectStarted)
        {
            return;
        }

        var expected = current switch
        {
            JournalPhase.IntentRecorded => JournalPhase.SideEffectStarted,
            JournalPhase.SideEffectStarted => JournalPhase.SideEffectApplied,
            JournalPhase.SideEffectApplied => JournalPhase.StateCommitted,
            JournalPhase.StateCommitted => JournalPhase.Completed,
            _ => throw new InvalidOperationException($"Journal phase {current} cannot advance."),
        };

        if (next != expected)
        {
            throw new InvalidOperationException(
                $"Journal phase {current} must advance to {expected}, not {next}.");
        }
    }
}
