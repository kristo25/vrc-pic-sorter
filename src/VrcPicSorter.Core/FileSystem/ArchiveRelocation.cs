using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

/// <summary>One archive folder that still holds files, and where those files would go.</summary>
public sealed record ArchiveRelocationStep(
    VrcImageCategory Category,
    string From,
    string To,
    int FileCount,
    long TotalBytes,
    string? InventoryError = null);

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
    bool Stopped = false,
    bool InventoryComplete = true);

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
    public static void EnsurePlanMatchesSettings(AppSettings settings, IEnumerable<ArchiveRelocationStep> steps)
    {
        foreach (var step in steps)
        {
            var mapping = settings.CategoryMappings.SingleOrDefault(item => item.Category == step.Category);
            var sources = settings.LegacyArchiveMappings.Where(item => item.Category == step.Category)
                .Select(item => item.ArchivePath).Append(mapping?.ArchivePath ?? string.Empty);
            if (mapping is null || !mapping.IsEnabled || !SameRoot(mapping.ArchivePath, step.To)
                || !sources.Any(source => !string.IsNullOrWhiteSpace(source) && SameRoot(source, step.From)))
                throw new InvalidOperationException("Archive settings changed. Refresh the retained archives and confirm the move again.");
        }
    }

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
                if (string.Equals(PathBoundary.Normalize(destination), PathBoundary.Normalize(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnsureSeparateRoots(candidate, destination);
                var inventory = ReadInventory(candidate);
                if (inventory.Error is null && inventory.Files.Count == 0)
                {
                    continue;
                }

                steps.Add(new ArchiveRelocationStep(
                    mapping.Category,
                    PathBoundary.Normalize(candidate),
                    destination,
                    inventory.Files.Count,
                    inventory.Bytes,
                    inventory.Error));
            }
        }

        return steps;
    }

    /// <summary>
    /// Moves every file of each step into its destination, keeping the folders it sat in.
    /// </summary>
    /// <remarks>
    /// Byte-identical destination files are reused. Different content receives a separate name.
    /// Copies are flushed and verified before the locked source is removed; failed copies can
    /// be retried without overwriting an existing file.
    /// <para>
    /// Every file that moves is recorded. A batch is nearly always mixed - some files move, some
    /// meet a name already taken, some fail outright - and the index may only be rewritten from
    /// what actually happened. Rebasing a whole folder because a move of it was attempted pointed
    /// records at files that had never moved, and, worse, at a different image that happened to be
    /// sitting at the destination under the same name, with the old fingerprint still attached.
    /// </para>
    /// </remarks>
    public static Task<ArchiveRelocationResult> RelocateAsync(
        IEnumerable<ArchiveRelocationStep> steps,
        IProgress<ArchiveRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        // Do not cancel scheduling: a pre-cancelled request still returns a truthful stopped
        // result. Synchronous filesystem work must never run on the WPF calling thread.
        return Task.Run(() => RelocateCore(steps, progress, cancellationToken));
    }

    private static ArchiveRelocationResult RelocateCore(
        IEnumerable<ArchiveRelocationStep> steps,
        IProgress<ArchiveRelocationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var planned = steps.ToArray();
        var total = planned.Sum(step => step.FileCount);
        var moved = 0;
        var leftBehind = 0;
        var errors = new List<string>();
        var moves = new List<ArchiveRelocationMove>();
        var inventoryComplete = true;
        // Validate every pair before the first move, including cross-step nesting.
        try
        {
            foreach (var step in planned)
            {
                if (SameRoot(step.From, step.To)) continue;
                foreach (var other in planned) EnsureSeparateRoots(other.From, step.To);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new ArchiveRelocationResult(0, 0, [exception.Message], [], InventoryComplete: false);
        }

        foreach (var step in planned)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(moved, leftBehind, errors, moves);
            }

            if (SameRoot(step.From, step.To)) continue;
            var inventory = ReadInventory(step.From);
            if (inventory.Error is { } inventoryError)
            {
                inventoryComplete = false;
                errors.Add(inventoryError);
                continue;
            }
            foreach (var file in inventory.Files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Stop(moved, leftBehind, errors, moves);
                }

                var relative = Path.GetRelativePath(step.From, file);
                var destination = Path.Combine(step.To, relative);
                try
                {
                    PathBoundary.EnsureSafeDestination(step.To, destination, "Relocated archive file");
                    PathBoundary.EnsureContained(step.From, file, "Relocation source");
                    PathBoundary.EnsureNoReparsePoints(file, "Relocation source");
                    destination = VerifiedArchiveTransfer.Move(file, step.To, destination);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or InvalidOperationException)
                {
                    leftBehind++;
                    errors.Add($"{file}: {exception.Message}");
                    continue;
                }

                // A successful move is a completed effect even if the destination goes offline
                // immediately afterwards. Preserve its receipt before reporting progress.
                moved++;
                moves.Add(new ArchiveRelocationMove(step.Category, file, destination));
                try
                {
                    progress?.Report(new ArchiveRelocationProgress(moved, total, Path.GetFileName(file)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or NotSupportedException or InvalidOperationException or OperationCanceledException)
                {
                    errors.Add($"Progress reporting failed after moving {file}: {exception.Message}");
                    return Stop(moved, leftBehind, errors, moves);
                }

            }
        }

        return new ArchiveRelocationResult(moved, leftBehind, errors, moves, InventoryComplete: inventoryComplete);
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
        return new ArchiveRelocationResult(moved, leftBehind, errors, moves, Stopped: true, InventoryComplete: false);
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
                index.Status = IndexStatus.Stale;
                index.LastError = "Archive files relocated; refresh required.";
                index.Images = index.Images.GroupBy(image => NormalizeOrRaw(image.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
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
                var indexed = state.ArchiveIndex.Categories.SelectMany(index => index.Images)
                    .FirstOrDefault(image => string.Equals(image.Path, candidate.ArchivePath, StringComparison.OrdinalIgnoreCase));
                if (indexed is not null) candidate.IndexedImageId = indexed.Id;
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

    /// <summary>Explicit user acknowledgement only: forget metadata, never move or delete files.</summary>
    public static int ForgetUnavailable(AppStateDocument state, string root)
    {
        root = PathBoundary.Normalize(root);
        if (!state.Settings.LegacyArchiveMappings.Any(item => SameRoot(item.ArchivePath, root)))
            throw new InvalidOperationException("That folder is no longer a retained archive. Refresh the list.");
        if (state.Settings.CategoryMappings.Any(item => PathBoundary.Overlaps(item.ArchivePath, root)))
            throw new InvalidOperationException("A current archive cannot be forgotten.");
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException("That folder is available. Move its contents instead of forgetting it.");
        bool UnderRoot(string? path) => !string.IsNullOrWhiteSpace(path) && PathBoundary.Contains(root, path);
        if (state.OperationJournal.Any(entry => entry.Phase != JournalPhase.Completed
            && (UnderRoot(entry.SourcePath) || UnderRoot(entry.DestinationPath) || UnderRoot(entry.SurvivingPath))))
            throw new InvalidOperationException("Resolve pending file operations for this folder before forgetting it.");

        foreach (var index in state.ArchiveIndex.Categories)
        {
            if (!state.Settings.LegacyArchiveMappings.Any(item => item.Category == index.Category && SameRoot(item.ArchivePath, root))) continue;
            index.Images.RemoveAll(image => UnderRoot(image.Path));
            index.Status = IndexStatus.Stale;
            index.LastError = "Unavailable archive forgotten by user; refresh required.";
            index.Generation++;
        }
        foreach (var review in state.ReviewQueue.Where(item => item.Status != ReviewStatus.Resolved))
        {
            if (UnderRoot(review.HeldFilePath) || UnderRoot(review.KeptIncomingArchivedPath)
                || review.Candidates.Any(candidate => UnderRoot(candidate.ArchivePath)))
                review.Status = ReviewStatus.NeedsReconciliation;
            review.Candidates.RemoveAll(candidate => UnderRoot(candidate.ArchivePath));
        }
        return state.Settings.LegacyArchiveMappings.RemoveAll(item => SameRoot(item.ArchivePath, root));
    }

    public static bool IsVerifiedEmpty(string root)
    {
        try
        {
            PathBoundary.EnsureNoReparsePoints(root, "Archive folder");
            // Empty subdirectories are harmless, but never follow or forget a redirecting link.
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var folder))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) == 0) return false;
                    pending.Push(path);
                }
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SameRoot(string first, string second) =>
        string.Equals(PathBoundary.Normalize(first), PathBoundary.Normalize(second), StringComparison.OrdinalIgnoreCase);

    private static void EnsureSeparateRoots(string source, string destination)
    {
        if (PathBoundary.Overlaps(source, destination))
            throw new InvalidOperationException($"Archive relocation folders cannot overlap: {source} and {destination}");
        PathBoundary.EnsureNoReparsePoints(source, "Relocation source");
        PathBoundary.EnsureNoReparsePoints(destination, "Relocation destination");
    }

    private static (IReadOnlyList<string> Files, long Bytes, string? Error) ReadInventory(string root)
    {
        try
        {
            var files = PathBoundary.EnumerateFilesWithoutReparsePoints(root);
            return (files, files.Sum(path => new FileInfo(path).Length), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ([], 0, $"Archive inventory is unavailable or incomplete: {root}. {exception.Message}");
        }
    }
}
