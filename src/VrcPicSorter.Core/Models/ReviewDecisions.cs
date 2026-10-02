using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Core.Models;

public enum ReviewFileAction { Unchanged, Keep, Remove }

public sealed record ReviewFileChoice(Guid? CandidateId, string Path, string Fingerprint, ReviewFileAction Action);

public sealed record ReviewDecisionRequest(Guid ReviewId, IReadOnlyList<ReviewFileChoice> Files, bool DeleteIncomingOnly = false, Guid? MatchOnlyId = null)
{
    public string Summary => $"Keep {Files.Count(f => f.Action == ReviewFileAction.Keep)} · Recycle {Files.Count(f => f.Action == ReviewFileAction.Remove)}"
        + (Files.Any(f => f.CandidateId is null && f.Action == ReviewFileAction.Keep) ? " · Archive incoming" : string.Empty);

    public void Validate()
    {
        if (ReviewId == Guid.Empty || Files.Count == 0 || Files.Count(f => f.CandidateId is null) != 1)
            throw new InvalidOperationException("Select a current review before applying a decision.");
        if (Files.Any(f => string.IsNullOrWhiteSpace(f.Path) || string.IsNullOrWhiteSpace(f.Fingerprint)
            || !Enum.IsDefined(f.Action))) throw new InvalidOperationException("An image identity or selection is invalid. Refresh the review.");
        if (Files.Select(f => PathBoundary.Normalize(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count
            || Files.Where(f => f.CandidateId is not null).Select(f => f.CandidateId).Distinct().Count() != Files.Count - 1)
            throw new InvalidOperationException("The group contains duplicate file references. Refresh the review.");
        if (MatchOnlyId is { } matchId)
        {
            if (DeleteIncomingOnly || Files.Count(f => f.CandidateId == matchId && f.Action is ReviewFileAction.Keep or ReviewFileAction.Remove) != 1
                || Files.Any(f => f.CandidateId != matchId && f.Action != ReviewFileAction.Unchanged))
                throw new InvalidOperationException("A match action can only change the selected archive match.");
            return;
        }
        if (DeleteIncomingOnly)
        {
            if (Files.Single(f => f.CandidateId is null).Action != ReviewFileAction.Remove
                || Files.Any(f => f.CandidateId is not null && f.Action != ReviewFileAction.Unchanged))
                throw new InvalidOperationException("Delete incoming cannot change archived files.");
            return;
        }
        if (Files.All(f => f.Action == ReviewFileAction.Unchanged))
            throw new InvalidOperationException("Choose at least one action.");
        if (Files.All(f => f.Action == ReviewFileAction.Remove)
            || (Files.Any(f => f.CandidateId is not null && f.Action == ReviewFileAction.Remove)
                && !Files.Any(f => f.Action == ReviewFileAction.Keep)))
            throw new InvalidOperationException("Choose an image to keep before removing archived matches.");
    }
}

public sealed class ReviewGroupDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewId { get; set; }
    public VrcImageCategory Category { get; set; }
    public List<ReviewFileChoice> Files { get; set; } = [];
    public List<ReviewDecisionStep> Steps { get; set; } = [];
    public bool DeleteIncomingOnly { get; set; }
    public Guid? MatchOnlyId { get; set; }
    public string? LastError { get; set; }
}

public sealed class ReviewDecisionStep
{
    public JournalEntry Operation { get; set; } = new();
    public bool Completed { get; set; }
}

public sealed record ReviewDecisionResult(bool ReviewResolved, int CompletedOperations, int RemainingOperations);
public sealed record ManualKeepProtection(string Path, string Fingerprint, long FileSize, DateTimeOffset LastWriteUtc);
