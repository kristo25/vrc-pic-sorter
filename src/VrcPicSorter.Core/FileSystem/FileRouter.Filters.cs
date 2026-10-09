using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

public sealed partial class FileRouter
{
    public async Task RecycleFilteredAsync(string path, VrcImageCategory category, ImageFingerprint fingerprint,
        IReadOnlyList<RemovalFilterReason> reasons, Guid? reviewId = null, bool explicitRemoval = false,
        CancellationToken cancellationToken = default)
    {
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (HasPendingFileOperation(state, path))
                throw new InvalidOperationException("This image has an unfinished operation. Resolve it before filtering.");
            var entry = CreateRecycleEntry(path, category, fingerprint.ExactIdentity, JournalOperationPurpose.FilterRecycle);
            entry.FilterReasons = reasons.Distinct().ToList();
            entry.FilterRemovalExplicit = explicitRemoval;
            entry.ReviewItemId = reviewId;
            await ExecuteRecycleAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally { _groupGate.Release(); }
    }

    public static bool HasPendingFileOperation(AppStateDocument state, string path) =>
        state.OperationJournal.Any(e => e.Phase != JournalPhase.Completed
            && (PathsMatch(e.SourcePath, path) || PathsMatch(e.DestinationPath, path)))
        || state.ReviewDecisions.Any(g => g.Files.Any(f => PathsMatch(f.Path, path)));

    private async Task VerifyFilterBeforeEffectAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Purpose != JournalOperationPurpose.FilterRecycle) return;
        PathBoundary.EnsureNoReparsePoints(entry.SourcePath, "Filter source");
        var decoded = await _decoder.DecodeAsync(entry.SourcePath, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess || ImageFingerprint.Create(decoded.Image!).ExactIdentity != entry.ExpectedSource.Fingerprint)
            throw new InvalidOperationException("The source image changed after filtering. No file operation was performed.");
        if (entry.FilterRemovalExplicit) return;
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var enabled = entry.FilterReasons.Where(reason => !FilterKeepRules.IsKept(state, entry.SourcePath,
            entry.ExpectedSource.Fingerprint, reason)).ToList();
        if (enabled.Contains(RemovalFilterReason.PlayerName) && state.Settings.RemoveMyImages
            && FilenameAuthor.IsExactMatch(entry.SourcePath, state.Settings.PlayerName)) return;
        if (enabled.Contains(RemovalFilterReason.PrintFrame) && state.Settings.RemovePrintFrame
            && entry.Category == VrcImageCategory.Prints)
        {
            // Identity and frame evidence must describe the same decoded pixels.
            if (PrintFrameDetector.Detect(decoded.Image!) == PrintFrameMatch.Confirmed) return;
        }
        throw new InvalidOperationException("The enabled filters no longer confirm removing this image.");
    }

    private static void ApplyFilterMutation(AppStateDocument state, JournalEntry entry)
    {
        foreach (var index in state.ArchiveIndex.Categories)
            if (index.Images.RemoveAll(i => PathsMatch(i.Path, entry.SourcePath)) > 0) index.Generation++;
        foreach (var review in state.ReviewQueue)
        {
            review.Candidates.RemoveAll(c => PathsMatch(c.ArchivePath, entry.SourcePath));
            if (PathsMatch(review.HeldFilePath, entry.SourcePath)) ResolveReview(state, review.Id);
        }
        state.ManualKeeps.RemoveAll(p => PathsMatch(p.Path, entry.SourcePath));
        state.FilterKeeps.RemoveAll(p => PathsMatch(p.Path, entry.SourcePath));
        if (AtlasAnimationWriter.IsAnimation(entry.SourcePath) && ManualKeepRules.AnimationKey(entry.SourcePath) is { } key
            && !state.SuppressedAnimations.Contains(key, StringComparer.OrdinalIgnoreCase)) state.SuppressedAnimations.Add(key);
    }
}
