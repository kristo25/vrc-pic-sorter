using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using MessageBox = System.Windows.MessageBox;

namespace VrcPicSorter.App;

public partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly PreviewService _previewService = new();
    private readonly LatestRequestGuard _reviewDisplayRequests = new();
    private readonly KeepIncomingPrompt _keepIncomingPrompt = new();
    private IReadOnlyList<ArchiveRelocationStep> _retainedArchives = [];

    private IReadOnlyList<ArchiveDuplicateGroup> _archiveDuplicates = [];
    private bool _busy;
    private bool _loadingSettings;
    private CancellationTokenSource? _operationCancellation;
    private bool _scanProgressActive;
    private Guid? _validatedIncomingReviewId;
    private bool _validatedIncomingCurrent;

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        InitializeComponent();
        SimilarityCombo.ItemsSource = Enum.GetValues<SimilarityProfile>();
        OrganizationCombo.ItemsSource = Enum.GetValues<OrganizationPolicy>();
        WatchModeCombo.ItemsSource = Enum.GetValues<WatchMode>();
    }

    private ReviewItem? SelectedReview => QueueList.SelectedItem as ReviewItem;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(selectFirst: false, refreshSettings: true);
        var state = await _runtime.StateStore.LoadAsync();
        ShowPage(ReviewPage);
        UpdateWatchingDisplay();
        if (_runtime.StateStore.LastRecoveryNotice is { } recovery)
        {
            SetStatus("Unreadable local state was quarantined and the app recovered safely.");
            var source = recovery.RestoredBackup ? "The last valid backup was restored." : "Default settings were restored.";
            MessageBox.Show(
                this,
                $"{source}\n\nThe unreadable file was preserved at:\n{recovery.QuarantinedPath}",
                "Application state recovered",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (!state.Settings.OutputRootConfirmed)
        {
            SetStatus("Confirm the migrated main output folder in Settings before scanning.");
        }
        else if (!state.Settings.CategoryMappings.Any(mapping => mapping.IsEnabled))
        {
            SetStatus("Review is ready. Configure folders in Settings before scanning.");
        }
    }

    private async Task RefreshAsync(
        Guid? selectReviewId = null,
        bool selectFirst = true,
        bool refreshSettings = false)
    {
        _reviewDisplayRequests.Invalidate();
        var state = await _runtime.StateStore.LoadAsync();
        var queue = state.ReviewQueue
            .Where(item => item.Status != ReviewStatus.Resolved)
            .OrderBy(item => item.CreatedUtc)
            .ToArray();
        QueueList.ItemsSource = queue;
        QueueCountText.Text = $"{queue.Length} pending";
        ClearQueueButton.IsEnabled = !_busy && queue.Length > 0;
        HistoryList.ItemsSource = state.History.OrderByDescending(item => item.OccurredUtc).ToArray();
        var pendingOperations = state.OperationJournal.Where(item => item.Phase != JournalPhase.Completed).ToArray();
        var attentionCount = pendingOperations.Count(item => item.Phase == JournalPhase.NeedsAttention);
        RecoveryStatusText.Text = pendingOperations.Length == 0
            ? "No file operations need attention."
            : $"{pendingOperations.Length} operation(s) pending; {attentionCount} require a decision.";
        RetryRecoveryButton.IsEnabled = !_busy && pendingOperations.Length > 0;
        DismissRecoveryButton.IsEnabled = !_busy && attentionCount > 0;
        if (refreshSettings)
        {
            LoadSettings(state.Settings);
        }

        if (queue.Length == 0)
        {
            QueueList.SelectedItem = null;
            ShowEmptyReview(
                state.Settings.OutputRootConfirmed
                && state.Settings.CategoryMappings.Any(mapping => mapping.IsEnabled));
            return;
        }

        if (!selectFirst)
        {
            var preservedReview = selectReviewId is null
                ? null
                : queue.FirstOrDefault(item => item.Id == selectReviewId);
            if (preservedReview is null)
            {
                QueueList.SelectedItem = null;
                ShowEmptyReview();
                ReviewTitle.Text = "Select a review";
                ReviewSubtitle.Text = $"{queue.Length} pending review(s). Select one from the queue to load its images.";
                return;
            }

            QueueList.SelectedItem = preservedReview;
            return;
        }

        QueueList.SelectedItem = selectReviewId is null
            ? queue[0]
            : queue.FirstOrDefault(item => item.Id == selectReviewId) ?? queue[0];
    }

    private void LoadSettings(AppSettings settings)
    {
        // Populating the controls raises the same change events that drive auto-save.
        _loadingSettings = true;
        try
        {
            LoadCategory(settings, VrcImageCategory.Emoji, EmojiSource, EmojiEnabled);
            LoadCategory(settings, VrcImageCategory.Prints, PrintsSource, PrintsEnabled);
            LoadCategory(settings, VrcImageCategory.Stickers, StickersSource, StickersEnabled);
            OutputRoot.Text = settings.OutputRootPath;
            UpdateResolvedDestinations(settings.OutputRootPath);
            SimilarityCombo.SelectedItem = settings.SimilarityProfile;
            OrganizationCombo.SelectedItem = settings.OrganizationPolicy;
            StartWithWindowsCheck.IsChecked = settings.Automation.StartWithWindows;
            StartWithWindowsCheck.IsEnabled = _runtime.AllowStartupRegistration;
            BringReviewForwardCheck.IsChecked = settings.BringReviewForwardWhenHeld;
            WatchModeCombo.SelectedItem = settings.Automation.WatchMode;
            WatchScanSeconds.Text = settings.Automation.WatchScanSeconds.ToString(
                System.Globalization.CultureInfo.CurrentCulture);
            UpdateStartupStatusText();
            var missingFolders = CaptureSettingsDraftOrNull()?.DescribeMissingFolders();
            ShowSettingsNotice(
                missingFolders ?? "Settings save automatically.",
                isWarning: missingFolders is not null);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void UpdateStartupStatusText()
    {
        var executablePath = Environment.ProcessPath;
        StartupRegistrationStatus? startupStatus = !_runtime.AllowStartupRegistration
            ? null
            : executablePath is null
            ? StartupRegistrationStatus.Stale
            : _runtime.Startup.GetStatus(executablePath);
        StartupStatusText.Text = startupStatus switch
        {
            null => "Windows startup changes are disabled for this isolated data profile.",
            StartupRegistrationStatus.Disabled => "Windows startup is not registered.",
            StartupRegistrationStatus.Current => "Windows startup points to this executable.",
            StartupRegistrationStatus.Stale => "Windows startup points to an old location. Toggle the checkbox to repair it.",
            _ => "Windows startup status is unknown.",
        };
    }

    private static void LoadCategory(
        AppSettings settings,
        VrcImageCategory category,
        System.Windows.Controls.TextBox source,
        System.Windows.Controls.CheckBox enabled)
    {
        var mapping = settings.CategoryMappings.Single(item => item.Category == category);
        source.Text = mapping.SourcePath;
        enabled.IsChecked = mapping.IsEnabled;
    }

    private void UpdateResolvedDestinations(string outputRoot)
    {
        EmojiDestinationText.Text = $"Emoji: {Path.Combine(outputRoot, nameof(VrcImageCategory.Emoji))}";
        PrintsDestinationText.Text = $"Prints: {Path.Combine(outputRoot, nameof(VrcImageCategory.Prints))}";
        StickersDestinationText.Text = $"Stickers: {Path.Combine(outputRoot, nameof(VrcImageCategory.Stickers))}";
    }

    private async void QueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var displayRequest = _reviewDisplayRequests.Begin();
        var review = SelectedReview;
        if (review is null)
        {
            ShowEmptyReview();
            return;
        }

        ReviewTitle.Text = Path.GetFileName(review.IncomingOriginalPath);
        ReviewTitle.ToolTip = review.IncomingOriginalPath;
        _validatedIncomingReviewId = review.Id;
        _validatedIncomingCurrent = false;
        MoveUniqueButton.IsEnabled = false;
        CandidateList.IsEnabled = false;
        RemoveReviewButton.IsEnabled = !_busy;
        var incomingTiming = review.IncomingImageFingerprint is { FrameCount: > 1 } incoming
            ? $" | {incoming.FrameCount} frames, {incoming.FrameDelaysMilliseconds.Sum()} ms"
            : string.Empty;
        ReviewSubtitle.Text = $"{review.Category} | {review.Candidates.Count} possible match(es) | {review.Status}{incomingTiming}";
        IncomingResolutionText.Text = review.IncomingImageFingerprint is { } incomingFingerprint
            ? $"{incomingFingerprint.Width} x {incomingFingerprint.Height}"
            : "Resolution unavailable";
        IncomingAvailabilityText.Text = "Checking incoming file...";
        IncomingLocationText.Text = review.IsIncomingInPlace
            ? $"Incoming file: {review.IncomingOriginalPath}"
            : $"Original: {review.IncomingOriginalPath}\nLegacy review copy: {review.HeldFilePath}";
        IncomingLocationText.ToolTip = IncomingLocationText.Text;
        IncomingLocationText.SetValue(
            System.Windows.Automation.AutomationProperties.HelpTextProperty,
            IncomingLocationText.Text);
        await _previewService.ShowAsync(IncomingPreview, review.HeldFilePath);
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        var incomingCurrent = await IsIncomingCurrentAsync(review);
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        _validatedIncomingReviewId = review.Id;
        _validatedIncomingCurrent = incomingCurrent;
        IncomingAvailabilityText.Text = incomingCurrent
            ? "Incoming file is available and unchanged."
            : "Incoming file is missing or changed. Remove this item or scan the file again.";

        var state = await _runtime.StateStore.LoadAsync();
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        // Grouped rather than ToDictionary: a duplicate id would otherwise throw from a
        // selection-changed handler and take the whole page down.
        var indexed = state.ArchiveIndex.Categories
            .SelectMany(category => category.Images)
            .GroupBy(image => image.Id)
            .ToDictionary(group => group.Key, group => group.First());
        CandidateList.ItemsSource = review.Candidates.Select(candidate =>
        {
            indexed.TryGetValue(candidate.IndexedImageId, out var record);
            var resolution = record is null
                ? "Resolution unavailable"
                : $"{record.Width} x {record.Height}";
            var timing = record?.Fingerprint is { FrameCount: > 1 } fingerprint
                ? $" Frames: {fingerprint.FrameCount}; duration: {fingerprint.FrameDelaysMilliseconds.Sum()} ms."
                : string.Empty;
            return new CandidatePreviewItem(
                candidate,
                $"{candidate.MatchKind} | {candidate.SimilarityScore:P1} similar",
                resolution,
                string.Join("  ", candidate.MatchReasons) + timing,
                candidate.ArchivePath);
        }).ToArray();
        CandidateHint.Text = review.Candidates.Count == 0
            ? "No remaining matches."
            : $"{review.Candidates.Count} ranked candidate(s)";
        UpdateActionAvailability(review);
    }

    private void ShowEmptyReview(bool isConfigured = true)
    {
        ReviewTitle.Text = isConfigured ? "Review queue is clear" : "Set up your image folders";
        ReviewTitle.ToolTip = null;
        ReviewSubtitle.Text = isConfigured
            ? "New matches will appear here after a scan."
            : "Open Settings to confirm the output folder and enable at least one VRCX category.";
        _previewService.Stop(IncomingPreview);
        IncomingPreview.Source = null;
        CandidateList.ItemsSource = null;
        IncomingResolutionText.Text = string.Empty;
        IncomingAvailabilityText.Text = string.Empty;
        IncomingLocationText.Text = string.Empty;
        IncomingLocationText.ToolTip = null;
        IncomingLocationText.ClearValue(System.Windows.Automation.AutomationProperties.HelpTextProperty);
        CandidateHint.Text = "No pending matches.";
        MoveUniqueButton.IsEnabled = false;
        RemoveReviewButton.IsEnabled = false;
        _validatedIncomingReviewId = null;
        _validatedIncomingCurrent = false;
    }

    private void UpdateActionAvailability(ReviewItem review)
    {
        var incomingCurrent = _validatedIncomingReviewId == review.Id
            ? _validatedIncomingCurrent
            : File.Exists(review.HeldFilePath);
        MoveUniqueButton.IsEnabled = !_busy && incomingCurrent;
        CandidateList.IsEnabled = !_busy && incomingCurrent;
        RemoveReviewButton.IsEnabled = !_busy;
        ClearQueueButton.IsEnabled = !_busy && QueueList.Items.Count > 0;
        if (!incomingCurrent)
        {
            CandidateHint.Text = "The incoming file is missing or changed. Remove this review or scan the file again.";
        }
    }

    private async Task<bool> IsIncomingCurrentAsync(ReviewItem review)
    {
        if (!File.Exists(review.HeldFilePath))
        {
            return false;
        }

        var decoded = await _runtime.Decoder.DecodeAsync(review.HeldFilePath);
        if (!decoded.IsSuccess)
        {
            return false;
        }

        // Create hashes every pixel of every frame and builds a stack of downsamples from them.
        // Awaited straight from the UI thread it ran on the UI thread, freezing the window for as
        // long as that took every time a queue row was clicked.
        var identity = await Task.Run(() => ImageFingerprint.Create(decoded.Image!).ExactIdentity);
        return identity == review.IncomingFingerprint;
    }

    private async void ScanNow(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RunBusyAsync(
            "Scanning enabled categories...",
            async () =>
            {
                BeginScanProgress();
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var processingProgress = new Progress<ScanProcessingProgress>(UpdateScanProcessingProgress);
                var results = await _runtime.Scanner.ScanAllAsync(
                    progress,
                    processingProgress,
                    CurrentCancellation);
                if (results.Count == 0)
                {
                    SetStatus("Nothing scanned: enable at least one category in Settings.");
                    MessageBox.Show(this, StatusText.Text, "Scan now", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var moved = results.Sum(result => result.MovedUnique);
                var held = results.Sum(result => result.HeldForReview);
                var autoKept = results.Sum(result => result.AutoKeptArchived);
                var examined = results.Sum(result => result.Examined);
                var skipped = results.Sum(result => result.Skipped);
                var errors = results.Sum(result => result.Errors.Count);
                SetStatus(
                    $"Scan complete: {examined} examined, {moved} archived, {autoKept} exact duplicates recycled, "
                    + $"{held} queued for review, {skipped} skipped, {errors} failed.");
                await ReportScanErrorsAsync(results.SelectMany(result => result.Errors));
                await RefreshAsync();
                ShowPage(ReviewPage);
            });
    }

    private async void ScanAnotherFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose another image folder to scan",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var categoryDialog = new CategorySelectionDialog { Owner = this };
        if (categoryDialog.ShowDialog() != true)
        {
            return;
        }

        await RunBusyAsync(
            $"Scanning {picker.FolderName}...",
            async () =>
            {
                BeginScanProgress();
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var processingProgress = new Progress<ScanProcessingProgress>(UpdateScanProcessingProgress);
                var result = await _runtime.Scanner.ScanFolderAsync(
                    picker.FolderName,
                    categoryDialog.SelectedCategory,
                    progress,
                    processingProgress,
                    CurrentCancellation);
                SetStatus(
                    $"Folder scan complete: {result.Examined} examined, {result.MovedUnique} archived, "
                    + $"{result.AutoKeptArchived} exact duplicates recycled, {result.HeldForReview} queued for review, "
                    + $"{result.Skipped} skipped, {result.Errors.Count} failed.");
                await ReportScanErrorsAsync(result.Errors);
                await RefreshAsync();
                ShowPage(ReviewPage);
            });
    }

    private async void ToggleWatching(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_runtime.Watcher.IsRunning)
        {
            await _runtime.StopWatchingAsync();
            UpdateWatchingDisplay();
            SetStatus("Watching stopped.");
            return;
        }

        await RunBusyAsync(
            "Starting folder monitoring...",
            async () =>
            {
                var state = await _runtime.StateStore.LoadAsync();
                var enabled = state.Settings.CategoryMappings.Where(mapping => mapping.IsEnabled).ToArray();
                if (enabled.Length == 0)
                {
                    throw new InvalidOperationException("Enable at least one category in Settings before starting watching.");
                }

                await _runtime.StartWatchingAsync();
                UpdateWatchingDisplay();
                SetStatus("Watching configured VRCX folders.");
            });
    }

    private void UpdateWatchingDisplay()
    {
        var running = _runtime.Watcher.IsRunning;
        WatchButton.Content = running ? "Stop watching" : "Start watching";
        WatchButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            running ? "Stop watching configured image folders" : "Start watching configured image folders");
        WatchStatusText.Text = running ? "Watching" : "Not watching";
    }

    private async void MoveAsUnique(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        if (review is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunReviewActionAsync(
            async () =>
            {
                if (!await ValidateReviewAsync(review))
                {
                    return false;
                }

                return await MoveReviewAsync(review);
            });
    }

    private async void KeepIncoming(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        var candidate = (sender as System.Windows.Controls.Button)?.CommandParameter as ReviewCandidate;
        if (review is null || candidate is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunBusyAsync(
            "Keeping incoming image...",
            async () =>
            {
                if (!await ValidateReviewAsync(review, candidate))
                {
                    return;
                }

                // Asked once per review, not once per match. Settling a review with several
                // matches takes one press each, and putting the same question behind every one of
                // them only makes a person dismiss dialogs they have already answered.
                var canRecycle = _runtime.Router.CanRecycle(candidate.ArchivePath);
                if (_keepIncomingPrompt.MustAsk(review.Id, canRecycle))
                {
                    if (MessageBox.Show(
                            this,
                            canRecycle
                                ? "Recycle this archived match and keep the incoming image? If other matches remain, the review stays open and keeping the incoming over them will not ask again."
                                : "Windows Recycle Bin is unavailable for this archive drive. Move the archived match into the VRC Pic Sorter Replaced folder and keep the incoming image? If other matches on this drive remain, they will not ask again.",
                            "Keep incoming",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) != MessageBoxResult.OK)
                    {
                        SetStatus("Keep incoming canceled.");
                        return;
                    }

                    _keepIncomingPrompt.Agreed(review.Id, canRecycle);
                }

                KeepIncomingResult result;
                try
                {
                    result = await _runtime.Router.KeepIncomingOverMatchAsync(review, candidate);
                }
                catch
                {
                    await RefreshAsync();
                    throw;
                }

                if (result.ReviewResolved)
                {
                    _keepIncomingPrompt.Forget();
                }

                await RefreshAsync(result.ReviewResolved ? null : review.Id);
                SetStatus(result.PreservedMatchPath is not null
                    ? $"Incoming image kept. The previous match was preserved at {result.PreservedMatchPath}"
                    : result.ReviewResolved
                        ? "Incoming image kept and moved into the archive."
                        : "Archived match removed. Review the remaining matches.");
            });
    }

    private async void KeepMatch(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        var candidate = (sender as System.Windows.Controls.Button)?.CommandParameter as ReviewCandidate;
        if (review is null || candidate is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunReviewActionAsync(
            async () =>
            {
                if (!await ValidateReviewAsync(review, candidate))
                {
                    return false;
                }

                if (!_runtime.Router.CanRecycle(review.HeldFilePath))
                {
                    MessageBox.Show(this, "This incoming file location does not support the Windows Recycle Bin.", "Keep match unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // No confirmation: the incoming image goes to the Recycle Bin, the archived copy
                // is verified unchanged first, and the operation is journaled, so this is
                // reversible without a prompt.
                await _runtime.Router.KeepMatchAsync(review, candidate);
                return true;
            });
    }

    private async Task RefreshChangedReviewAsync()
    {
        SetStatus("That review changed before the action could start. Refreshing the queue...");
        await RefreshAsync();
    }

    private async Task<bool> MoveReviewAsync(ReviewItem review)
    {
        if (review.IncomingImageFingerprint is null)
        {
            MessageBox.Show(this, "The incoming fingerprint is missing. Run a new scan.", "Cannot move", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        await _runtime.Router.MoveUniqueAsync(
                review.HeldFilePath,
                review.Category,
                review.IncomingImageFingerprint,
                review.RoutingContext,
                reviewItemId: review.Id,
                duplicateOverride: false);
        return true;
    }

    private async void ClearReviewQueue(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        var state = await _runtime.StateStore.LoadAsync();
        var reviews = state.ReviewQueue
            .Where(review => review.Status != ReviewStatus.Resolved)
            .OrderBy(review => review.CreatedUtc)
            .ToArray();
        if (reviews.Length == 0)
        {
            return;
        }

        var legacyHeldCount = reviews.Count(review => !review.IsIncomingInPlace);
        var message = legacyHeldCount == 0
            ? $"Remove {reviews.Length} item(s) from the review queue? The incoming files will remain untouched."
            : $"Clear {reviews.Length} review item(s)? In-place incoming files will remain untouched, and {legacyHeldCount} legacy held file(s) will be returned to their original folders.";
        if (MessageBox.Show(
                this,
                message,
                "Clear review queue",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Clearing review queue...",
            async () =>
            {
                BeginReturnProgress(reviews.Length);
                var progress = new Progress<ReviewRestoreProgress>(UpdateReturnProgress);
                var result = await _runtime.Router.RestoreReviewsAsync(reviews, progress, CurrentCancellation);

                await RefreshAsync(selectFirst: false);
                SetStatus(result.Failures.Count == 0
                    ? $"Review queue cleared. {result.Restored} item(s) removed."
                    : $"Cleared {result.Restored} item(s); {result.Failures.Count} review(s) remain.");
                if (result.Failures.Count > 0)
                {
                    MessageBox.Show(this, string.Join(Environment.NewLine, result.Failures.Take(8)), "Some reviews could not be cleared", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
    }

    private async void RemoveSelectedReview(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        var review = SelectedReview;
        if (review is null)
        {
            return;
        }

        var message = review.IsIncomingInPlace
            ? "Remove this item from the Review queue? The incoming file will remain untouched."
            : File.Exists(review.HeldFilePath)
                ? "Return this legacy held image to its original folder and remove it from the Review queue?"
                : "Remove this unavailable legacy review entry? No image file was found to move.";
        if (MessageBox.Show(
                this,
                message,
                "Remove review item",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Removing review item...",
            async () =>
            {
                if (review.IsIncomingInPlace || !File.Exists(review.HeldFilePath))
                {
                    await _runtime.Router.DismissReviewAsync(review.Id);
                }
                else
                {
                    await _runtime.Router.RestoreReviewAsync(review);
                }

                await RefreshAsync(selectFirst: false);
                SetStatus("Review item removed. The incoming image was not deleted.");
            });
    }

    /// <param name="candidate">
    /// The candidate this action will touch, or null when the action does not involve one.
    /// Only that candidate is re-decoded: checking every candidate meant decoding all of them on
    /// every button press, and FileRouter verifies again immediately before it touches a file.
    /// </param>
    private async Task<bool> ValidateReviewAsync(ReviewItem review, ReviewCandidate? candidate = null)
    {
        if (!File.Exists(review.HeldFilePath))
        {
            MessageBox.Show(this, "The incoming file is missing. Remove this review item or scan the file again.", "Review changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (review.Status == ReviewStatus.NeedsReconciliation)
        {
            return await ReconcileReviewAsync(review);
        }

        if (candidate is null)
        {
            return true;
        }

        var decoded = await _runtime.Decoder.DecodeAsync(candidate.ArchivePath);
        if (decoded.IsSuccess)
        {
            var identity = await Task.Run(() => ImageFingerprint.Create(decoded.Image!).ExactIdentity);
            if (identity == candidate.ExpectedFingerprint)
            {
                return true;
            }
        }

        var staleIds = new List<Guid> { candidate.Id };
        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                var current = state.ReviewQueue.SingleOrDefault(item => item.Id == review.Id);
                if (current is null)
                {
                    return false;
                }

                current.Candidates.RemoveAll(item => staleIds.Contains(item.Id));
                current.Status = ReviewStatus.Pending;
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Warning,
                    Level = ActivityLevel.Warning,
                    Category = current.Category,
                    Message = $"Removed {staleIds.Count} changed or missing candidate(s) from a pending review.",
                    SourcePath = current.HeldFilePath,
                });

                return true;
            });
        await RefreshAsync(review.Id);
        MessageBox.Show(this, "That archive match changed or disappeared, so its stale reference was removed. Review the remaining matches before choosing a terminal action.", "Review reconciled", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task<bool> ReconcileReviewAsync(ReviewItem review)
    {
        SetStatus("Refreshing this review with the current matcher...");
        var incomingDecoded = await _runtime.Decoder.DecodeAsync(review.HeldFilePath);
        if (!incomingDecoded.IsSuccess)
        {
            MessageBox.Show(
                this,
                "The incoming image could not be decoded. Remove this review item or scan the file again.",
                "Review refresh failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var incomingFingerprint = await Task.Run(() => ImageFingerprint.Create(incomingDecoded.Image!));
        var refreshedIndex = await _runtime.Indexer.RefreshAsync(review.Category);
        if (refreshedIndex.Status != IndexStatus.Current || refreshedIndex.Errors.Count > 0)
        {
            MessageBox.Show(
                this,
                "The archive index could not be refreshed completely. This review remains locked for reconciliation; check History for the indexing errors and try again.",
                "Review refresh incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var currentState = await _runtime.StateStore.LoadAsync();
        var indexedCategory = currentState.ArchiveIndex.Categories
            .Single(category => category.Category == review.Category);
        var indexedByKey = indexedCategory.Images
            .Where(image => image.Fingerprint is { HasCurrentFeatures: true })
            .ToDictionary(image => image.Id.ToString("D"), StringComparer.Ordinal);
        var ranked = ImageMatcher.RankCandidates(
            incomingFingerprint,
            indexedByKey.Select(pair => new ImageCandidate(pair.Key, pair.Value.Fingerprint!)),
            currentState.Settings.SimilarityProfile);
        var refreshedCandidates = ranked
            .Select(match =>
            {
                var indexed = indexedByKey[match.CandidateKey];
                return new ReviewCandidate
                {
                    Id = Guid.NewGuid(),
                    IndexedImageId = indexed.Id,
                    ArchivePath = indexed.Path,
                    ExpectedFingerprint = indexed.Fingerprint!.ExactIdentity,
                    MatchKind = match.MatchKind,
                    SimilarityScore = match.SimilarityScore,
                    MatchReasons = match.MatchReasons.ToList(),
                };
            })
            .ToList();

        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                var current = state.ReviewQueue.Single(item => item.Id == review.Id);
                current.IncomingFingerprint = incomingFingerprint.ExactIdentity;
                current.IncomingImageFingerprint = incomingFingerprint;
                current.IndexGeneration = refreshedIndex.Generation;
                current.Candidates = refreshedCandidates;
                current.Status = ReviewStatus.Pending;
                return true;
            });

        await RefreshAsync(review.Id);
        MessageBox.Show(
            this,
            refreshedCandidates.Count == 0
                ? "This image no longer matches anything in the archive. Review it, then choose Move as Unique."
                : "This review was rescanned with the current matcher. Review the refreshed matches, then choose an action again.",
            "Review refreshed",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private async Task RunReviewActionAsync(Func<Task<bool>> action)
    {
        await RunBusyAsync(
            "Applying review decision...",
            async () =>
            {
                if (!await action())
                {
                    return;
                }

                await RefreshAsync();
                StatusText.Text = "Review decision completed.";
                ReviewNavButton.Focus();
            });
    }

    private void SettingsToggled(object sender, RoutedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsFieldCommitted(object sender, RoutedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        BeginAutoSaveSettings();
    }

    private void BeginAutoSaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _ = AsyncCommandRunner.RunAsync(AutoSaveSettingsAsync, ReportSettingsAutoSaveFailureAsync);
    }

    /// <summary>
    /// Persists the settings controls as soon as a field is committed. Folder-existence problems
    /// are reported inline rather than refused, because a watched folder is allowed to appear
    /// later; only the overlap invariants that could make the app scan its own archive block a save.
    /// </summary>
    private async Task AutoSaveSettingsAsync()
    {
        var draft = CaptureSettingsDraftOrNull();
        if (draft is null)
        {
            ShowSettingsNotice("Choose a main output folder.", isWarning: true);
            return;
        }

        var settings = (await _runtime.StateStore.LoadAsync()).Settings;
        if (draft.DescribeBlockingProblem(settings) is { } blocking)
        {
            ShowSettingsNotice(blocking, isWarning: true);
            return;
        }

        // Worked out before the draft is applied, because it is the archive paths as they stand
        // that say where the images are now.
        IReadOnlyList<ArchiveRelocationStep> relocation = string.Equals(
            settings.ArchiveRelocationAnsweredFor,
            Path.GetFullPath(draft.OutputRootPath),
            StringComparison.OrdinalIgnoreCase)
            ? []
            : await Task.Run(() => ArchiveRelocation.Plan(settings, draft.OutputRootPath));

        var startupChanged = settings.Automation.StartWithWindows != draft.StartWithWindows;
        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                draft.ApplyTo(state);
                return true;
            });

        // A running watcher holds the folders, mode and interval it was started with, so it has
        // to be restarted for any settings change to take effect.
        if (startupChanged || _runtime.Watcher.IsRunning)
        {
            await _runtime.ApplyAutomationSettingsAsync(updateStartupRegistration: startupChanged);
        }

        await OfferToBringTheArchiveAlongAsync(draft.OutputRootPath, relocation);
        await RefreshRetainedArchivesAsync();

        UpdateResolvedDestinations(draft.OutputRootPath);
        UpdateStartupStatusText();
        var missing = draft.DescribeMissingFolders();
        ShowSettingsNotice(
            missing ?? $"Settings saved at {DateTime.Now:t}.",
            isWarning: missing is not null);
    }

    /// <summary>
    /// Offers to move an archive that the new output folder has left behind, and remembers the
    /// answer so the question is asked once rather than on every save.
    /// </summary>
    /// <remarks>
    /// Whichever way it is answered, the new location is made ready. Saying no should cost nothing
    /// beyond the files staying where they are: both archives go on being indexed, and new images
    /// are filed in the new one.
    /// </remarks>
    private async Task OfferToBringTheArchiveAlongAsync(
        string outputRoot,
        IReadOnlyList<ArchiveRelocationStep> relocation)
    {
        if (relocation.Count == 0)
        {
            return;
        }

        var files = relocation.Sum(step => step.FileCount);
        var gigabytes = relocation.Sum(step => step.TotalBytes) / (double)(1024 * 1024 * 1024);
        var folders = string.Join(
            Environment.NewLine,
            relocation.Select(step => $"    {step.From}  ({step.FileCount} images)"));

        var move = MessageBox.Show(
            this,
            $"{files} images are still filed in your previous archive:{Environment.NewLine}{Environment.NewLine}"
                + $"{folders}{Environment.NewLine}{Environment.NewLine}"
                + $"Move them into {outputRoot}? ({gigabytes:0.##} GB){Environment.NewLine}{Environment.NewLine}"
                + "Either way the new folder is set up and new images are filed there. Moving also "
                + "frees the old folders to be scanned like any other.",
            "Bring your archive along?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Recorded before the move runs. A move interrupted half way still leaves the question
        // answered, and what it managed to move is described by the index either way.
        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                state.Settings.ArchiveRelocationAnsweredFor = Path.GetFullPath(outputRoot);
                return true;
            });

        if (!move)
        {
            foreach (var step in relocation)
            {
                TryCreateFolder(step.To);
            }

            ShowSettingsNotice(
                "Your previous archive was left where it is. Both folders stay indexed, so "
                    + "duplicates are still found across them.",
                isWarning: false);
            return;
        }

        await MoveArchivesAsync(relocation);
    }

    /// <summary>
    /// Moves the planned archives, points the index at where the files landed, and forgets the
    /// folders that emptied.
    /// </summary>
    /// <remarks>
    /// Shared by the question asked when the output folder changes and by the button in Settings,
    /// so the two cannot drift into doing subtly different things to a person's archive.
    /// </remarks>
    private async Task MoveArchivesAsync(IReadOnlyList<ArchiveRelocationStep> relocation)
    {
        if (relocation.Count == 0)
        {
            return;
        }

        await RunBusyAsync(
            "Moving your archive...",
            async () =>
            {
                var progress = new Progress<ArchiveRelocationProgress>(
                    value => SetStatus($"Moving your archive: {value.Moved} of {value.Total} - {value.FileName}"));
                var result = await ArchiveRelocation.RelocateAsync(relocation, progress, CurrentCancellation);

                await _runtime.StateStore.UpdateAsync(
                    state =>
                    {
                        // Only the folders that actually emptied are forgotten. One that kept a
                        // file back is still a place images live.
                        var emptied = relocation.Where(step => IsEmptyNow(step.From)).ToArray();

                        // Rebased from the files that moved, not from the folders it was tried on.
                        // A collision or a failure leaves that record where it was, and pointing it
                        // at the destination anyway named a file that had never arrived - or, when
                        // the collision was a different image under the same name, the wrong one.
                        ArchiveRelocation.RebaseIndex(state, result.Moves);
                        ArchiveRelocation.ForgetRelocated(state.Settings, emptied);
                        return true;
                    });

                await RefreshAsync();
                await RefreshRetainedArchivesAsync();
                SetStatus(
                    (result.Stopped, result.LeftBehind) switch
                    {
                        // A stopped run is not a finished one. It reported the same sentence as a
                        // clean finish, so a move interrupted half way looked like a move that had
                        // brought everything across.
                        (true, _) => $"Stopped after moving {result.Moved} images; the rest are still where they were.",
                        (false, 0) => $"Moved {result.Moved} images into your archive.",
                        _ => $"Moved {result.Moved} images; {result.LeftBehind} were left where they are.",
                    });
                await ReportScanErrorsAsync(result.Errors);
            });
    }

    /// <summary>
    /// Shows the archive folders an output folder change has left behind, or hides the section
    /// when there are none.
    /// </summary>
    private async Task RefreshRetainedArchivesAsync()
    {
        if (RetainedArchivesPanel is null)
        {
            return;
        }

        var settings = (await _runtime.StateStore.LoadAsync()).Settings;

        // Planning counts and measures every file in every retained folder, so it is kept off the
        // thread drawing the window. An archive of any size would otherwise freeze the page as it
        // opened.
        IReadOnlyList<ArchiveRelocationStep> steps = string.IsNullOrWhiteSpace(settings.OutputRootPath)
            ? []
            : await Task.Run(() => ArchiveRelocation.Plan(settings, settings.OutputRootPath));

        _retainedArchives = steps;
        RetainedArchivesList.ItemsSource = steps
            .Select(step => new
            {
                step.From,
                Summary = $"{step.FileCount} images, {step.TotalBytes / (double)(1024 * 1024):0.#} MB",
            })
            .ToArray();

        RetainedArchivesPanel.Visibility = steps.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// What the archive is holding more than once. Read from the index that is already built, so
    /// this costs no disk work - but only what the index knows about is considered, which for a
    /// category that has never been scanned is nothing.
    /// </summary>
    private async Task RefreshArchiveDuplicatesAsync()
    {
        if (ArchiveDuplicatesPanel is null)
        {
            return;
        }

        var state = await _runtime.StateStore.LoadAsync();
        var groups = state.ArchiveIndex.Categories
            .SelectMany(ArchiveDuplicateFinder.Find)
            .ToArray();
        _archiveDuplicates = groups;

        var identical = groups.Where(group => group.Kind == ArchiveDuplicateKind.Identical).ToArray();
        var sameEmoji = groups.Length - identical.Length;
        var extras = identical.Sum(group => group.Extras.Count);
        var megabytes = identical.Sum(group => group.ReclaimableBytes) / (double)(1024 * 1024);

        ArchiveDuplicatesList.ItemsSource = groups
            .SelectMany(group => group.Extras.Select(extra => new
            {
                Extra = Path.GetFileName(extra.Path),
                Detail = $"{extra.Path}{Environment.NewLine}kept instead: {group.Keep.Path}",
                Summary = group.Kind == ArchiveDuplicateKind.Identical
                    ? "identical"
                    : $"{extra.Width}x{extra.Height} beside {group.Keep.Width}x{group.Keep.Height}",
            }))
            .ToArray();

        var identicalText = extras == 1
            ? $"One extra copy is the same picture as one already here, taking {megabytes:0.#} MB."
            : $"{extras} extra copies are the same picture as one already here, taking {megabytes:0.#} MB.";
        var sameEmojiText = sameEmoji == 1
            ? "One image is the same emoji as another copy here, at a different size."
            : $"{sameEmoji} images are the same emoji as another copy here, at a different size.";
        var judgement = " Which of those is worth keeping is yours to decide, so nothing here will touch them.";

        // Read from the index, so it describes the archive as the app last saw it. Saying so is
        // the difference between "you have three duplicates" and "three is what I can see".
        const string provenance = " Counted from the archive index; run a scan first if the folder"
            + " has changed outside the app.";
        ArchiveDuplicatesSummary.Text = (extras, sameEmoji) switch
        {
            (0, _) => sameEmojiText + judgement + provenance,
            (_, 0) => identicalText + provenance,
            _ => identicalText + " Another " + sameEmojiText[..1].ToLowerInvariant() + sameEmojiText[1..]
                + judgement + provenance,
        };

        RemoveIdenticalDuplicatesButton.IsEnabled = extras > 0;
        ArchiveDuplicatesPanel.Visibility = groups.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void RemoveIdenticalDuplicates(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // The copy each extra is being removed in favour of travels with it. Dropping it here and
        // passing the extras alone is what let a removal go ahead with the keeper already gone -
        // the router had nothing to check, so there was nothing to refuse.
        var removals = _archiveDuplicates
            .Where(group => group.Kind == ArchiveDuplicateKind.Identical)
            .SelectMany(group => group.Extras.Select(extra => (group.Keep, Extra: extra)))
            .ToArray();
        if (removals.Length == 0)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Send {removals.Length} extra copies to the Recycle Bin? Every one of them is the same "
                + "picture, byte for byte, as another copy that stays. Nothing is deleted "
                + "permanently, and both files are checked before either is touched: the copy "
                + "going, and the copy it is going in favour of.",
            "Recycle the identical extras",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            $"Recycling {removals.Length} duplicate copies...",
            async () =>
            {
                var removed = 0;
                var failures = new List<string>();
                foreach (var removal in removals)
                {
                    CurrentCancellation.ThrowIfCancellationRequested();
                    try
                    {
                        await _runtime.Router.RemoveArchivedDuplicateAsync(
                            removal.Extra,
                            removal.Keep,
                            CurrentCancellation);
                        removed++;
                    }
                    catch (Exception exception) when (
                        exception is NotSupportedException
                            or InvalidOperationException
                            or IOException
                            or UnauthorizedAccessException)
                    {
                        // One copy that changed, a keeper that is no longer there, or a drive that
                        // cannot recycle, must not stop the rest. Nothing is ever deleted outright
                        // to get past it.
                        failures.Add($"{removal.Extra.Path}: {exception.Message}");
                    }
                }

                await RefreshArchiveDuplicatesAsync();
                await RefreshAsync();
                SetStatus(
                    failures.Count == 0
                        ? $"Recycled {removed} duplicate copies."
                        : $"Recycled {removed} duplicate copies; {failures.Count} were left alone.");
                await ReportScanErrorsAsync(failures);
            });
    }

    private async void MoveRetainedArchives(object sender, RoutedEventArgs e)
    {
        var relocation = _retainedArchives;
        if (relocation.Count == 0)
        {
            await RefreshRetainedArchivesAsync();
            return;
        }

        var images = relocation.Sum(step => step.FileCount);
        var destination = (await _runtime.StateStore.LoadAsync()).Settings.OutputRootPath;
        if (MessageBox.Show(
                this,
                $"Move {images} images into {destination}?{Environment.NewLine}{Environment.NewLine}"
                    + "Nothing is deleted. A file whose name is already taken in your archive stays "
                    + "where it is and is reported.",
                "Move retained archives",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            SetStatus("Move canceled.");
            return;
        }

        await MoveArchivesAsync(relocation);
    }

    /// <summary>True when nothing is left in the folder, and false if that cannot be established.</summary>
    /// <remarks>
    /// A folder that cannot be read is treated as still holding something. Forgetting an archive
    /// that turns out to still have images in it would leave those images unindexed and invisible.
    /// </remarks>
    private static bool IsEmptyNow(string path)
    {
        try
        {
            return !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryCreateFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Scanning creates the folder on demand anyway; this only saves a person the surprise
            // of an output folder that does not exist yet.
        }
    }

    private async Task ReportSettingsAutoSaveFailureAsync(Exception exception)
    {
        var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        await Dispatcher.InvokeAsync(
            () => ShowSettingsNotice($"Settings were not saved. {exception.Message}{detail}", isWarning: true));
    }

    private void ShowSettingsNotice(string? message, bool isWarning)
    {
        SettingsNoticeText.Text = message ?? string.Empty;
        SettingsNoticeText.Foreground = isWarning && !string.IsNullOrWhiteSpace(message)
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
    }

    private SettingsDraft? CaptureSettingsDraftOrNull()
    {
        try
        {
            return CaptureSettingsDraft();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private SettingsDraft CaptureSettingsDraft()
    {
        var outputRoot = OutputRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new InvalidOperationException("Choose a main output folder.");
        }

        return new SettingsDraft(
            [
                new CategorySettingsDraft(VrcImageCategory.Emoji, EmojiSource.Text.Trim(), EmojiEnabled.IsChecked == true),
                new CategorySettingsDraft(VrcImageCategory.Prints, PrintsSource.Text.Trim(), PrintsEnabled.IsChecked == true),
                new CategorySettingsDraft(VrcImageCategory.Stickers, StickersSource.Text.Trim(), StickersEnabled.IsChecked == true),
            ],
            outputRoot,
            (SimilarityProfile?)SimilarityCombo.SelectedItem ?? SimilarityProfile.Conservative,
            StartWithWindowsCheck.IsChecked == true,
            BringReviewForwardCheck.IsChecked == true,
            ParseWatchScanSeconds(WatchScanSeconds.Text),
            (WatchMode?)WatchModeCombo.SelectedItem ?? WatchMode.OnDetection,
            (OrganizationPolicy?)OrganizationCombo.SelectedItem ?? OrganizationPolicy.CategoryRoot);
    }

    private static int ParseWatchScanSeconds(string? text) =>
        int.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.CurrentCulture,
            out var seconds)
            ? Math.Clamp(
                seconds,
                AutomationSettings.MinimumWatchScanSeconds,
                AutomationSettings.MaximumWatchScanSeconds)
            : AutomationSettings.DefaultWatchScanSeconds;

    private async void RebuildIndexes(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "Rebuilding enabled archive indexes...",
            async () =>
            {
                var state = await _runtime.StateStore.LoadAsync();
                foreach (var mapping in state.Settings.CategoryMappings.Where(item => item.IsEnabled))
                {
                    await _runtime.Indexer.RebuildAsync(mapping.Category, CurrentCancellation);
                }

                StatusText.Text = "Archive indexes rebuilt.";
                await RefreshAsync();
            });
    }

    private async void ClearLocalData(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        if (MessageBox.Show(
                this,
                "Clear settings, cache, index, and history? Images are never removed by this action.",
                "Clear local data",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Clearing local application data...",
            async () =>
            {
                var wasWatching = _runtime.Watcher.IsRunning;
                await _runtime.StopWatchingAsync();
                ClearLocalDataResult result;
                try
                {
                    result = await _runtime.StateStore.TryClearLocalDataAsync();
                }
                catch
                {
                    if (wasWatching)
                    {
                        await _runtime.StartWatchingAsync();
                    }

                    throw;
                }
                if (!result.WasCleared)
                {
                    if (wasWatching)
                    {
                        await _runtime.StartWatchingAsync();
                    }

                    if (result.Status == ClearLocalDataStatus.BlockedByHeldFiles)
                    {
                        var holdingRoot = (await _runtime.StateStore.LoadAsync()).Settings.HoldingRootPath;
                        var open = MessageBox.Show(
                            this,
                            $"Local data was not cleared because orphaned images remain in the holding folder. Move them somewhere safe first.\n\n{holdingRoot}\n\nOpen this folder now?",
                            "Held images need attention",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning);
                        if (open == MessageBoxResult.OK)
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(
                                    new System.Diagnostics.ProcessStartInfo(holdingRoot) { UseShellExecute = true });
                            }
                            catch (Exception exception) when (
                                exception is System.ComponentModel.Win32Exception
                                    or IOException
                                    or InvalidOperationException)
                            {
                                MessageBox.Show(
                                    this,
                                    $"The holding folder could not be opened. Open it manually:\n\n{holdingRoot}\n\n{exception.Message}",
                                    "Could not open folder",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            }
                        }

                        return;
                    }

                    var message = result.Status == ClearLocalDataStatus.BlockedByUnsafeHoldingRoot
                        ? "Local data was not cleared because the stored holding folder is outside the application-data boundary."
                        : "Local data cannot be cleared while reviews or file operations are pending.";
                    MessageBox.Show(this, message, "Clear blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_runtime.AllowStartupRegistration && Environment.ProcessPath is { } executablePath)
                {
                    _runtime.Startup.SetEnabled(false, executablePath);
                }

                _ = await _runtime.StateStore.LoadAsync();
                await RefreshAsync(refreshSettings: true);
                SetStatus("Local application data cleared. Watching and Windows startup were stopped.");
            });
    }

    private async void RetryRecovery(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "Retrying recoverable file operations...",
            async () =>
            {
                var decisions = await _runtime.Router.RecoverPendingOperationsAsync(CurrentCancellation);
                await RefreshAsync(selectFirst: false);
                var unresolved = (await _runtime.StateStore.LoadAsync()).OperationJournal.Count;
                SetStatus($"Recovery checked {decisions.Count} operation(s); {unresolved} still need attention.");
            });
    }

    private async void DismissRecovery(object sender, RoutedEventArgs e)
    {
        if (_busy
            || MessageBox.Show(
                this,
                "Dismiss all ambiguous operations? No files will be moved or deleted. Affected indexes will be rebuilt on the next scan.",
                "Dismiss recovery operations",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Dismissing ambiguous operations...",
            async () =>
            {
                var dismissed = await _runtime.Router.DismissNeedsAttentionOperationsAsync();
                await RefreshAsync(selectFirst: false);
                SetStatus($"Dismissed {dismissed} operation(s) without changing files.");
            });
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string targetName }
            || FindName(targetName) is not System.Windows.Controls.TextBox target)
        {
            return;
        }

        var picker = new OpenFolderDialog
        {
            Title = "Choose folder",
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : null,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) == true)
        {
            target.Text = picker.FolderName;
            if (ReferenceEquals(target, OutputRoot))
            {
                UpdateResolvedDestinations(target.Text);
            }
        }
    }

    private void OutputRootChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (EmojiDestinationText is not null)
        {
            UpdateResolvedDestinations(OutputRoot.Text.Trim());
        }
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        _busy = true;
        _operationCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ScanAnotherButton.IsEnabled = false;
        WatchButton.IsEnabled = false;
        RetryRecoveryButton.IsEnabled = false;
        DismissRecoveryButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SetStatus(status);
        if (SelectedReview is { } review)
        {
            UpdateActionAvailability(review);
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Every file operation is journaled and verified before it runs, so stopping
            // between images can never leave one half-moved.
            SetStatus("Stopped. Images already handled are done; nothing was left half-moved.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            SetStatus("Action failed. No image was deleted and no permanent deletion fallback was used.");
            var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
            var details = logPath is null
                ? "\n\nThe diagnostic log could not be written."
                : $"\n\nDetails were written to:\n{logPath}";
            MessageBox.Show(this, exception.Message + details, "VRC Pic Sorter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndScanProgress();
            _busy = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            StopButton.IsEnabled = false;
            ScanButton.IsEnabled = true;
            ScanAnotherButton.IsEnabled = true;
            WatchButton.IsEnabled = true;
            UpdateWatchingDisplay();
            if (SelectedReview is { } currentReview)
            {
                UpdateActionAvailability(currentReview);
            }
        }
    }

    private CancellationToken CurrentCancellation => _operationCancellation?.Token ?? CancellationToken.None;

    private void StopCurrentOperation(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not { IsCancellationRequested: false })
        {
            return;
        }

        StopButton.IsEnabled = false;
        SetStatus("Stopping after the current image...");
        _operationCancellation.Cancel();

        // A scan started by folder watching runs on its own token, so cancel that too.
        _runtime.Watcher.CancelActiveScan();
    }

    public void ReportWatchDetection(VrcImageCategory category, string fileName) =>
        SetStatus($"New entry detected: {fileName} ({category}).");

    private static string DescribeStep(string activity, string? fileName, string fallback)
    {
        var label = string.IsNullOrWhiteSpace(activity) ? fallback : activity;
        return string.IsNullOrWhiteSpace(fileName) ? label : $"{label}: {fileName}";
    }

    private void ShowReviewPage(object sender, RoutedEventArgs e) => ShowPage(ReviewPage);

    private async void ShowAnimationsPage(object sender, RoutedEventArgs e)
    {
        ShowPage(AnimationsPage);
        await RefreshAnimationsAsync();
    }

    private void ShowHistoryPage(object sender, RoutedEventArgs e) => ShowPage(HistoryPage);

    private void ShowSettingsPage(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage);

        // Counted when the page is opened rather than held from startup: a scan or a move in the
        // meantime changes what is actually left behind.
        _ = AsyncCommandRunner.RunAsync(
            async () =>
            {
                await RefreshRetainedArchivesAsync();
                await RefreshArchiveDuplicatesAsync();
            },
            exception => Dispatcher.InvokeAsync(
                    () => ShowSettingsNotice(
                        $"Could not check the archive. {exception.Message}",
                        isWarning: true))
                .Task);
    }

    private void ShowPage(UIElement page)
    {
        ReviewPage.Visibility = page == ReviewPage ? Visibility.Visible : Visibility.Collapsed;
        AnimationsPage.Visibility = page == AnimationsPage ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == HistoryPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        ReviewNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == ReviewPage ? "Current page" : string.Empty);
        AnimationsNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == AnimationsPage ? "Current page" : string.Empty);
        HistoryNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == HistoryPage ? "Current page" : string.Empty);
        SettingsNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == SettingsPage ? "Current page" : string.Empty);

        if (page != AnimationsPage)
        {
            StopAnimationPreview();
        }

        if (IsLoaded)
        {
            _ = page == ReviewPage
                ? ReviewHeading.Focus()
                : page == AnimationsPage
                    ? AnimationsHeading.Focus()
                    : page == HistoryPage
                        ? HistoryHeading.Focus()
                        : SettingsHeading.Focus();
        }
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IncomingScale is null)
        {
            return;
        }

        IncomingScale.ScaleX = e.NewValue;
        IncomingScale.ScaleY = e.NewValue;
    }

    private async void CandidatePreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image { Tag: string path } preview)
        {
            await _previewService.ShowAsync(preview, path, decodeWidth: 360);
        }
    }

    private void CandidatePreviewUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image preview)
        {
            _previewService.Stop(preview);
        }
    }

    public Task RefreshFromExternalAsync() => RefreshAsync(SelectedReview?.Id, selectFirst: false);

    public async Task ScanFromTrayAsync()
    {
        var results = await _runtime.Scanner.ScanAllAsync();
        var refreshTask = await Dispatcher.InvokeAsync(() => RefreshAsync());
        await refreshTask;
        var queued = results.Sum(result => result.HeldForReview);
        await Dispatcher.InvokeAsync(() => SetStatus($"Background scan complete: {queued} queued for review."));
    }

    public void ReportWatcherFailure(VrcImageCategory category, string message, string? logPath)
    {
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        SetStatus($"Watching {category} failed: {message}.{detail}");
        WatchStatusText.Text = "Watching - error";
    }

    public void ReportBackgroundFailure(string operation, string message, string? logPath)
    {
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        SetStatus($"{operation} failed: {message}.{detail}");
    }

    private async Task ReportScanErrorsAsync(IEnumerable<string> errors)
    {
        var failures = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var exception = new InvalidOperationException(string.Join(Environment.NewLine, failures));
        var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
        var details = logPath is null ? string.Empty : $"\n\nFull details:\n{logPath}";
        MessageBox.Show(
            this,
            $"{failures.Length} scan issue(s) occurred.\n\n{string.Join(Environment.NewLine, failures.Take(5))}{details}",
            "Scan completed with warnings",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void BeginScanProgress()
    {
        _scanProgressActive = true;
        ScanProgressLabel.Text = "Reading";
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = 1;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "Counting";
        ScanFileText.Text = string.Empty;
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = 1;
        ProcessingProgressBar.Value = 0;
        ProcessingProgressText.Text = string.Empty;
        ProcessingActivityText.Text = string.Empty;
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Maximum = Math.Max(1, progress.TotalImages);
        ScanProgressBar.Value = Math.Min(progress.ScannedImages, ScanProgressBar.Maximum);
        ScanProgressText.Text = $"{progress.ScannedImages} / {progress.TotalImages}";
        ScanProgressLabel.Text = string.IsNullOrWhiteSpace(progress.Activity) ? "Reading" : progress.Activity;
        ScanFileText.Text = progress.FileName ?? string.Empty;
        SetStatus(
            $"{progress.Category} - {DescribeStep(progress.Activity, progress.FileName, "Reading")} "
            + $"({progress.ScannedImages} of {progress.TotalImages}).");
    }

    private void UpdateScanProcessingProgress(ScanProcessingProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ProcessingProgressPanel.Visibility = Visibility.Visible;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Maximum = Math.Max(1, progress.TotalImages);
        ProcessingProgressBar.Value = Math.Min(progress.ProcessedImages, ProcessingProgressBar.Maximum);
        ProcessingProgressText.Text = $"{progress.ProcessedImages} / {progress.TotalImages}";
        ProcessingActivityText.Text = DescribeStep(progress.Activity, progress.FileName, "Processing");
    }

    private void BeginReturnProgress(int total)
    {
        _scanProgressActive = true;
        ScanProgressLabel.Text = "Queue";
        ScanProgressPanel.Visibility = Visibility.Visible;
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = Math.Max(1, total);
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = $"0 / {total}";
        ScanFileText.Text = string.Empty;
        SetStatus($"Clearing review queue: 0 of {total} processed.");
    }

    private void UpdateReturnProgress(ReviewRestoreProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Maximum = Math.Max(1, progress.Total);
        ScanProgressBar.Value = Math.Min(progress.Processed, ScanProgressBar.Maximum);
        ScanProgressText.Text = $"{progress.Processed} / {progress.Total}";
        SetStatus($"Clearing review queue: {progress.Processed} of {progress.Total} processed, {progress.Restored} cleared.");
    }

    private void EndScanProgress()
    {
        _scanProgressActive = false;
        ScanFileText.Text = string.Empty;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressPanel.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
    }

    private sealed record CandidatePreviewItem(
        ReviewCandidate Candidate,
        string MatchLabel,
        string Resolution,
        string Details,
        string ArchivePath);

    protected override void OnClosed(EventArgs e)
    {
        _previewService.Dispose();
        base.OnClosed(e);
    }

    // --- Animated emoji -------------------------------------------------------------------
    //
    // The preview animates the atlas itself rather than the exported GIF: cropping a cell per
    // frame needs nothing but WPF, shows the animation before any file exists, and updates the
    // moment a correction is typed.

    private AtlasAnimationCatalog? _animationCatalog;
    private DispatcherTimer? _animationTimer;
    private BitmapSource? _animationSource;
    private IReadOnlyList<int> _animationOrder = [];
    private AtlasLayout? _animationLayout;
    private int _animationFrame;
    private AtlasInspection? _animationInspection;
    private readonly List<System.Windows.Controls.Border> _animationCells = [];
    private readonly List<System.Windows.Media.Brush?> _animationRestingBrushes = [];
    private int _animationInspectionRequest;
    private int _animationHighlighted = -1;

    private AtlasAnimationCatalog AnimationCatalog =>
        _animationCatalog ??= new AtlasAnimationCatalog(_runtime.StateStore);

    private async Task RefreshAnimationsAsync()
    {
        try
        {
            var sheets = await AnimationCatalog.ListAsync();
            var selectedPath = (SheetList.SelectedItem as ArchivedSheet)?.AtlasPath;
            var waiting = sheets.Count(sheet => sheet.NeedsDecision);
            var skipped = sheets.Count(sheet => sheet.IsSkipped);

            // Anything the app could work out for itself has already been exported, and anything a
            // person has skipped is settled too. What is left is the queue: sheets whose pixels did
            // not agree with their name. The two tickboxes bring the settled ones back into view.
            var showExported = ShowExportedCheck.IsChecked == true;
            var showSkipped = ShowSkippedCheck.IsChecked == true;
            var listed = sheets
                .Where(sheet => sheet.NeedsDecision
                    || (showExported && sheet.HasAnimation)
                    || (showSkipped && sheet.IsSkipped))
                .ToArray();
            SheetList.ItemsSource = listed;
            ExportMissingButton.IsEnabled = waiting > 0;
            ExportMissingButton.Content = waiting > 0 ? $"Export {waiting} missing" : "All exported";
            ClearAnimationQueueButton.IsEnabled = waiting > 0;
            AnimationsSubtitle.Text = sheets.Count == 0
                ? "No animated emoji in the archive yet. They appear here once a scan has indexed them."
                : waiting == 0
                    ? $"Nothing waiting. {sheets.Count - skipped} of {sheets.Count} exported"
                        + (skipped == 0 ? "." : $", {skipped} skipped.")
                    : $"{waiting} of {sheets.Count} animated emoji have not been exported yet. Export them, "
                        + "or correct the frames, rate or loop below first if a name looks wrong - "
                        + "and skip the ones you do not want.";

            // Restored from what is actually on the list. Looking it up in the full set instead
            // would hand the box a sheet the filter has hidden, which it answers by selecting
            // nothing at all - the same outcome, arrived at by accident rather than on purpose.
            if (selectedPath is not null)
            {
                SheetList.SelectedItem = listed.FirstOrDefault(
                    sheet => string.Equals(sheet.AtlasPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnimationsSubtitle.Text = $"The archive could not be read: {exception.Message}";
        }
    }

    private async void AnimationFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        await RefreshAnimationsAsync();
    }

    private async void SheetSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        StopAnimationPreview();
        if (SheetList.SelectedItem is not ArchivedSheet sheet)
        {
            SheetTitle.Text = "Select an emoji to preview";
            SheetStatusText.Text = string.Empty;
            ExportSheetButton.IsEnabled = false;
            SkipSheetButton.IsEnabled = false;
            SkipSheetButton.Content = "Skip";
            SheetAtlasImage.Source = null;
            SheetPreview.Source = null;
            ClearCellOverlay();
            return;
        }

        SkipSheetButton.IsEnabled = true;
        SkipSheetButton.Content = sheet.IsSkipped ? "Unskip" : "Skip";
        SheetTitle.Text = sheet.FileName;

        // Everything measured about the previous sheet goes now, before anything new is drawn. Two
        // sheets with the same frame count share a grid size, so a reading left over from the last
        // one would pass every check and outline this one's cells with the other one's art.
        ClearCellOverlay();
        _animationLayout = null;
        SheetPreview.Source = null;

        // The sheet is loaded before the three boxes are filled in. Setting SheetLoop raises
        // SelectionChanged synchronously, which starts a preview - and if that ran first it would
        // run against the previous sheet's bitmap with this sheet's frame count.
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(sheet.AtlasPath);
            bitmap.EndInit();
            bitmap.Freeze();
            _animationSource = bitmap;
            SheetAtlasImage.Source = bitmap;
            SheetAtlasHost.Width = bitmap.PixelWidth;
            SheetAtlasHost.Height = bitmap.PixelHeight;
        }

        // FormatException is in the list because a truncated or corrupt image throws
        // FileFormatException, which is one of those rather than an IOException. Escaping an async
        // void handler reaches the dispatcher, so a half-written PNG in the archive would otherwise
        // take the window down with it.
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or UriFormatException
                or FormatException
                or OverflowException)
        {
            StopAnimationPreview();
            _animationSource = null;
            SheetAtlasImage.Source = null;
            ClearCellOverlay();
            SheetStatusText.Text = $"This sheet could not be opened: {exception.Message}";
            return;
        }

        SheetFrames.Text = sheet.Name.FrameCount.ToString(CultureInfo.InvariantCulture);
        SheetRate.Text = sheet.Name.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        SheetLoop.SelectedIndex = sheet.Name.LoopStyle == AtlasLoopStyle.PingPong ? 1 : 0;
        ExportSheetButton.IsEnabled = true;

        StartAnimationPreview();
        await InspectSelectedSheetAsync(sheet, ++_animationInspectionRequest);
    }

    /// <summary>
    /// Measures which cells of the selected sheet actually carry art, and says so when that count
    /// differs from the one in the name.
    /// </summary>
    /// <remarks>
    /// Only ever a remark. The name decides what gets animated; this exists so a disagreement is
    /// visible rather than silent, and so the outlines drawn over the sheet are the same reading
    /// the export acted on.
    /// </remarks>
    private async Task InspectSelectedSheetAsync(ArchivedSheet sheet, int request)
    {
        var path = sheet.AtlasPath;
        var name = sheet.Name;
        AtlasInspection? inspection;
        try
        {
            inspection = await Task.Run(() => AtlasInspector.Inspect(path, name));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SixLabors.ImageSharp.ImageFormatException
                or InvalidOperationException
                or NotSupportedException
                or OutOfMemoryException)
        {
            // The preview above is already drawn from the same file, so a failure here costs the
            // outlines and nothing else. Not worth interrupting a person over.
            return;
        }

        // Only the newest request may speak. Selecting a sheet, moving away and coming back leaves
        // two measurements in flight, and both would find that sheet selected when they landed -
        // so keying on the selection alone let the same remark be appended twice.
        if (request != _animationInspectionRequest || !ReferenceEquals(SheetList.SelectedItem, sheet))
        {
            return;
        }

        _animationInspection = inspection;
        BuildCellOverlay();
        if (inspection is { DisagreesWithTheName: true })
        {
            var beyond = inspection.FrameCountThatWouldFit > inspection.FrameCount;
            SheetStatusText.Text +=
                $" Heads up: art sits in {inspection.CellsWithContent} cells but the name counts "
                + $"{inspection.FrameCount} frames. The name wins - "
                + (beyond
                    ? $"set Frames to {inspection.FrameCountThatWouldFit} if you want the rest included."
                    : "the cells it does not reach are simply left out.");
        }
    }

    private void SheetOverrideChanged(object sender, RoutedEventArgs e) => StartAnimationPreview();

    private void SheetOverrideSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        StartAnimationPreview();

    /// <summary>The values in the three boxes, which start as the ones VRChat put in the name.</summary>
    private EmojiAtlasName? CurrentAnimationName()
    {
        if (!int.TryParse(SheetFrames.Text, out var frames)
            || !int.TryParse(SheetRate.Text, out var rate)
            || frames < EmojiAtlasName.MinimumFrameCount
            || frames > EmojiAtlasName.MaximumFrameCount
            || rate < 1
            || rate > EmojiAtlasName.MaximumFramesPerSecond)
        {
            return null;
        }

        return new EmojiAtlasName(
            frames,
            rate,
            SheetLoop.SelectedIndex == 1 ? AtlasLoopStyle.PingPong : AtlasLoopStyle.Linear);
    }

    private void StartAnimationPreview()
    {
        StopAnimationPreview();
        if (_animationSource is not { } source || SheetList.SelectedItem is not ArchivedSheet sheet)
        {
            return;
        }

        var name = CurrentAnimationName();
        if (name is null)
        {
            SheetStatusText.Text = "Frames and rate have to be whole numbers inside the supported range.";
            ExportSheetButton.IsEnabled = false;
            return;
        }

        if (!AtlasLayout.TryCreate(name.FrameCount, source.PixelWidth, source.PixelHeight, out var layout))
        {
            SheetStatusText.Text =
                $"{name.FrameCount} frames do not divide a {source.PixelWidth}x{source.PixelHeight} sheet evenly.";
            ExportSheetButton.IsEnabled = false;
            return;
        }

        _animationLayout = layout;
        _animationOrder = layout.PlaybackOrder(name.LoopStyle);
        _animationFrame = 0;
        ExportSheetButton.IsEnabled = true;
        BuildCellOverlay();

        // Asked of the exporter rather than worked out again here. Doing the arithmetic twice is
        // exactly what once had this tab and the export result disagreeing about the same file:
        // an integer 100 / delay truncates where the exporter rounds, so an 8 fps sheet was
        // announced as "exported at 7" and then exported at 8.
        var effective = AtlasGifExporter.EffectiveFramesPerSecondFor(name.FramesPerSecond);
        var rateNote = effective == name.FramesPerSecond
            ? $"{name.FramesPerSecond} fps"
            : $"{name.FramesPerSecond} fps, exported at {effective} - GIF cannot express the rest";
        SheetStatusText.Text = sheet.HasAnimation
            ? $"{layout.Columns}x{layout.Rows} grid, {name.FrameCount} frames, {rateNote}. Exported to {sheet.AnimationPath}"
            : $"{layout.Columns}x{layout.Rows} grid, {name.FrameCount} frames, {rateNote}. Not exported yet.";

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, name.FramesPerSecond)),
        };
        _animationTimer.Tick += AdvanceAnimationFrame;
        _animationTimer.Start();
        AdvanceAnimationFrame(this, EventArgs.Empty);
    }

    /// <summary>Outline colours for the grid drawn over the sheet.</summary>
    /// <remarks>
    /// Three states, because there are three things worth telling apart at a glance: the cells the
    /// animation is built from, the one playing right now, and art the name does not count. A cell
    /// that is simply empty gets no outline at all - drawing a box round nothing is noise.
    /// </remarks>
    // Fully qualified on purpose: this project sets both UseWPF and UseWindowsForms, so System.Drawing
    // and System.Windows.Media are both in scope and a bare Brush or Color does not compile.
    private static readonly System.Windows.Media.Brush FrameCellBrush =
        Freeze(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D));

    private static readonly System.Windows.Media.Brush PlayingCellBrush =
        Freeze(System.Windows.Media.Color.FromRgb(0xF5, 0xD9, 0x0A));

    private static readonly System.Windows.Media.Brush ExtraCellBrush =
        Freeze(System.Windows.Media.Color.FromRgb(0xF7, 0x6B, 0x15));

    private static System.Windows.Media.Brush Freeze(System.Windows.Media.Color colour)
    {
        var brush = new System.Windows.Media.SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private void ClearCellOverlay()
    {
        SheetCellOverlay.Children.Clear();
        _animationCells.Clear();
        _animationRestingBrushes.Clear();
        _animationInspection = null;
        _animationHighlighted = -1;
    }

    /// <summary>
    /// Draws one outlined box per cell over the sheet, in the same grid the animation is cut from.
    /// </summary>
    /// <remarks>
    /// The boxes sit in a UniformGrid the same pixel size as the sheet, inside the Viewbox that
    /// scales it. Laying them out in the sheet's own coordinates rather than the control's is what
    /// keeps an outline on its cell at every window size.
    /// </remarks>
    private void BuildCellOverlay()
    {
        SheetCellOverlay.Children.Clear();
        _animationCells.Clear();
        _animationRestingBrushes.Clear();
        _animationHighlighted = -1;
        if (_animationLayout is not { } layout)
        {
            return;
        }

        SheetCellOverlay.Rows = layout.Rows;
        SheetCellOverlay.Columns = layout.Columns;
        var thickness = Math.Max(1.0, Math.Min(layout.CellWidth, layout.CellHeight) / 40.0);
        var cells = layout.Columns * layout.Rows;
        for (var index = 0; index < cells; index++)
        {
            var isFrame = index < layout.FrameCount;
            // Only trusted when it describes this very grid. Changing the frame count can change
            // the grid under it, and an older reading would then outline the wrong squares.
            var hasArt = _animationInspection is { } inspection
                && inspection.Layout.Columns == layout.Columns
                && inspection.Layout.Rows == layout.Rows
                && index < inspection.Cells.Count
                && inspection.Cells[index].HasContent;
            var resting = isFrame ? FrameCellBrush : hasArt ? ExtraCellBrush : null;
            var border = new System.Windows.Controls.Border
            {
                BorderThickness = new Thickness(isFrame || hasArt ? thickness : 0),
                BorderBrush = resting,
            };
            _animationCells.Add(border);
            _animationRestingBrushes.Add(resting);
            SheetCellOverlay.Children.Add(border);
        }
    }

    /// <summary>Moves the highlight to the cell the preview is showing.</summary>
    private void HighlightCell(int index)
    {
        if (_animationHighlighted == index)
        {
            return;
        }

        // Put the previous cell back to the colour it was given when the grid was drawn, rather
        // than working it out again here. Deriving it a second time is how the two ends drift
        // apart, and the earlier attempt left a cell yellow whenever the derivation disagreed.
        if (_animationHighlighted >= 0 && _animationHighlighted < _animationCells.Count)
        {
            _animationCells[_animationHighlighted].BorderBrush = _animationRestingBrushes[_animationHighlighted];
        }

        if (index >= 0 && index < _animationCells.Count)
        {
            _animationCells[index].BorderBrush = PlayingCellBrush;
        }

        _animationHighlighted = index;
    }

    private void AdvanceAnimationFrame(object? sender, EventArgs e)
    {
        if (_animationSource is not { } source || _animationLayout is not { } layout || _animationOrder.Count == 0)
        {
            return;
        }

        var index = _animationOrder[_animationFrame % _animationOrder.Count];
        _animationFrame++;
        var (x, y, width, height) = layout.GetFrame(index);
        if (x + width > source.PixelWidth || y + height > source.PixelHeight)
        {
            StopAnimationPreview();
            return;
        }

        SheetPreview.Source = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        HighlightCell(index);
    }

    private void StopAnimationPreview()
    {
        if (_animationTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= AdvanceAnimationFrame;
            _animationTimer = null;
        }
    }

    private async void ExportSelectedAnimation(object sender, RoutedEventArgs e)
    {
        if (SheetList.SelectedItem is not ArchivedSheet sheet || CurrentAnimationName() is not { } name)
        {
            return;
        }

        // The file about to be replaced may not be one the app made. A ready-made GIF from an
        // incoming folder is archived at exactly this path and then adopted as the sheet's
        // animation, and the exporter writes over whatever is there without a copy in the Recycle
        // Bin. Re-exporting is a deliberate act, so it is allowed - but not silently.
        if (sheet.HasAnimation
            && MessageBox.Show(
                $"Replace the existing animation?\n\n{sheet.AnimationPath}\n\nIf that file came from "
                    + "your incoming folder rather than from this app, it will be overwritten and not "
                    + "sent to the Recycle Bin.",
                "Export GIF",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        ExportSheetButton.IsEnabled = false;
        AtlasAnimationResult result;
        ExportedAnimationFollowUp? followUp = null;
        try
        {
            result = await AnimationCatalog.ExportAsync(sheet, name);

            // Writing the GIF was only ever the first half. A scan goes on to tell the index about
            // the new file, file the sheet beside it, and notice when the archive already held that
            // picture; this button did none of it, so anything exported here stayed invisible to
            // deduplication until the next full index - which is how the same emoji ended up in the
            // archive twice.
            if (result.Exported && result.Path is { } exportedPath)
            {
                followUp = await FinishExportAsync(
                    [new ExportedAnimation(sheet.Id, sheet.Category, sheet.AtlasPath, exportedPath)]);
            }
        }
        finally
        {
            ExportSheetButton.IsEnabled = true;
        }

        await RefreshAnimationsAsync();
        await RefreshArchiveDuplicatesAsync();

        // The answer is written after the refresh, not before it. A successful export takes the
        // sheet off the list of ones still needing a decision, so the refresh clears the selection
        // and with it everything written here - which looked exactly like a button that did
        // nothing, whether the export had succeeded or failed.
        SheetStatusText.Text = result.Exported
            ? result.Note is null
                ? $"Exported to {result.Path}"
                : $"Exported to {result.Path} - {result.Note}"
            : $"Not exported: {result.Warning ?? "this file is not a sheet."}";
        SheetStatusText.Text += DescribeFollowUp(followUp);

        if (result.Exported && SheetList.SelectedItem is null)
        {
            SheetTitle.Text = sheet.FileName;
            SheetStatusText.Text += " It has left the list of emoji still needing a decision; "
                + "tick Show exported to see it again.";
        }
    }

    /// <summary>
    /// Skips the selected sheet, or puts it back if it was already skipped.
    /// </summary>
    /// <remarks>
    /// A skip only records a decision. The sheet, and any animation already made from it, are left
    /// exactly where they are - this is a way to stop being asked about a sheet, not a way to throw
    /// one away.
    /// </remarks>
    private async void SkipSelectedAnimation(object sender, RoutedEventArgs e)
    {
        if (SheetList.SelectedItem is not ArchivedSheet sheet)
        {
            return;
        }

        SkipSheetButton.IsEnabled = false;
        try
        {
            if (sheet.IsSkipped)
            {
                await AnimationCatalog.RestoreAsync([sheet]);
            }
            else
            {
                await AnimationCatalog.SkipAsync([sheet]);
            }
        }
        finally
        {
            SkipSheetButton.IsEnabled = true;
        }

        await RefreshAnimationsAsync();
        SheetStatusText.Text = sheet.IsSkipped
            ? $"{sheet.FileName} is back in the queue."
            : $"{sheet.FileName} was skipped. Tick Show skipped to find it again.";
    }

    private async void ClearAnimationQueue(object sender, RoutedEventArgs e)
    {
        var waiting = (await AnimationCatalog.ListAsync()).Where(sheet => sheet.NeedsDecision).ToArray();
        if (waiting.Length == 0)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Skip all {waiting.Length} emoji still waiting on a decision?\n\nNothing is deleted or moved - "
                + "they stop appearing in this list, and Show skipped brings them back.",
            "Clear queue",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        ClearAnimationQueueButton.IsEnabled = false;
        int cleared;
        try
        {
            cleared = await AnimationCatalog.SkipAsync(waiting);
        }
        finally
        {
            ClearAnimationQueueButton.IsEnabled = true;
        }

        await RefreshAnimationsAsync();
        SheetStatusText.Text = $"Skipped {cleared} emoji. Tick Show skipped to find them again.";
    }

    private async void ExportMissingAnimations(object sender, RoutedEventArgs e)
    {
        ExportMissingButton.IsEnabled = false;
        var exported = 0;
        var failed = 0;
        ExportedAnimationFollowUp? followUp = null;
        try
        {
            var sheets = await AnimationCatalog.ListAsync();

            // Collected as they are written and handed over in one go at the end. The follow-up
            // reads the whole archive once per category, and doing that per file would turn a
            // fifty-sheet batch into fifty full reads of the archive.
            var written = new List<ExportedAnimation>();
            foreach (var sheet in sheets.Where(item => item.NeedsDecision))
            {
                ExportMissingButton.Content = $"Exporting {exported + failed + 1}...";
                var result = await AnimationCatalog.ExportAsync(sheet, sheet.Name);
                if (result.Exported && result.Path is { } exportedPath)
                {
                    exported++;
                    written.Add(new ExportedAnimation(sheet.Id, sheet.Category, sheet.AtlasPath, exportedPath));
                }
                else
                {
                    failed++;
                }
            }

            followUp = await FinishExportAsync(written);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnimationsSubtitle.Text = $"The archive could not be read: {exception.Message}";
        }
        finally
        {
            ExportMissingButton.IsEnabled = true;
        }

        await RefreshAnimationsAsync();
        await RefreshArchiveDuplicatesAsync();

        // Written after the refresh for the same reason as the single export above.
        SheetStatusText.Text = failed == 0
            ? $"Exported {exported} animations."
            : $"Exported {exported} animations, {failed} could not be exported.";
        SheetStatusText.Text += DescribeFollowUp(followUp);
    }

    /// <summary>
    /// Puts freshly exported animations through the rest of what a scan does to one.
    /// </summary>
    /// <remarks>
    /// Never throws. The animations are already on disk and correct by the time this runs, so a
    /// failure here is something to say out loud, not a reason to report the export as failed.
    /// </remarks>
    private async Task<ExportedAnimationFollowUp> FinishExportAsync(IReadOnlyCollection<ExportedAnimation> written)
    {
        try
        {
            return await _runtime.Scanner.FinishExportedAnimationsAsync(written, CurrentCancellation);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or InvalidDataException)
        {
            return new ExportedAnimationFollowUp(
                [],
                [],
                [$"The archive could not be brought up to date: {exception.Message}. Run a scan."]);
        }
    }

    /// <summary>
    /// What the archive made of an export: where the sheet went, and whether the picture was
    /// already in there.
    /// </summary>
    private static string DescribeFollowUp(ExportedAnimationFollowUp? followUp)
    {
        if (followUp is null)
        {
            return string.Empty;
        }

        var sentences = new List<string>();
        if (followUp.Filed.Count == 1)
        {
            sentences.Add($"The sheet was filed beside it, at {followUp.Filed[0]}.");
        }
        else if (followUp.Filed.Count > 1)
        {
            sentences.Add($"{followUp.Filed.Count} sheets were filed beside their animations.");
        }

        // Said, not acted on. Both copies are already in the archive, so which one to keep is a
        // decision - and the place to make it is the archive duplicates list in Settings, where
        // the copy being kept is checked on disk before anything is removed.
        if (followUp.Duplicates.Count == 1)
        {
            var copies = string.Join(", ", followUp.Duplicates[0].ExistingCopies);
            sentences.Add(
                $"The archive already held this picture at {copies}. Nothing was removed - "
                    + "Settings lists archive duplicates when you want to decide.");
        }
        else if (followUp.Duplicates.Count > 1)
        {
            sentences.Add(
                $"{followUp.Duplicates.Count} of them are pictures the archive already held. "
                    + "Nothing was removed - Settings lists archive duplicates when you want to decide.");
        }

        sentences.AddRange(followUp.Warnings);
        return sentences.Count == 0 ? string.Empty : " " + string.Join(" ", sentences);
    }
}
