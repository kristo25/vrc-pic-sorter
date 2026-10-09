namespace VrcPicSorter.Core.Models;

/// <summary>Write-ahead evidence owned exclusively by archive relocation recovery.</summary>
public sealed class ArchiveRelocationJournalEntry
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public VrcImageCategory Category { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string DestinationRoot { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public DateTimeOffset SourceStamp { get; set; }
    public DateTimeOffset? DestinationStamp { get; set; }
    public bool DestinationVerified { get; set; }
    public List<ManualKeepProtection> ManualKeeps { get; set; } = [];
    public List<FilterKeepException> FilterKeeps { get; set; } = [];
    public string? LastError { get; set; }
}
