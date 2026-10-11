namespace VrcPicSorter.Core.Models;

public enum RemovalFilterReason { PrintFrame, PlayerName }

public sealed class FilterReviewInfo
{
    public bool IsArchive { get; set; }
    public List<RemovalFilterReason> Reasons { get; set; } = [];
}

public sealed record FilterKeepException(string Path, string Fingerprint, long FileSize,
    DateTimeOffset LastWriteUtc, RemovalFilterReason Reason)
{
    public bool DecoderMigrationUnresolved { get; init; }
}

public sealed record FilterScanResult(int Examined, int Recycled, int ReviewNeeded, int Failed,
    IReadOnlyList<string> Errors);

public static class FilterKeepRules
{
    public static bool IsKept(AppStateDocument state, string path, string fingerprint, RemovalFilterReason reason)
    {
        var info = new FileInfo(path);
        return info.Exists && state.FilterKeeps.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)
            && p.Reason == reason && p.FileSize == info.Length && p.LastWriteUtc == info.LastWriteTimeUtc
            && (p.DecoderMigrationUnresolved || p.Fingerprint == fingerprint));
    }

    public static void Repoint(AppStateDocument state, string source, string destination)
    {
        var info = new FileInfo(destination);
        for (var i = 0; i < state.FilterKeeps.Count; i++)
        {
            var item = state.FilterKeeps[i];
            if (string.Equals(item.Path, source, StringComparison.OrdinalIgnoreCase) && info.Exists)
                state.FilterKeeps[i] = item with { Path = destination, FileSize = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }
    }
}
