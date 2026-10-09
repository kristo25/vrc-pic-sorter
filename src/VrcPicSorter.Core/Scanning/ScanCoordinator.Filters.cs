using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public sealed partial class ScanCoordinator
{
    private enum FilterOutcome { None, Recycled, Review }

    private async Task<FilterOutcome> FilterFileAsync(string path, VrcImageCategory category, bool archive,
        CancellationToken cancellationToken, string? sourceRoot = null)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var settings = state.Settings;
        if (FileRouter.HasPendingFileOperation(state, path))
            throw new InvalidOperationException("Image has an unfinished operation; filtering was skipped.");
        var player = settings.RemoveMyImages && FilenameAuthor.IsExactMatch(path, settings.PlayerName);
        var inspectFrame = settings.RemovePrintFrame && category == VrcImageCategory.Prints;
        if (!player && !inspectFrame)
        {
            await RetireFilterReviewAsync(state, path, cancellationToken).ConfigureAwait(false);
            return FilterOutcome.None;
        }
        var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess) throw new InvalidDataException("Image could not be decoded for removal filters.");
        var fingerprint = ImageFingerprint.Create(decoded.Image!);
        var reasons = new List<RemovalFilterReason>();
        if (player && !FilterKeepRules.IsKept(state, path, fingerprint.ExactIdentity, RemovalFilterReason.PlayerName))
            reasons.Add(RemovalFilterReason.PlayerName);
        var frame = inspectFrame && !FilterKeepRules.IsKept(state, path, fingerprint.ExactIdentity, RemovalFilterReason.PrintFrame)
            ? PrintFrameDetector.Detect(decoded.Image!) : PrintFrameMatch.None;
        if (frame != PrintFrameMatch.None) reasons.Add(RemovalFilterReason.PrintFrame);
        if (reasons.Count == 0)
        {
            await RetireFilterReviewAsync(state, path, cancellationToken).ConfigureAwait(false);
            return FilterOutcome.None;
        }
        var existing = state.ReviewQueue.FirstOrDefault(r => r.Status != ReviewStatus.Resolved
            && string.Equals(r.HeldFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (frame == PrintFrameMatch.Confirmed || reasons.Contains(RemovalFilterReason.PlayerName))
        {
            await _router.RecycleFilteredAsync(path, category, fingerprint, reasons, existing?.Id,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return FilterOutcome.Recycled;
        }
        await _stateStore.UpdateAsync(current =>
        {
            var review = current.ReviewQueue.FirstOrDefault(r => r.Status != ReviewStatus.Resolved
                && string.Equals(r.HeldFilePath, path, StringComparison.OrdinalIgnoreCase));
            if (review is null)
            {
                review = new ReviewItem
                {
                    Id = Guid.NewGuid(),
                    Category = category,
                    IncomingOriginalPath = path,
                    HeldFilePath = path,
                    CreatedUtc = _timeProvider.GetUtcNow()
                };
                current.ReviewQueue.Add(review);
            }
            review.FilterReview = new FilterReviewInfo { IsArchive = archive, Reasons = reasons };
            if (sourceRoot is not null)
                review.RoutingContext = new ScanRoutingContext
                {
                    SourceRootPath = sourceRoot,
                    RelativeDirectory = Path.GetRelativePath(sourceRoot, Path.GetDirectoryName(path)!),
                    OutputRootPath = current.Settings.OutputRootPath
                };
            review.IncomingFingerprint = fingerprint.ExactIdentity;
            review.IncomingImageFingerprint = fingerprint;
            review.Status = ReviewStatus.Pending;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return FilterOutcome.Review;
    }

    private async Task RetireFilterReviewAsync(AppStateDocument state, string path, CancellationToken cancellationToken)
    {
        if (!state.ReviewQueue.Any(r => r.Status != ReviewStatus.Resolved && r.FilterReview is not null
            && string.Equals(r.HeldFilePath, path, StringComparison.OrdinalIgnoreCase))) return;
        await _stateStore.UpdateAsync(current =>
        {
            foreach (var review in current.ReviewQueue.Where(r => r.Status != ReviewStatus.Resolved && r.FilterReview is not null
                && string.Equals(r.HeldFilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                review.Status = ReviewStatus.Resolved;
                review.FilterReview = null;
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FilterScanResult> RescanArchiveFiltersAsync(IProgress<ScanProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            ArchiveRelocation.EnsureResolved(state);
            var errors = new List<string>();
            var files = new List<(string Path, VrcImageCategory Category)>();
            if (!state.Settings.RemovePrintFrame && !state.Settings.RemoveMyImages)
            {
                foreach (var pending in state.ReviewQueue.Where(r => r.Status != ReviewStatus.Resolved
                    && r.FilterReview?.IsArchive == true))
                    await RetireFilterReviewAsync(state, pending.HeldFilePath, cancellationToken).ConfigureAwait(false);
                return new(0, 0, 0, 0, []);
            }
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                var snapshot = CreateSourceSnapshotSafe(mapping.ArchivePath);
                if (snapshot.Error is not null) errors.Add(snapshot.Error);
                else files.AddRange(snapshot.Paths.Select(p => (p, mapping.Category)));
            }
            var examined = 0; var recycled = 0; var review = 0;
            foreach (var file in files.DistinctBy(f => PathBoundary.Normalize(f.Path), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await FilterFileAsync(file.Path, file.Category, true, cancellationToken).ConfigureAwait(false);
                    if (outcome == FilterOutcome.Recycled) recycled++;
                    if (outcome == FilterOutcome.Review) review++;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                { errors.Add($"{file.Path}: {error.Message}"); }
                examined++;
                progress?.Report(new(examined, files.Count, "Archive filters", Path.GetFileName(file.Path)));
            }
            return new(examined, recycled, review, errors.Count, errors);
        }
        finally { _scanGate.Release(); }
    }

    public async Task ResolveFilterReviewAsync(Guid reviewId, bool keep, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var review = state.ReviewQueue.SingleOrDefault(r => r.Id == reviewId && r.Status != ReviewStatus.Resolved)
                ?? throw new InvalidOperationException("The filter review was already resolved.");
            var filter = review.FilterReview ?? throw new InvalidOperationException("This is not a filter review.");
            if (FileRouter.HasPendingFileOperation(state, review.HeldFilePath))
                throw new InvalidOperationException("Resolve the pending file operation first.");
            var decoded = await _decoder.DecodeAsync(review.HeldFilePath, cancellationToken).ConfigureAwait(false);
            var fingerprint = decoded.IsSuccess ? ImageFingerprint.Create(decoded.Image!) : null;
            if (fingerprint?.ExactIdentity != review.IncomingFingerprint)
                throw new InvalidOperationException("The image changed or is unavailable. Rescan before deciding.");
            if (!keep)
            {
                await _router.RecycleFilteredAsync(review.HeldFilePath, review.Category, fingerprint!,
                    filter.Reasons, review.Id, true, cancellationToken).ConfigureAwait(false);
                return;
            }
            var info = new FileInfo(review.HeldFilePath);
            await _stateStore.UpdateAsync(current =>
            {
                foreach (var reason in filter.Reasons)
                {
                    current.FilterKeeps.RemoveAll(p => string.Equals(p.Path, review.HeldFilePath, StringComparison.OrdinalIgnoreCase) && p.Reason == reason);
                    current.FilterKeeps.Add(new(review.HeldFilePath, review.IncomingFingerprint, info.Length, info.LastWriteTimeUtc, reason));
                }
                var saved = current.ReviewQueue.Single(r => r.Id == review.Id);
                saved.FilterReview = null;
                saved.Status = ReviewStatus.Resolved;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            if (!filter.IsArchive)
            {
                var root = review.RoutingContext.SourceRootPath;
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetDirectoryName(review.HeldFilePath)!;
                var result = await ScanCategoryCoreAsync(review.Category, root, false,
                    new SourceSnapshot([review.HeldFilePath], 0, null), null, null, cancellationToken).ConfigureAwait(false);
                if (result.Errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
            }
        }
        finally { _scanGate.Release(); }
    }
}
