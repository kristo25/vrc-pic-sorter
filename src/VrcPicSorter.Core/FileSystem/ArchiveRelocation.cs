using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

/// <summary>One archive folder that still holds files, and where those files would go.</summary>
public sealed record ArchiveRelocationStep(
    VrcImageCategory Category,
    string From,
    string To,
    int FileCount,
    long TotalBytes);

/// <summary>One file that really did move, and where it landed.</summary>
/// <remarks>
/// The unit everything downstream works from. A plan describes what was hoped for; this describes
/// what happened, one file at a time, which is the only thing an index may be rewritten from.
/// </remarks>
public sealed record ArchiveRelocationMove(VrcImageCategory Category, string From, string To);

/// <summary>What a relocation managed to do.</summary>
/// <param name="Moved">Files that reached the new archive.</param>
/// <param name="LeftBehind">Files deliberately not moved, plus any that could not be.</param>
/// <param name="Errors">One line per file that failed, in the words of the failure.</param>
/// <param name="Moves">Every file that moved, with the place it moved from and the place it is now.</param>
/// <param name="Stopped">
/// True when the run was cancelled part-way. The files in <paramref name="Moves"/> still have to be
/// rebased, so a stop cannot be reported by throwing - but a caller that could not tell the
/// difference announced a half-finished relocation as a finished one.
/// </param>
public sealed record ArchiveRelocationResult(
    int Moved,
    int LeftBehind,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ArchiveRelocationMove> Moves,
    bool Stopped = false);

public sealed record ArchiveRelocationProgress(int Moved, int Total, string FileName);

/// <summary>
/// Moves an archive that has been left behind by a change of output folder.
/// </summary>
/// <remarks>
/// Changing where images are filed has never moved the images already filed, which is the right
/// default - shifting someone's files as a side effect of editing a text box would be rude. What
/// it left behind was a person with their archive in one place, their new output in another, and
/// no way from inside the application to bring the two together.
/// <para>
/// Every archive folder the application knows about is a candidate, not just the current one. A
/// folder that was the output root two changes ago is still indexed and still holds files, and it
/// is exactly as stranded as the one abandoned a moment ago.
/// </para>
/// </remarks>
public static class ArchiveRelocation
{
    /// <summary>
    /// Works out which known archive folders hold files that do not sit under
    /// <paramref name="newOutputRoot"/> yet.
    /// </summary>
    /// <remarks>
    /// A folder that is already the destination is not a step, and neither is an empty one. The
    /// count and total size are read here so the question put to a person can say how much is
    /// about to move rather than asking them to agree to an unknown.
    /// </remarks>
    public static IReadOnlyList<ArchiveRelocationStep> Plan(AppSettings settings, string newOutputRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOutputRoot);

        var root = Path.GetFullPath(newOutputRoot);
        var steps = new List<ArchiveRelocationStep>();

        foreach (var mapping in settings.CategoryMappings.Where(item => item.IsEnabled))
        {
            var destination = Path.Combine(root, mapping.Category.ToString());
            var candidates = settings.LegacyArchiveMappings
                .Where(legacy => legacy.Category == mapping.Category)
                .Select(legacy => legacy.ArchivePath)
                .Prepend(mapping.ArchivePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (PathBoundary.Contains(destination, candidate) || !Directory.Exists(candidate))
                {
                    continue;
                }

                var files = SafeEnumerate(candidate);
                if (files.Count == 0)
                {
                    continue;
                }

                steps.Add(new ArchiveRelocationStep(
                    mapping.Category,
                    PathBoundary.Normalize(candidate),
                    destination,
                    files.Count,
                    files.Sum(SafeLength)));
            }
        }

        return steps;
    }

    /// <summary>
    /// Moves every file of each step into its destination, keeping the folders it sat in.
    /// </summary>
    /// <remarks>
    /// A file whose name is already taken at the destination is left exactly where it is rather
    /// than overwritten. Two archives can hold different images under one name, and the copy
    /// already filed is the one the index is describing.
    /// <para>
    /// Every file that moves is recorded. A batch is nearly always mixed - some files move, some
    /// meet a name already taken, some fail outright - and the index may only be rewritten from
    /// what actually happened. Rebasing a whole folder because a move of it was attempted pointed
    /// records at files that had never moved, and, worse, at a different image that happened to be
    /// sitting at the destination under the same name, with the old fingerprint still attached.
    /// </para>
    /// </remarks>
    public static async Task<ArchiveRelocationResult> RelocateAsync(
        IEnumerable<ArchiveRelocationStep> steps,
        IProgress<ArchiveRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var planned = steps.ToArray();
        var total = planned.Sum(step => step.FileCount);
        var moved = 0;
        var leftBehind = 0;
        var errors = new List<string>();
        var moves = new List<ArchiveRelocationMove>();

        foreach (var step in planned)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(moved, leftBehind, errors, moves);
            }

            foreach (var file in SafeEnumerate(step.From))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Stop(moved, leftBehind, errors, moves);
                }

                var relative = Path.GetRelativePath(step.From, file);
                var destination = Path.Combine(step.To, relative);
                try
                {
                    if (File.Exists(destination))
                    {
                        leftBehind++;
                        errors.Add($"{file}: a file of that name is already in the new archive.");
                        continue;
                    }

                    PathBoundary.EnsureSafeDestination(step.To, destination, "Relocated archive file");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(file, destination);
                    if (!File.Exists(destination))
                    {
                        leftBehind++;
                        errors.Add($"{file}: the move reported success but nothing arrived.");
                        continue;
                    }

                    moved++;
                    moves.Add(new ArchiveRelocationMove(step.Category, file, destination));
                    progress?.Report(new ArchiveRelocationProgress(moved, total, Path.GetFileName(file)));
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or InvalidOperationException)
                {
                    leftBehind++;
                    errors.Add($"{file}: {exception.Message}");
                }

                await Task.Yield();
            }
        }

        return new ArchiveRelocationResult(moved, leftBehind, errors, moves);
    }

    /// <summary>What a cancelled relocation hands back.</summary>
    /// <remarks>
    /// Not an exception. The files already moved are somewhere new, and only this list can tell the
    /// index where they went - throwing took that list with it and left the index describing files
    /// that were no longer there. The flag is what stops the caller reporting a half-finished move
    /// as a finished one.
    /// </remarks>
    private static ArchiveRelocationResult Stop(
        int moved,
        int leftBehind,
        List<string> errors,
        List<ArchiveRelocationMove> moves)
    {
        errors.Add("Moving your archive was stopped. The files that had already moved are recorded.");
        return new ArchiveRelocationResult(moved, leftBehind, errors, moves, Stopped: true);
    }

    /// <summary>
    /// Points indexed images at their new homes.
    /// </summary>
    /// <remarks>
    /// Without this the whole archive would have to be read and fingerprinted again - minutes of
    /// work to rediscover what is already known. The fingerprints are held in a sidecar keyed by
    /// each record's id, so moving the record's path keeps them.
    /// <para>
    /// Driven by the files that really moved, never by the folders a move was planned for. A
    /// record whose file stayed put keeps its path, so the archive is described correctly however
    /// much of the batch succeeded. The index is not the only thing holding archive paths: a
    /// pending review points at its held image and names the archived matches it is being compared
    /// against, and an unfinished journal entry names the file it is about. All of them follow.
    /// </para>
    /// </remarks>
    /// <returns>How many indexed images were repointed.</returns>
    public static int RebaseIndex(AppStateDocument state, IEnumerable<ArchiveRelocationMove> moves)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(moves);

        var landed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            if (string.IsNullOrWhiteSpace(move.From) || string.IsNullOrWhiteSpace(move.To))
            {
                continue;
            }

            landed[NormalizeOrRaw(move.From)] = move.To;
        }

        if (landed.Count == 0)
        {
            return 0;
        }

        var rebased = 0;
        foreach (var index in state.ArchiveIndex.Categories)
        {
            var changed = 0;
            foreach (var image in index.Images)
            {
                if (TryFollow(landed, image.Path, out var movedPath))
                {
                    image.Path = movedPath;
                    changed++;
                }
            }

            if (changed > 0)
            {
                // A generation that did not move would let a scan already in flight route against
                // the paths this just rewrote.
                index.Generation++;
                rebased += changed;
            }
        }

        foreach (var review in state.ReviewQueue.Where(item => item.Status != ReviewStatus.Resolved))
        {
            if (TryFollow(landed, review.HeldFilePath, out var heldPath))
            {
                review.HeldFilePath = heldPath;
            }

            if (TryFollow(landed, review.KeptIncomingArchivedPath, out var keptPath))
            {
                review.KeptIncomingArchivedPath = keptPath;
            }

            foreach (var candidate in review.Candidates)
            {
                if (TryFollow(landed, candidate.ArchivePath, out var candidatePath))
                {
                    candidate.ArchivePath = candidatePath;
                }
            }
        }

        foreach (var entry in state.OperationJournal.Where(item => item.Phase != JournalPhase.Completed))
        {
            if (TryFollow(landed, entry.SourcePath, out var sourcePath))
            {
                entry.SourcePath = sourcePath;
            }

            if (TryFollow(landed, entry.SurvivingPath, out var survivingPath))
            {
                entry.SurvivingPath = survivingPath;
            }
        }

        return rebased;
    }

    private static bool TryFollow(
        IReadOnlyDictionary<string, string> landed,
        string? path,
        out string movedPath)
    {
        movedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!landed.TryGetValue(NormalizeOrRaw(path), out var destination))
        {
            return false;
        }

        movedPath = destination;
        return true;
    }

    private static string NormalizeOrRaw(string path)
    {
        try
        {
            return PathBoundary.Normalize(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Drops the archive folders a relocation has emptied, so nothing goes on treating them as
    /// places images live.
    /// </summary>
    /// <remarks>
    /// This is what frees the folder for ordinary use again: while it is still a known archive,
    /// scanning it is refused, because scanning a folder the index describes would have every
    /// file match itself.
    /// </remarks>
    public static int ForgetRelocated(AppSettings settings, IEnumerable<ArchiveRelocationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(steps);

        var moved = steps
            .Select(step => step.From)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return settings.LegacyArchiveMappings.RemoveAll(
            legacy => !string.IsNullOrWhiteSpace(legacy.ArchivePath)
                && moved.Contains(PathBoundary.Normalize(legacy.ArchivePath)));
    }

    private static IReadOnlyList<string> SafeEnumerate(string root)
    {
        try
        {
            return PathBoundary.EnumerateFilesWithoutReparsePoints(root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
