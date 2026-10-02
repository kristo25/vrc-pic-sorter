using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.Scanning;

public sealed partial class ScanCoordinator
{
    public async Task CancelReviewDecisionAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _router.CancelReviewDecisionAsync(reviewId, cancellationToken).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }

    public async Task<ReviewDecisionResult> ApplyReviewDecisionAsync(ReviewDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _router.ApplyReviewDecisionAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }

    public async Task<ReviewDecisionResult> RetryReviewDecisionAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _router.RetryReviewDecisionAsync(reviewId, cancellationToken).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }
}
