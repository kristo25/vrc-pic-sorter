using System.Security.Cryptography;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Core.FileSystem;

public static partial class ArchiveRelocation
{
    public const string PendingMessage = "Resolve interrupted archive relocation with Retry recovery before scanning or cleanup.";

    public static Task<ArchiveRelocationResult> RelocateAsync(JsonStateStore store,
        IEnumerable<ArchiveRelocationStep> steps, IProgress<ArchiveRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            EnsureResolved(store.LoadAsync().GetAwaiter().GetResult());
            return RelocateCore(steps, progress, cancellationToken, store);
        });

    public static void EnsureResolved(AppStateDocument state)
    {
        if (state.RelocationJournal.Count != 0) throw new InvalidOperationException(PendingMessage);
    }

    private static string MoveDurably(JsonStateStore store, ArchiveRelocationStep step, string source, string destination)
    {
        var id = Guid.NewGuid();
        var moved = VerifiedArchiveTransfer.Move(source, step.To, destination,
            (chosen, hash, size, stamp) => store.UpdateAsync(state =>
            {
                state.RelocationJournal.RemoveAll(item => item.Id == id);
                state.RelocationJournal.Add(new ArchiveRelocationJournalEntry
                {
                    Id = id,
                    Category = step.Category,
                    SourcePath = source,
                    DestinationPath = chosen,
                    SourceRoot = step.From,
                    DestinationRoot = step.To,
                    ContentSha256 = hash,
                    SourceSize = size,
                    SourceStamp = stamp,
                    ManualKeeps = state.ManualKeeps.Where(item => SameRoot(item.Path, source)
                        && item.FileSize == size && item.LastWriteUtc == stamp).ToList(),
                    FilterKeeps = state.FilterKeeps.Where(item => SameRoot(item.Path, source)
                        && item.FileSize == size && item.LastWriteUtc == stamp).ToList(),
                });
                return true;
            }).GetAwaiter().GetResult(),
            (_, stamp) => SaveVerified(store, id, stamp));
        CompleteReceipt(store, id);
        return moved;
    }

    private static void SaveVerified(JsonStateStore store, Guid id, DateTimeOffset stamp) =>
        store.UpdateAsync(state =>
        {
            var entry = state.RelocationJournal.Single(item => item.Id == id);
            entry.DestinationVerified = true;
            entry.DestinationStamp = stamp;
            entry.LastError = null;
            return true;
        }).GetAwaiter().GetResult();

    /// <summary>Called under the scanner gate (or before startup exposes any scanner).</summary>
    public static Task<IReadOnlyList<string>> RecoverAsync(JsonStateStore store,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<string>>(() =>
        {
            var errors = new List<string>();
            var entries = store.LoadAsync(cancellationToken).GetAwaiter().GetResult().RelocationJournal;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ValidateReceipt(entry);
                    if (IsPresent(entry.SourcePath))
                    {
                        VerifiedArchiveTransfer.Move(entry.SourcePath, entry.DestinationRoot, entry.DestinationPath,
                            saveVerified: (_, stamp) => SaveVerified(store, entry.Id, stamp),
                            expectedHash: entry.ContentSha256, exactDestination: true);
                    }
                    else if (!entry.DestinationVerified)
                    {
                        throw new IOException("The original is absent without a verified destination receipt.");
                    }
                    CompleteReceipt(store, entry.Id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    errors.Add($"{entry.SourcePath}: {error.Message}");
                    try
                    {
                        store.UpdateAsync(state =>
                        {
                            state.RelocationJournal.Single(item => item.Id == entry.Id).LastError = error.Message;
                            return true;
                        }).GetAwaiter().GetResult();
                    }
                    catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"Could not save relocation recovery status: {saveError.Message}");
                    }
                }
            }
            return errors;
        });

    private static bool IsPresent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("A folder occupies the recorded file path.");
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // An unavailable parent is never proof of absence.
            _ = Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!);
            return false;
        }
    }

    private static void ValidateReceipt(ArchiveRelocationJournalEntry entry)
    {
        if (entry.Version != 1 || entry.Id == Guid.Empty || entry.SourceSize < 0
            || entry.ContentSha256 is null || entry.ContentSha256.Length != 64 || !entry.ContentSha256.All(Uri.IsHexDigit)
            || entry.ManualKeeps is null || entry.FilterKeeps is null
            || (entry.DestinationVerified && entry.DestinationStamp is null))
            throw new IOException("Relocation receipt is invalid; manual attention is required.");
        EnsureSeparateRoots(entry.SourceRoot, entry.DestinationRoot);
        PathBoundary.EnsureContained(entry.SourceRoot, entry.SourcePath, "Recorded relocation source");
        PathBoundary.EnsureSafeDestination(entry.DestinationRoot, entry.DestinationPath, "Recorded relocation destination");
        PathBoundary.EnsureNoReparsePoints(entry.SourcePath, "Recorded relocation source");
        if (entry.ManualKeeps.Any(keep => !SameRoot(keep.Path, entry.SourcePath)
                || keep.FileSize != entry.SourceSize || keep.LastWriteUtc != entry.SourceStamp)
            || entry.FilterKeeps.Any(keep => !SameRoot(keep.Path, entry.SourcePath)
                || keep.FileSize != entry.SourceSize || keep.LastWriteUtc != entry.SourceStamp))
            throw new IOException("Relocation protection receipt does not match the recorded source.");
    }

    private static void CompleteReceipt(JsonStateStore store, Guid id)
    {
        var entry = store.LoadAsync().GetAwaiter().GetResult().RelocationJournal.Single(item => item.Id == id);
        ValidateReceipt(entry);
        if (!entry.DestinationVerified || IsPresent(entry.SourcePath))
            throw new IOException("Relocation is not ready to commit; the receipt was kept.");
        using var destination = new FileStream(entry.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (destination.Length != entry.SourceSize
            || !string.Equals(Convert.ToHexString(SHA256.HashData(destination)), entry.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Relocation destination identity changed; the receipt was kept.");
        var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(entry.DestinationPath));
        store.UpdateAsync(state =>
        {
            var receipt = state.RelocationJournal.Single(item => item.Id == id);
            // Rebase and receipt removal are one flushed, atomic state replacement.
            RebaseIndex(state, [new(receipt.Category, receipt.SourcePath, receipt.DestinationPath)
            { SourceSize = receipt.SourceSize, SourceStamp = receipt.SourceStamp, DestinationStamp = stamp }]);
            foreach (var keep in receipt.ManualKeeps)
            {
                var moved = keep with { Path = receipt.DestinationPath, LastWriteUtc = stamp };
                if (!state.ManualKeeps.Contains(moved)) state.ManualKeeps.Add(moved);
            }
            foreach (var keep in receipt.FilterKeeps)
            {
                var moved = keep with { Path = receipt.DestinationPath, LastWriteUtc = stamp };
                if (!state.FilterKeeps.Contains(moved)) state.FilterKeeps.Add(moved);
            }
            state.RelocationJournal.Remove(receipt);
            return true;
        }).GetAwaiter().GetResult();
    }
}
