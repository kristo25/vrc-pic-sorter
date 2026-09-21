using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.Atlas;

/// <summary>One archived sheet and the animation that belongs to it.</summary>
public sealed record ArchivedSheet(
    Guid Id,
    VrcImageCategory Category,
    string AtlasPath,
    string ArchiveRoot,
    string AnimationPath,
    bool HasAnimation,
    EmojiAtlasName Name,
    string Fingerprint = "",
    bool IsSkipped = false,
    string? AnimationFingerprint = null)
{
    public string FileName => System.IO.Path.GetFileName(AtlasPath);

    public string Summary =>
        $"{Name.FrameCount} frames · {Name.FramesPerSecond} fps · "
        + (Name.LoopStyle == AtlasLoopStyle.PingPong ? "ping-pong" : "linear")
        + (HasAnimation ? string.Empty : IsSkipped ? " · skipped" : " · not exported");

    /// <summary>Still waiting on a decision: no animation, and not one a person has skipped.</summary>
    public bool NeedsDecision => !HasAnimation && !IsSkipped;

    /// <summary>
    /// The rate the exported file will really play at. GIF cannot express every rate, so most land
    /// on the nearest one a viewer will honour.
    /// </summary>
    public int EffectiveFramesPerSecond =>
        AtlasGifExporter.EffectiveFramesPerSecondFor(Name.FramesPerSecond);

    /// <summary>
    /// True only when the rate was too fast for GIF to express and had to be pulled down, rather
    /// than merely landing on a neighbouring whole hundredth.
    /// </summary>
    /// <remarks>
    /// This used to compare a truncated quotient against the requested rate, which called almost
    /// every real sheet clamped - 31, 30, 24 and 15 fps all reported it - because nearly no rate
    /// divides 100 exactly. Only the ceiling is a real clamp.
    /// </remarks>
    public bool RateWasClamped =>
        Name.FramesPerSecond > AtlasGifExporter.MaximumRepresentableFramesPerSecond;
}

/// <summary>
/// Lists the animated emoji in the archive and re-exports them on request.
/// </summary>
/// <remarks>
/// Sheets come from the index; animation candidates are read from disk once per root so an
/// unindexed or renamed GIF does not turn an already animated sheet back into missing work.
/// </remarks>
public sealed class AtlasAnimationCatalog
{
    private readonly JsonStateStore _stateStore;
    private readonly AtlasAnimationWriter _writer = new();

    public AtlasAnimationCatalog(JsonStateStore stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<IReadOnlyList<ArchivedSheet>> ListAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var skipped = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);

        var sheets = new List<ArchivedSheet>();
        var animations = new Dictionary<string, ExistingAnimations>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in state.ArchiveIndex.Categories)
        {
            foreach (var image in category.Images)
            {
                if (!EmojiAtlasName.TryParse(image.Path, out var name))
                {
                    continue;
                }

                string destination;
                string root;
                bool exists;
                string? animationFingerprint = null;
                try
                {
                    root = ArchiveOwner.Resolve(state.Settings, category.Category, image.Path);
                    destination = AtlasAnimationWriter.BuildDestination(image.Path, root);
                    var searchRoot = AtlasAnimationWriter.DestinationRoot(image.Path, root);
                    if (!animations.TryGetValue(searchRoot, out var existing))
                    {
                        existing = new ExistingAnimations(searchRoot);
                        animations.Add(searchRoot, existing);
                    }
                    var retained = existing.Find(image.Path, root);
                    exists = retained is not null;
                    destination = retained ?? destination;
                    if (retained is not null && !fingerprints.TryGetValue(retained, out animationFingerprint))
                    {
                        animationFingerprint = await AtlasGifExporter.ReadContentHashAsync(retained, cancellationToken).ConfigureAwait(false);
                        fingerprints.Add(retained, animationFingerprint);
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or UnauthorizedAccessException
                        or InvalidOperationException or NotSupportedException)
                {
                    throw new IOException($"Could not check the animation for {image.Path}: {exception.Message}", exception);
                }

                sheets.Add(new ArchivedSheet(
                    image.Id,
                    category.Category,
                    image.Path,
                    root,
                    destination,
                    exists,
                    name,
                    image.ExactFingerprint,
                    skipped.Contains(image.ExactFingerprint),
                    animationFingerprint));
            }
        }

        return sheets
            .OrderBy(sheet => sheet.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Exports one sheet using <paramref name="name"/>, which may differ from the one in the file
    /// name when the name is wrong and you are correcting it by hand.
    /// </summary>
    public Task<AtlasAnimationResult> ExportAsync(
        ArchivedSheet sheet,
        EmojiAtlasName name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(name);
        var current = AtlasAnimationWriter.FindExistingAnimation(sheet.AtlasPath, sheet.ArchiveRoot);
        if (sheet.HasAnimation && !string.Equals(current, sheet.AnimationPath, StringComparison.OrdinalIgnoreCase)
            || !sheet.HasAnimation && current is not null)
        {
            return Task.FromResult(new AtlasAnimationResult(false, null,
                "The existing animation changed. Refresh the list and confirm the replacement again."));
        }

        return sheet.HasAnimation
            ? sheet.AnimationFingerprint is null
                ? Task.FromResult(new AtlasAnimationResult(false, null, "Refresh the list before replacing an existing animation."))
                : _writer.TryWriteAsync(sheet.AtlasPath, sheet.ArchiveRoot, name, cancellationToken,
                    sheet.AnimationPath, sheet.AnimationFingerprint)
            : _writer.TryWriteMissingAsync(sheet.AtlasPath, sheet.ArchiveRoot, name, cancellationToken);
    }

    public Task<AtlasAnimationResult> ExportMissingAsync(
        ArchivedSheet sheet, CancellationToken cancellationToken = default) =>
        _writer.TryWriteMissingAsync(sheet.AtlasPath, sheet.ArchiveRoot, sheet.Name, cancellationToken);

    /// <summary>
    /// Marks sheets as skipped, so they stop counting as work still to do. Nothing on disk moves
    /// or is deleted: a skip is a note that this sheet is not worth animating.
    /// </summary>
    public Task<int> SkipAsync(
        IEnumerable<ArchivedSheet> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var keys = sheets
            .Select(sheet => sheet.Fingerprint)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        return UpdateSkipsAsync(
            skips =>
            {
                var added = 0;
                foreach (var key in keys)
                {
                    if (skips.Add(key))
                    {
                        added++;
                    }
                }

                return added;
            },
            cancellationToken);
    }

    /// <summary>Puts skipped sheets back in the queue.</summary>
    public Task<int> RestoreAsync(
        IEnumerable<ArchivedSheet> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var keys = sheets.Select(sheet => sheet.Fingerprint).ToArray();
        return UpdateSkipsAsync(
            skips => keys.Count(key => skips.Remove(key)),
            cancellationToken);
    }

    private async Task<int> UpdateSkipsAsync(
        Func<HashSet<string>, int> change,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        await _stateStore
            .UpdateAsync(
                state =>
                {
                    var skips = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);
                    changed = change(skips);
                    if (changed == 0)
                    {
                        return false;
                    }

                    state.SkippedAnimations = skips.Order(StringComparer.OrdinalIgnoreCase).ToList();
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }
}
