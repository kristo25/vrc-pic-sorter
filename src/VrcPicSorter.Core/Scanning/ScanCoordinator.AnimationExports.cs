using VrcPicSorter.Core.Atlas;

namespace VrcPicSorter.Core.Scanning;

public sealed record AnimationExportRequest(ArchivedSheet Sheet, EmojiAtlasName Name, bool MissingOnly = false);
public sealed record AnimationExportBatch(
    IReadOnlyList<AtlasAnimationResult> Results, ExportedAnimationFollowUp FollowUp);

public sealed partial class ScanCoordinator
{
    /// <summary>Serializes UI exports and their index updates with watcher and manual scans.</summary>
    public async Task<AnimationExportBatch> ExportAnimationsAsync(
        IReadOnlyList<AnimationExportRequest> requests, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AtlasAnimationResult>();
        var written = new List<ExportedAnimation>();
        ExportedAnimationFollowUp followUp = new([], [], []);
        try
        {
            var catalog = new AtlasAnimationCatalog(_stateStore);
            try
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = request.MissingOnly
                        ? await catalog.ExportMissingAsync(request.Sheet, cancellationToken).ConfigureAwait(false)
                        : await catalog.ExportAsync(request.Sheet, request.Name, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    if (result.Exported && result.Path is { } path)
                    {
                        written.Add(new ExportedAnimation(request.Sheet.Id, request.Sheet.Category,
                            request.Sheet.AtlasPath, path));
                    }
                }
            }
            finally
            {
                // An interrupted batch may already have written GIFs. Index those too.
                if (written.Count > 0)
                {
                    try
                    {
                        followUp = await FinishExportedAnimationsCoreAsync(written, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or InvalidOperationException or NotSupportedException or ArgumentException)
                    {
                        followUp = new([], [], [$"The archive could not be brought up to date: {exception.Message}"]);
                    }
                }
            }
            return new AnimationExportBatch(results, followUp);
        }
        finally
        {
            _scanGate.Release();
        }
    }
}
