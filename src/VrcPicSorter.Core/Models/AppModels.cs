using VrcPicSorter.Core.Imaging;
using System.Text.Json.Serialization;

namespace VrcPicSorter.Core.Models;

public enum VrcImageCategory
{
    Emoji,
    Prints,
    Stickers,
}

/// <summary>Which subfolders, if any, an archived image is filed into under its category.</summary>
public enum OrganizationPolicy
{
    /// <summary>Everything for a category lands directly in that category's folder.</summary>
    CategoryRoot,

    /// <summary>One folder per month, taken from when the image was written.</summary>
    CategoryYearMonth,

    /// <summary>Whatever folders the image already sat in under its source are recreated.</summary>
    PreserveIncomingRelativeFolder,
}

public enum SimilarityProfile
{
    Strict,
    Conservative,
    Broad,
}

public enum IndexStatus
{
    Stale,
    Building,
    Current,
    Unavailable,
}

/// <summary>How folder watching decides when to analyze incoming images.</summary>
public enum WatchMode
{
    /// <summary>Analyze each image as soon as the watcher reports it, and nothing else.</summary>
    OnDetection,

    /// <summary>
    /// Ignore individual file events and sweep the configured folders on a fixed interval.
    /// Arrivals are still announced, but nothing is analyzed until the next sweep.
    /// </summary>
    OnInterval,
}

public enum ReviewStatus
{
    Pending,
    NeedsReconciliation,
    Resolved,
}

public enum MatchKind
{
    Exact,
    Similar,
}

public enum ActivityKind
{
    Scan,
    AutomaticMove,
    ReviewDecision,
    Warning,
    Retry,
    DeletionRequested,
}

public enum ActivityLevel
{
    Information,
    Warning,
    Error,
}

public enum JournalOperationType
{
    Move,
    Recycle,
    DeleteExactIncoming,
}

public enum JournalOperationPurpose
{
    HoldForReview,
    MoveUnique,
    MoveDuplicateOverride,
    KeepExisting,
    AutoKeepArchived,

    /// <summary>
    /// Recycles a second copy of a picture that is already waiting in Review. The held copy keeps
    /// the decision; this one was only ever the same picture saved under another name.
    /// </summary>
    AutoKeepHeld,
    DeleteArchiveCandidate,
    PreserveArchiveCandidate,

    /// <summary>
    /// Recycles a copy the archive is holding twice. No review is involved: the decision was made
    /// by comparing two files that are both already archived, which is the one thing deduplication
    /// never used to do.
    /// </summary>
    RemoveArchiveDuplicate,
    RestoreReviewToSource,

    /// <summary>
    /// Files an archived sheet beside the animation just made from it. The image is already in the
    /// index by this point, so the commit moves the record rather than adding one.
    /// </summary>
    FileAnimatedSheet,
}

public enum JournalPhase
{
    IntentRecorded,
    SideEffectStarted,
    SideEffectApplied,
    StateCommitted,
    Completed,
    NeedsAttention,
}

public sealed class AppStateDocument
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public long Revision { get; set; }

    public AppSettings Settings { get; set; } = new();

    public ArchiveIndexState ArchiveIndex { get; set; } = new();

    public List<ReviewItem> ReviewQueue { get; set; } = [];

    /// <summary>
    /// Sheets a person has decided not to animate, by exact image fingerprint.
    /// </summary>
    /// <remarks>
    /// Keyed by fingerprint rather than by index id or path, because both of those move: rebuilding
    /// the archive index mints new ids, and filing a sheet beside its animation changes its path.
    /// The fingerprint is the one thing that survives both, so a sheet skipped once stays skipped.
    /// </remarks>
    public List<string> SkippedAnimations { get; set; } = [];

    public List<JournalEntry> OperationJournal { get; set; } = [];

    public List<ActivityEntry> History { get; set; } = [];
}

public sealed class AppSettings
{
    public List<CategoryMapping> CategoryMappings { get; set; } = [];

    public string OutputRootPath { get; set; } = string.Empty;

    public bool OutputRootConfirmed { get; set; } = true;

    // Only freshly generated defaults are suggestions. Older state omits this flag and
    // remains authoritative, including temporarily unavailable archives.
    public bool OutputRootIsSuggested { get; set; }

    public List<LegacyArchiveMapping> LegacyArchiveMappings { get; set; } = [];

    public OrganizationPolicy OrganizationPolicy { get; set; } = OrganizationPolicy.CategoryRoot;

    /// <summary>
    /// The output folder a person has already been asked about moving their old archive into.
    /// </summary>
    /// <remarks>
    /// Stored as the destination rather than as a yes or no, so the question returns if they pick
    /// a different folder later and stays gone while they keep the one they answered for. Asking
    /// on every save would be nagging; never asking again would make a change of mind impossible.
    /// </remarks>
    public string ArchiveRelocationAnsweredFor { get; set; } = string.Empty;

    public SimilarityProfile SimilarityProfile { get; set; } = SimilarityProfile.Conservative;

    public SimilarityThresholds? CustomSimilarityThresholds { get; set; }

    public AutomationSettings Automation { get; set; } = new();

    public string HoldingRootPath { get; set; } = string.Empty;

    public bool BringReviewForwardWhenHeld { get; set; } = true;
}

public sealed class CategoryMapping
{
    // False for old state. True only when a newly selected category folder was
    // positively absent beneath a readable parent, not merely unreachable.
    public bool ArchivePathKnownMissing { get; set; }

    public VrcImageCategory Category { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string ArchivePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
}

public sealed class LegacyArchiveMapping
{
    public VrcImageCategory Category { get; set; }

    public string ArchivePath { get; set; } = string.Empty;
}

public sealed class AutomationSettings
{
    public bool WatchWhileOpen { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Whether watching reacts to each arrival or sweeps on a timer.</summary>
    public WatchMode WatchMode { get; set; } = WatchMode.OnDetection;

    /// <summary>
    /// How often folder watching sweeps the configured folders when <see cref="WatchMode"/> is
    /// <see cref="WatchMode.OnInterval"/>. Clamped to
    /// <see cref="MinimumWatchScanSeconds"/>..<see cref="MaximumWatchScanSeconds"/>.
    /// </summary>
    public int WatchScanSeconds { get; set; } = DefaultWatchScanSeconds;

    public const int DefaultWatchScanSeconds = 60;

    public const int MinimumWatchScanSeconds = 15;

    public const int MaximumWatchScanSeconds = 3600;

    public TimeSpan WatchScanInterval => TimeSpan.FromSeconds(
        Math.Clamp(WatchScanSeconds, MinimumWatchScanSeconds, MaximumWatchScanSeconds));
}

public sealed class ArchiveIndexState
{
    public List<CategoryIndexState> Categories { get; set; } = [];
}

public sealed class CategoryIndexState
{
    public VrcImageCategory Category { get; set; }

    public IndexStatus Status { get; set; } = IndexStatus.Stale;

    public long Generation { get; set; }

    public DateTimeOffset? LastCompletedUtc { get; set; }

    public string? LastError { get; set; }

    public List<IndexedImageRecord> Images { get; set; } = [];
}

public sealed class IndexedImageRecord
{
    public Guid Id { get; set; }

    public VrcImageCategory Category { get; set; }

    public string Path { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTimeOffset LastWriteUtc { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string ExactFingerprint { get; set; } = string.Empty;

    public string PerceptualFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Held in memory only. Perceptual fingerprints are large - a frame carries 8 KB of
    /// thumbnails, inset variants, alpha and detail samples, which is roughly 11 KB of JSON for a
    /// still image and 90 KB for an animation - and keeping them inside the state document meant
    /// every state write rewrote all of them. They live in a sidecar keyed by <see cref="Id"/>
    /// and are reattached on load; a record whose fingerprint is missing forces an index
    /// rebuild.
    /// </summary>
    [JsonIgnore]
    public ImageFingerprint? Fingerprint { get; set; }
}

public sealed class ReviewItem
{
    public Guid Id { get; set; }

    public VrcImageCategory Category { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    public string IncomingOriginalPath { get; set; } = string.Empty;

    public string HeldFilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsIncomingInPlace =>
        !string.IsNullOrWhiteSpace(IncomingOriginalPath)
        && string.Equals(IncomingOriginalPath, HeldFilePath, StringComparison.OrdinalIgnoreCase);

    public string IncomingFingerprint { get; set; } = string.Empty;

    public ImageFingerprint? IncomingImageFingerprint { get; set; }

    public long IndexGeneration { get; set; }

    /// <summary>
    /// Where a Keep incoming decision has already put the incoming image, once the first of its
    /// two halves has committed.
    /// </summary>
    /// <remarks>
    /// Keep incoming archives the incoming image and then removes the match it replaced, and the
    /// two steps are separate durable writes. This is the phase between them, written as part of
    /// the same update that adds the archive record: a retry reads it and resumes at the second
    /// step instead of filing a second copy, and the removal of the last match reads it to know
    /// the decision is complete. Null on a review that has not started one, and cleared again when
    /// the decision closes.
    /// </remarks>
    public string? KeptIncomingArchivedPath { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public ScanRoutingContext RoutingContext { get; set; } = new();

    public List<ReviewCandidate> Candidates { get; set; } = [];
}

public sealed class ReviewCandidate
{
    public Guid Id { get; set; }

    public Guid IndexedImageId { get; set; }

    public string ArchivePath { get; set; } = string.Empty;

    public string ExpectedFingerprint { get; set; } = string.Empty;

    public MatchKind MatchKind { get; set; }

    public double SimilarityScore { get; set; }

    public List<string> MatchReasons { get; set; } = [];

    public bool IsSelected { get; set; }

    public bool IsStale { get; set; }
}

public sealed class ExpectedFileIdentity
{
    public string Fingerprint { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTimeOffset LastWriteUtc { get; set; }
}

public sealed class JournalEntry
{
    public Guid Id { get; set; }

    public JournalOperationType OperationType { get; set; }

    public JournalOperationPurpose Purpose { get; set; }

    public JournalPhase Phase { get; set; } = JournalPhase.IntentRecorded;

    public VrcImageCategory Category { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string? DestinationPath { get; set; }

    public ScanRoutingContext? RoutingContext { get; set; }

    public ExpectedFileIdentity ExpectedSource { get; set; } = new();

    public Guid? ReviewItemId { get; set; }

    public Guid? IndexedImageId { get; set; }

    /// <summary>
    /// The copy an automatic duplicate resolution decided to keep, when that decision is the only
    /// reason this operation is safe. Recorded so recovery can re-check it before retrying, rather
    /// than discarding a file on a precondition that held before the crash and may not hold now.
    /// </summary>
    public string? SurvivingPath { get; set; }

    /// <summary>What <see cref="SurvivingPath"/> must still be for the retry to go ahead.</summary>
    public string? SurvivingFingerprint { get; set; }

    public ReviewItem? ReviewItemAfterCommit { get; set; }

    public IndexedImageRecord? IndexedImageAfterCommit { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public string? LastError { get; set; }
}

public sealed class ScanRoutingContext
{
    public string SourceRootPath { get; set; } = string.Empty;

    public string RelativeDirectory { get; set; } = string.Empty;

    public string OutputRootPath { get; set; } = string.Empty;
}

public sealed class ActivityEntry
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredUtc { get; set; }

    public ActivityKind Kind { get; set; }

    public ActivityLevel Level { get; set; }

    public VrcImageCategory? Category { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? SourcePath { get; set; }

    public string? DestinationPath { get; set; }

    public Guid? OperationId { get; set; }
}

public static class AppStateDefaults
{
    public static readonly IReadOnlyList<VrcImageCategory> FixedCategories =
    [
        VrcImageCategory.Emoji,
        VrcImageCategory.Prints,
        VrcImageCategory.Stickers,
    ];

    public static AppStateDocument Create(
        string? userProfilePath = null,
        string? localAppDataPath = null,
        string? stateDirectoryPath = null,
        string? picturesPath = null)
    {
        var profileWasProvided = userProfilePath is not null;
        userProfilePath ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        localAppDataPath ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Pictures is commonly redirected away from the profile folder by OneDrive Known Folder
        // Move, so ask Windows where it actually is. Tests and isolated runs pass their own
        // profile path and stay contained inside it.
        if (string.IsNullOrWhiteSpace(picturesPath))
        {
            picturesPath = profileWasProvided
                ? Path.Combine(userProfilePath, "Pictures")
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        if (string.IsNullOrWhiteSpace(picturesPath))
        {
            picturesPath = Path.Combine(userProfilePath, "Pictures");
        }

        // The archive lives beside the category folders rather than inside any of them, so no
        // source folder ever contains the archive.
        var sourceRoot = Path.Combine(picturesPath, "VRChat");
        var archiveRoot = Path.Combine(sourceRoot, "Archived Images");

        return new AppStateDocument
        {
            Settings = new AppSettings
            {
                OutputRootPath = archiveRoot,
                OutputRootIsSuggested = true,
                CategoryMappings = FixedCategories
                    .Select(category => new CategoryMapping
                    {
                        Category = category,
                        SourcePath = Path.Combine(sourceRoot, category.ToString()),
                        ArchivePath = Path.Combine(archiveRoot, category.ToString()),
                    })
                    .ToList(),
                HoldingRootPath = stateDirectoryPath is null
                    ? Path.Combine(localAppDataPath, "VrcPicSorter", "Holding")
                    : Path.Combine(stateDirectoryPath, "Holding"),
                OrganizationPolicy = OrganizationPolicy.CategoryRoot,
            },
            ArchiveIndex = new ArchiveIndexState
            {
                Categories = FixedCategories
                    .Select(category => new CategoryIndexState { Category = category })
                    .ToList(),
            },
        };
    }
}

public static class AppStateValidator
{
    public static void Validate(AppStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SchemaVersion != AppStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported state schema version {state.SchemaVersion}; expected {AppStateDocument.CurrentSchemaVersion}.");
        }

        ArgumentNullException.ThrowIfNull(state.Settings);
        ArgumentNullException.ThrowIfNull(state.Settings.Automation);
        ArgumentNullException.ThrowIfNull(state.Settings.CategoryMappings);
        ArgumentNullException.ThrowIfNull(state.Settings.LegacyArchiveMappings);
        ArgumentNullException.ThrowIfNull(state.ArchiveIndex);
        ArgumentNullException.ThrowIfNull(state.ArchiveIndex.Categories);
        ArgumentNullException.ThrowIfNull(state.ReviewQueue);
        ArgumentNullException.ThrowIfNull(state.OperationJournal);
        ArgumentNullException.ThrowIfNull(state.History);

        ValidateFixedCategories(
            state.Settings.CategoryMappings.Select(mapping => mapping.Category),
            "category mappings");
        ValidateFixedCategories(
            state.ArchiveIndex.Categories.Select(category => category.Category),
            "archive index categories");

        if (string.IsNullOrWhiteSpace(state.Settings.OutputRootPath))
        {
            throw new InvalidDataException("The output root path is required.");
        }

        foreach (var category in state.ArchiveIndex.Categories)
        {
            ArgumentNullException.ThrowIfNull(category.Images);
        }

        foreach (var item in state.ReviewQueue)
        {
            ArgumentNullException.ThrowIfNull(item.RoutingContext);
            ArgumentNullException.ThrowIfNull(item.Candidates);
            foreach (var candidate in item.Candidates)
            {
                ArgumentNullException.ThrowIfNull(candidate.MatchReasons);
            }
        }
    }

    private static void ValidateFixedCategories(IEnumerable<VrcImageCategory> categories, string fieldName)
    {
        var actual = categories.Order().ToArray();
        var expected = AppStateDefaults.FixedCategories.Order().ToArray();

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"State {fieldName} must contain each fixed category exactly once.");
        }
    }
}

public static class AppStateMigrator
{
    public static bool Migrate(AppStateDocument state, AppStateDocument defaults)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(defaults);

        if (state.SchemaVersion == AppStateDocument.CurrentSchemaVersion)
        {
            return false;
        }

        if (state.SchemaVersion == 4)
        {
            // Fingerprints moved out of the state document. The store lifts them into the
            // sidecar before this runs, so nothing needs rebuilding here.
            state.SchemaVersion = AppStateDocument.CurrentSchemaVersion;
            return true;
        }

        if (state.SchemaVersion is 2 or 3)
        {
            foreach (var index in state.ArchiveIndex.Categories)
            {
                index.Status = IndexStatus.Stale;
                index.LastError = "Image fingerprints must be refreshed for the improved matcher.";
            }

            MarkUnresolvedReviewsForReconciliation(state);

            state.SchemaVersion = AppStateDocument.CurrentSchemaVersion;
            return true;
        }

        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported state schema version {state.SchemaVersion}; expected 1 through {AppStateDocument.CurrentSchemaVersion}.");
        }

        state.Settings.LegacyArchiveMappings ??= [];
        var mappings = state.Settings.CategoryMappings;
        var inferredRoot = TryInferCommonRoot(mappings);
        state.Settings.OutputRootPath = inferredRoot ?? defaults.Settings.OutputRootPath;
        state.Settings.OutputRootConfirmed = inferredRoot is not null;

        if (inferredRoot is null)
        {
            state.Settings.LegacyArchiveMappings = mappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ArchivePath))
                .Select(mapping => new LegacyArchiveMapping
                {
                    Category = mapping.Category,
                    ArchivePath = mapping.ArchivePath,
                })
                .ToList();
        }

        foreach (var mapping in mappings)
        {
            mapping.ArchivePath = Path.Combine(state.Settings.OutputRootPath, mapping.Category.ToString());
        }

        RepairRoutingContexts(state);
        MarkUnresolvedReviewsForReconciliation(state);

        foreach (var index in state.ArchiveIndex.Categories)
        {
            index.Status = IndexStatus.Stale;
            index.LastError = "Application settings were migrated to the single output-root model.";
        }

        // The migration resets the policy the same way it resets the rest of the routing settings,
        // so a state file carried across lands on the current default rather than on whatever the
        // old model happened to leave behind.
        state.Settings.OrganizationPolicy = OrganizationPolicy.CategoryRoot;
        state.Settings.Automation.WatchWhileOpen = false;
        state.SchemaVersion = AppStateDocument.CurrentSchemaVersion;
        return true;
    }

    private static void MarkUnresolvedReviewsForReconciliation(AppStateDocument state)
    {
        foreach (var review in state.ReviewQueue.Where(item => item.Status != ReviewStatus.Resolved))
        {
            review.Status = ReviewStatus.NeedsReconciliation;
        }

        foreach (var journalReview in state.OperationJournal
            .Select(entry => entry.ReviewItemAfterCommit)
            .Where(review => review is not null && review.Status != ReviewStatus.Resolved))
        {
            journalReview!.Status = ReviewStatus.NeedsReconciliation;
        }
    }

    private static void RepairRoutingContexts(AppStateDocument state)
    {
        foreach (var review in state.ReviewQueue)
        {
            if (IsComplete(review.RoutingContext))
            {
                continue;
            }

            review.RoutingContext = TryCreateRoutingContext(
                state,
                review.Category,
                review.IncomingOriginalPath) ?? new ScanRoutingContext();
            if (!IsComplete(review.RoutingContext))
            {
                review.Status = ReviewStatus.NeedsReconciliation;
            }
        }

        foreach (var entry in state.OperationJournal.Where(item => !IsComplete(item.RoutingContext)))
        {
            entry.RoutingContext = entry.ReviewItemAfterCommit is { } review && IsComplete(review.RoutingContext)
                ? review.RoutingContext
                : TryCreateRoutingContext(state, entry.Category, entry.SourcePath);
            if (!IsComplete(entry.RoutingContext)
                && entry.Phase != JournalPhase.Completed
                && entry.OperationType == JournalOperationType.Move)
            {
                entry.Phase = JournalPhase.NeedsAttention;
                entry.LastError = "Routing context could not be reconstructed during migration.";
            }
        }
    }

    private static ScanRoutingContext? TryCreateRoutingContext(
        AppStateDocument state,
        VrcImageCategory category,
        string sourcePath)
    {
        try
        {
            var sourceRoot = state.Settings.CategoryMappings.Single(item => item.Category == category).SourcePath;
            if (string.IsNullOrWhiteSpace(sourceRoot)
                || string.IsNullOrWhiteSpace(sourcePath)
                || !Path.GetFullPath(sourcePath).StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(sourcePath)!;
            var relative = Path.GetRelativePath(sourceRoot, parent);
            return new ScanRoutingContext
            {
                SourceRootPath = Path.GetFullPath(sourceRoot),
                RelativeDirectory = relative == "." ? string.Empty : relative,
                OutputRootPath = Path.GetFullPath(state.Settings.OutputRootPath),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsComplete(ScanRoutingContext? context) =>
        context is not null
        && !string.IsNullOrWhiteSpace(context.SourceRootPath)
        && !string.IsNullOrWhiteSpace(context.OutputRootPath);

    private static string? TryInferCommonRoot(IReadOnlyCollection<CategoryMapping> mappings)
    {
        if (mappings.Count != AppStateDefaults.FixedCategories.Count)
        {
            return null;
        }

        var parents = new List<string>();
        try
        {
            foreach (var mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ArchivePath)
                    || !string.Equals(
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(mapping.ArchivePath)),
                        mapping.Category.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(mapping.ArchivePath));
                if (string.IsNullOrWhiteSpace(parent))
                {
                    return null;
                }

                parents.Add(Path.GetFullPath(parent));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return parents.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? parents[0] : null;
    }
}
