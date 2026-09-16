using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.App.Services;

/// <summary>
/// Carries the local data folder across the rename from VRC Image Curator to VRC Pic Sorter.
/// </summary>
/// <remarks>
/// Settings, the archive index, the review queue, the operation journal and the held incoming files
/// all live in one folder named after the application. Renaming the application without moving that
/// folder would leave an existing installation starting from nothing, with a queue of undecided
/// reviews stranded under a name nothing looks at any more.
/// <para>
/// The move happens once, and only when there is an old folder and no new one. Every other case -
/// both present, neither present, or a failure part-way - leaves the new folder to be created
/// empty. The old folder is never deleted, so the worst outcome of a failed carry-over is starting
/// fresh with the previous data still sitting on disk.
/// </para>
/// </remarks>
public static class LocalDataMigration
{
    /// <summary>The folder this application kept its data in before it was renamed.</summary>
    public const string PreviousFolderName = "VrcImageCurator";

    /// <summary>
    /// Moves <paramref name="previousDirectory"/> to <paramref name="currentDirectory"/> when the
    /// first exists and the second does not yet.
    /// </summary>
    /// <returns><see langword="true"/> when a folder was actually carried over.</returns>
    public static bool CarryOver(string previousDirectory, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        // An existing new folder wins outright. Merging the two would mean deciding which copy of
        // state.json is the real one, and there is no answer to that worth guessing at.
        if (Directory.Exists(currentDirectory) || !Directory.Exists(previousDirectory))
        {
            return false;
        }

        try
        {
            Directory.Move(previousDirectory, currentDirectory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is a recoverable disappointment. Refusing to start is not, so a
            // locked or unreadable old folder is stepped over rather than thrown from.
            return false;
        }
    }

    /// <summary>
    /// True when anything the state document remembers still names a path inside the old data
    /// folder.
    /// </summary>
    /// <remarks>
    /// Moving the folder is only half the job. Paths are stored absolute, so everything written
    /// while the application had its old name goes on naming the old folder afterwards - a folder
    /// that is no longer there. Settings were repaired and nothing else was, which left the parts
    /// that matter most: a held review pointing at a file under the vanished name could not be
    /// decided, restored or dismissed, and an unfinished operation naming one could not be
    /// reconciled. Asked before writing, so an ordinary start does not rewrite the document to say
    /// exactly what it already said.
    /// </remarks>
    public static bool NeedsRebase(AppStateDocument state, string previousDirectory, string currentDirectory) =>
        Apply(state, previousDirectory, currentDirectory, commit: false) > 0;

    /// <summary>
    /// Rewrites every stored path that sat inside <paramref name="previousDirectory"/> so it names
    /// the same place inside <paramref name="currentDirectory"/> instead.
    /// </summary>
    /// <remarks>
    /// Paths outside the old data folder are left exactly as they are. Someone's archive on another
    /// drive has nothing to do with what this application is called.
    /// <para>
    /// Safe to run twice: a path that has been rewritten no longer sits inside the old folder, so
    /// the second run finds nothing to do. Safe to run after an interruption for the same reason -
    /// whatever was rewritten stays rewritten, and whatever was not is found again next time.
    /// </para>
    /// </remarks>
    /// <returns>How many paths were rewritten.</returns>
    public static int RebasePaths(AppStateDocument state, string previousDirectory, string currentDirectory) =>
        Apply(state, previousDirectory, currentDirectory, commit: true);

    /// <summary>
    /// Walks every path the document holds once, either counting what would move or moving it.
    /// </summary>
    /// <remarks>
    /// One traversal for both questions on purpose. Two would drift, and the one that decides
    /// whether to write is the one that must agree with the one that writes.
    /// </remarks>
    private static int Apply(
        AppStateDocument state,
        string previousDirectory,
        string currentDirectory,
        bool commit)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var rebased = 0;
        var settings = state.Settings;
        settings.HoldingRootPath = Rebase(settings.HoldingRootPath, previousDirectory, currentDirectory, commit, ref rebased);
        settings.OutputRootPath = Rebase(settings.OutputRootPath, previousDirectory, currentDirectory, commit, ref rebased);

        foreach (var mapping in settings.CategoryMappings)
        {
            mapping.SourcePath = Rebase(mapping.SourcePath, previousDirectory, currentDirectory, commit, ref rebased);
            mapping.ArchivePath = Rebase(mapping.ArchivePath, previousDirectory, currentDirectory, commit, ref rebased);
        }

        foreach (var mapping in settings.LegacyArchiveMappings)
        {
            mapping.ArchivePath = Rebase(mapping.ArchivePath, previousDirectory, currentDirectory, commit, ref rebased);
        }

        foreach (var review in state.ReviewQueue)
        {
            RebaseReview(review, previousDirectory, currentDirectory, commit, ref rebased);
        }

        foreach (var category in state.ArchiveIndex.Categories)
        {
            foreach (var image in category.Images)
            {
                image.Path = Rebase(image.Path, previousDirectory, currentDirectory, commit, ref rebased);
            }
        }

        foreach (var entry in state.OperationJournal)
        {
            entry.SourcePath = Rebase(entry.SourcePath, previousDirectory, currentDirectory, commit, ref rebased);
            entry.DestinationPath = RebaseOptional(entry.DestinationPath, previousDirectory, currentDirectory, commit, ref rebased);
            entry.SurvivingPath = RebaseOptional(entry.SurvivingPath, previousDirectory, currentDirectory, commit, ref rebased);
            RebaseRouting(entry.RoutingContext, previousDirectory, currentDirectory, commit, ref rebased);

            // The review an unfinished hold is carrying. It is not in the queue yet - that is what
            // the unfinished half was going to do - so nothing else in this walk would reach it.
            if (entry.ReviewItemAfterCommit is { } pendingReview)
            {
                RebaseReview(pendingReview, previousDirectory, currentDirectory, commit, ref rebased);
            }

            if (entry.IndexedImageAfterCommit is { } pendingRecord)
            {
                pendingRecord.Path = Rebase(pendingRecord.Path, previousDirectory, currentDirectory, commit, ref rebased);
            }
        }

        return rebased;
    }

    private static void RebaseReview(
        ReviewItem review,
        string previousDirectory,
        string currentDirectory,
        bool commit,
        ref int rebased)
    {
        review.HeldFilePath = Rebase(review.HeldFilePath, previousDirectory, currentDirectory, commit, ref rebased);
        review.IncomingOriginalPath = Rebase(review.IncomingOriginalPath, previousDirectory, currentDirectory, commit, ref rebased);
        review.KeptIncomingArchivedPath = RebaseOptional(review.KeptIncomingArchivedPath, previousDirectory, currentDirectory, commit, ref rebased);
        RebaseRouting(review.RoutingContext, previousDirectory, currentDirectory, commit, ref rebased);

        foreach (var candidate in review.Candidates)
        {
            candidate.ArchivePath = Rebase(candidate.ArchivePath, previousDirectory, currentDirectory, commit, ref rebased);
        }
    }

    private static void RebaseRouting(
        ScanRoutingContext? routing,
        string previousDirectory,
        string currentDirectory,
        bool commit,
        ref int rebased)
    {
        if (routing is null)
        {
            return;
        }

        routing.SourceRootPath = Rebase(routing.SourceRootPath, previousDirectory, currentDirectory, commit, ref rebased);
        routing.OutputRootPath = Rebase(routing.OutputRootPath, previousDirectory, currentDirectory, commit, ref rebased);
    }

    private static string? RebaseOptional(
        string? path,
        string previousDirectory,
        string currentDirectory,
        bool commit,
        ref int rebased) =>
        path is null ? null : Rebase(path, previousDirectory, currentDirectory, commit, ref rebased);

    private static string Rebase(
        string path,
        string previousDirectory,
        string currentDirectory,
        bool commit,
        ref int rebased)
    {
        if (!IsInside(path, previousDirectory))
        {
            return path;
        }

        // Both folders can exist at once - the carry-over declines when they do, rather than
        // guessing which copy of the state document is the real one. A file that is genuinely
        // still in the old folder, with nothing at the new place, stays named where it is: moving
        // the name without the file would lose it.
        var relative = Path.GetRelativePath(previousDirectory, path);
        var rebasedPath = relative == "." ? currentDirectory : Path.Combine(currentDirectory, relative);
        if (Exists(path) && !Exists(rebasedPath))
        {
            return path;
        }

        rebased++;
        return commit ? rebasedPath : path;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsInside(string path, string previousDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return PathBoundary.Contains(previousDirectory, path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // This walks the whole state document now rather than a handful of settings, and it
            // runs before anything else on every start. One unusable path left behind by some
            // earlier version has to be stepped over, not allowed to stop the application.
            return false;
        }
    }
}
