using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Button = System.Windows.Controls.Button;

namespace VrcPicSorter.App;

public partial class MainWindow
{
    private readonly ComparisonPlayback _comparisonPlayback = new();
    private System.Windows.Controls.Image? _activeMatchPreview;
    private GroupFileCard[] _groupFiles = [];
    private bool _groupLoading;
    private bool _groupPending;
    private bool _anyPendingGroups;
    private int _groupRemaining;
    private Point? _panStart;
    private Point _panOrigin;
    private GroupDraft? _externalGroupDraft;
    private sealed record GroupDraft(Guid ReviewId, ReviewFileChoice[] Files, string? SelectedPath, double Zoom, double PanX, double PanY);

    private GroupDraft? CaptureGroupDraft() => SelectedReview is { } review && !_groupPending && !_groupLoading
        ? new(review.Id, _groupFiles.Select(f => f.Choice()).ToArray(),
            (CandidateList.SelectedItem as GroupFileCard)?.Path, ZoomSlider.Value, IncomingPan.X, IncomingPan.Y)
        : null;

    private sealed class GroupFileCard : INotifyPropertyChanged
    {
        public required Guid? CandidateId { get; init; }
        public required string Path { get; init; }
        public required string Fingerprint { get; init; }
        public required string Label { get; init; }
        public required string Details { get; init; }
        public string MatchDetails { get; init; } = string.Empty;
        public System.Windows.Controls.Image? PreviewControl { get; set; }
        public string FileName => System.IO.Path.GetFileName(Path);
        private ReviewFileAction _action = ReviewFileAction.Keep;
        public ReviewFileAction Action { get => _action; set { _action = value; Changed(); } }
        private bool _editable;
        private double _previewHeight = 90;
        public double PreviewHeight { get => _previewHeight; set { _previewHeight = value; Changed(); } }
        public bool Editable { get => _editable; set { _editable = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public ReviewFileChoice Choice() => new(CandidateId, Path, Fingerprint, Action);
    }

    private async Task LoadGroupReviewAsync()
    {
        var draft = _externalGroupDraft;
        var request = _reviewDisplayRequests.Begin();
        ClearGroupReview();
        var review = SelectedReview;
        if (review is null) { ShowEmptyReview(); return; }
        _groupLoading = true;
        UpdateGroupAvailability();
        ReviewTitle.Text = Path.GetFileName(review.IncomingOriginalPath);
        ReviewTitle.ToolTip = review.IncomingOriginalPath;
        ReviewSubtitle.Text = $"{review.Category} | Incoming + {review.Candidates.Count} archived match(es)";
        IncomingAvailabilityText.Text = "Checking incoming file…";
        try
        {
            var state = await _runtime.StateStore.LoadAsync();
            var current = await IsIncomingCurrentAsync(review);
            if (!_reviewDisplayRequests.IsCurrent(request) || SelectedReview?.Id != review.Id) return;
            _validatedIncomingReviewId = review.Id;
            _validatedIncomingCurrent = current;
            var pending = state.ReviewDecisions.FirstOrDefault(d => d.ReviewId == review.Id);
            _groupPending = pending is not null;
            _anyPendingGroups = state.ReviewDecisions.Count > 0;
            _groupRemaining = pending?.Steps.Count(step => !step.Completed) ?? 0;
            var indexed = state.ArchiveIndex.Categories.SelectMany(c => c.Images).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            var cards = await Task.Run(() =>
            {
                var loadedCards = new List<GroupFileCard>
            {
                new() { CandidateId = null, Path = review.HeldFilePath, Fingerprint = review.IncomingFingerprint,
                    Label = "Incoming • reference", Details = FileDetails(review.HeldFilePath, review.IncomingImageFingerprint) }
            };
                foreach (var candidate in review.Candidates)
                {
                    indexed.TryGetValue(candidate.IndexedImageId, out var archived);
                    loadedCards.Add(new GroupFileCard
                    {
                        CandidateId = candidate.Id,
                        Path = candidate.ArchivePath,
                        Fingerprint = candidate.ExpectedFingerprint,
                        Label = $"Archive • {candidate.SimilarityScore:P1} similar",
                        Details = FileDetails(candidate.ArchivePath, archived?.Fingerprint, archived?.Width, archived?.Height),
                        MatchDetails = $"{candidate.MatchKind} • {string.Join("; ", candidate.MatchReasons)}"
                    });
                }
                return loadedCards;
            });
            if (!_reviewDisplayRequests.IsCurrent(request) || SelectedReview?.Id != review.Id) return;
            _groupFiles = cards.ToArray();
            UpdateReviewDensity();
            var restoreDraft = current && !_groupPending && draft?.ReviewId == review.Id
                && !review.Candidates.Any(c => c.IsStale) && draft.Files.Length == _groupFiles.Length
                && _groupFiles.All(card => draft.Files.Any(saved => saved.CandidateId == card.CandidateId
                    && saved.Path == card.Path && saved.Fingerprint == card.Fingerprint));
            if (restoreDraft)
                foreach (var card in _groupFiles) card.Action = draft!.Files.Single(saved => saved.CandidateId == card.CandidateId).Action;
            foreach (var card in _groupFiles) card.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GroupFileCard.Action)) UpdateGroupAvailability();
            };
            IncomingCard.Content = _groupFiles[0];
            CandidateList.ItemsSource = _groupFiles.Skip(1).ToArray();
            CandidateList.SelectedIndex = CandidateList.Items.Count > 0 ? 0 : -1;
            if (restoreDraft)
            {
                CandidateList.SelectedItem = _groupFiles.FirstOrDefault(card => card.Path == draft!.SelectedPath) ?? _groupFiles[0];
                ZoomSlider.Value = draft!.Zoom;
                IncomingPan.X = ComparisonPan.X = draft.PanX;
                IncomingPan.Y = ComparisonPan.Y = draft.PanY;
            }
            IncomingAvailabilityText.Text = current ? "Incoming file available and unchanged." : "Incoming file missing or changed; refresh by scanning its folder.";
            CandidateHint.Text = "Keep incoming only recycles remaining matches. Keep all preserves incoming and remaining matches.";
            _groupLoading = false;
            UpdateGroupAvailability();
            await _comparisonPlayback.ShowAsync(IncomingPreview, review.HeldFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_reviewDisplayRequests.IsCurrent(request))
            {
                _groupLoading = false;
                IncomingAvailabilityText.Text = $"Could not load review: {e.Message}";
                UpdateGroupAvailability();
            }
        }
    }

    private static string FileDetails(string path, ImageFingerprint? fingerprint, int? width = null, int? height = null)
    {
        string size;
        try { size = $"{new FileInfo(path).Length:N0} bytes"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { size = "File unavailable"; }
        var dimensions = (fingerprint?.Width ?? width) is { } w && (fingerprint?.Height ?? height) is { } h
            ? $"{w} × {h}" : "Dimensions unavailable";
        var animation = fingerprint is { FrameCount: > 1 }
            ? $"{fingerprint.FrameCount} frames / {fingerprint.FrameDelaysMilliseconds.Sum()} ms"
            : fingerprint is null ? "Animation metadata unavailable" : "Still image";
        return $"{dimensions} • {size} • {animation}";
    }

    private void ClearGroupReview()
    {
        _comparisonPlayback.Clear();
        _activeMatchPreview = null;
        _previewService.Stop(IncomingPreview);
        _groupFiles = [];
        _groupPending = false;
        _groupLoading = false;
        CandidateList.ItemsSource = null;
        IncomingCard.Content = null;
        ComparisonLabel.Text = "Select a comparison image";
        ZoomSlider.Value = 1;
        IncomingPan.X = IncomingPan.Y = ComparisonPan.X = ComparisonPan.Y = 0;
        UpdateGroupAvailability();
    }

    private void UpdateGroupAvailability()
    {
        if (ApplyGroupButton is null) return;
        var ready = SelectedReview is not null && _groupFiles.Length > 0 && !_busy && !_groupLoading;
        var editable = ready && !_groupPending && _validatedIncomingCurrent;
        foreach (var card in _groupFiles) card.Editable = editable;
        CandidateList.IsEnabled = !_busy && !_groupLoading;
        DeleteIncomingButton.IsEnabled = editable;
        RetryGroupButton.IsEnabled = ready && _groupPending;
        RetryGroupButton.Visibility = _groupPending ? Visibility.Visible : Visibility.Collapsed;
        CancelGroupButton.IsEnabled = ready && _groupPending;
        CancelGroupButton.Visibility = _groupPending ? Visibility.Visible : Visibility.Collapsed;
        RemoveReviewButton.IsEnabled = !_busy && !_groupLoading && !_groupPending && SelectedReview is not null;
        MoveUniqueButton.IsEnabled = false;
        ClearQueueButton.IsEnabled = !_busy && !_anyPendingGroups && QueueList.Items.Count > 0;
        ApplyGroupButton.IsEnabled = false;
        KeepAllButton.IsEnabled = editable;
        if (_groupPending)
        {
            GroupSummary.Text = $"Decision partly completed: {_groupRemaining} operation(s) remaining. Retry to finish the saved decision.";
            GroupAffectedFiles.Text = "Changes are locked while a saved decision is pending.";
            return;
        }
        if (_groupFiles.Length == 0)
        {
            GroupSummary.Text = "No review selected.";
            GroupAffectedFiles.Text = string.Empty;
            return;
        }
        var decision = CreateGroupRequest();
        ApplyGroupButton.ToolTip = decision.Summary;
        GroupSummary.Text = decision.Summary;
        GroupAffectedFiles.Text = string.Join("\n", _groupFiles.Where(f => f.Action != ReviewFileAction.Unchanged)
            .Select(f => $"{f.Action}: {f.Path}"));
        try { decision.Validate(); ApplyGroupButton.IsEnabled = editable; }
        catch (InvalidOperationException e) { GroupSummary.Text += $" — {e.Message}"; }
    }

    private ReviewDecisionRequest CreateGroupRequest(bool deleteIncoming = false, bool keepAll = false) => new(
        SelectedReview!.Id,
        _groupFiles.Select(f => deleteIncoming
            ? new ReviewFileChoice(f.CandidateId, f.Path, f.Fingerprint, f.CandidateId is null ? ReviewFileAction.Remove : ReviewFileAction.Unchanged)
            : new(f.CandidateId, f.Path, f.Fingerprint, keepAll || f.CandidateId is null ? ReviewFileAction.Keep : ReviewFileAction.Remove)).ToArray(), deleteIncoming);

    private ReviewDecisionRequest CreateMatchRequest(GroupFileCard card, ReviewFileAction action) => new(
        SelectedReview!.Id, _groupFiles.Select(f => new ReviewFileChoice(f.CandidateId, f.Path, f.Fingerprint,
            f.CandidateId == card.CandidateId ? action : ReviewFileAction.Unchanged)).ToArray(), MatchOnlyId: card.CandidateId);

    private async void GroupCardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CandidateList) || CandidateList.SelectedItem is not GroupFileCard card) return;
        CandidateList.ScrollIntoView(card);
        await ActivateMatchPreviewAsync(card);
    }

    private async Task ActivateMatchPreviewAsync(GroupFileCard card)
    {
        var next = card.PreviewControl;
        if (ReferenceEquals(next, _activeMatchPreview)) return;
        var previous = _activeMatchPreview;
        _activeMatchPreview = next;
        if (previous is not null)
        {
            _comparisonPlayback.Stop(previous);
            previous.RenderTransform = System.Windows.Media.Transform.Identity;
            if (previous.DataContext is GroupFileCard old && previous.IsLoaded)
                _ = _previewService.ShowAsync(previous, old.Path, decodeWidth: 640, animate: false);
        }
        if (next is null) return;
        _previewService.Stop(next);
        next.RenderTransform = ComparisonPreview.RenderTransform;
        next.RenderTransformOrigin = new Point(.5, .5);
        await _comparisonPlayback.ShowAsync(next, card.Path);
    }

    private async void GroupFileActionClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _groupPending || _groupLoading
            || sender is not Button { DataContext: GroupFileCard { Editable: true } card, Tag: string action }
            || card.CandidateId is null || !Enum.TryParse<ReviewFileAction>(action, out var choice)
            || choice is not (ReviewFileAction.Keep or ReviewFileAction.Remove)) return;
        var request = CreateMatchRequest(card, choice);
        await RunBusyAsync(choice == ReviewFileAction.Keep ? "Keeping archive match…" : "Recycling archive match…", async () =>
        {
            try
            {
                await _runtime.Scanner.ApplyReviewDecisionAsync(request, CurrentCancellation);
                await RefreshAsync(request.ReviewId);
                SetStatus(choice == ReviewFileAction.Keep ? "Match kept in the archive and removed from this review." : "Match sent to the Recycle Bin.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                or OperationCanceledException or NotSupportedException or ArgumentException)
            {
                await RefreshAsync(request.ReviewId);
                SetStatus($"Match action interrupted: {error.Message} Use Retry remaining if available.");
            }
        });
    }

    private void ReviewWindowResized(object sender, SizeChangedEventArgs e) => UpdateReviewDensity();

    private void UpdateReviewDensity()
    {
        if (CandidateList is null) return;
        var compact = (ActualHeight > 0 ? ActualHeight : Height) < 760;
        foreach (var card in _groupFiles) card.PreviewHeight = card.CandidateId is null ? 0 : compact ? 135 : 160;
    }

    private async void ApplyGroupDecision(object sender, RoutedEventArgs e) => await ExecuteGroupDecisionAsync(false, false);
    private async void KeepAllGroupDecision(object sender, RoutedEventArgs e) => await ExecuteGroupDecisionAsync(false, false, true);
    private async void DeleteGroupIncoming(object sender, RoutedEventArgs e) => await ExecuteGroupDecisionAsync(true, false);
    private async void RetryGroupDecision(object sender, RoutedEventArgs e) => await ExecuteGroupDecisionAsync(false, true);

    private async void CancelGroupDecision(object sender, RoutedEventArgs e)
    {
        if (_busy || _groupLoading || !_groupPending || SelectedReview is not { } review) return;
        await RunBusyAsync("Checking interrupted actions…", async () =>
        {
            try
            {
                await _runtime.Scanner.CancelReviewDecisionAsync(review.Id, CurrentCancellation);
                await RefreshAsync(review.Id);
                SetStatus("Remaining actions cancelled. Completed actions were preserved; review the refreshed files.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                await RefreshAsync(review.Id);
                SetStatus($"Cannot cancel safely yet: {error.Message}");
            }
        });
    }

    private async Task ExecuteGroupDecisionAsync(bool deleteIncoming, bool retry, bool keepAll = false)
    {
        if (_busy || _groupLoading || SelectedReview is not { } review || _groupFiles.Length == 0 || (_groupPending && !retry)) return;
        var request = retry ? null : CreateGroupRequest(deleteIncoming, keepAll);
        try { request?.Validate(); }
        catch (InvalidOperationException e) { SetStatus(e.Message); return; }
        await RunBusyAsync(retry ? "Retrying remaining review operations…" : request!.Summary, async () =>
        {
            try
            {
                var result = retry
                    ? await _runtime.Scanner.RetryReviewDecisionAsync(review.Id, CurrentCancellation)
                    : await _runtime.Scanner.ApplyReviewDecisionAsync(request!, CurrentCancellation);
                await RefreshAsync(result.ReviewResolved ? null : review.Id);
                SetStatus(result.ReviewResolved ? "Review completed." : $"{result.CompletedOperations} operation(s) completed; {result.RemainingOperations} remaining.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or OperationCanceledException or NotSupportedException or ArgumentException)
            {
                await RefreshAsync(review.Id);
                SetStatus($"Decision interrupted: {e.Message} Saved operations can be retried using Retry remaining.");
            }
        });
    }

    private void ComparisonPlaybackChanged(object sender, RoutedEventArgs e) =>
        _comparisonPlayback.Configure(IndependentPlaybackCheck?.IsChecked == true, PauseComparisonCheck?.IsChecked == true);

    private void ComparisonPanStarted(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupFileCard { CandidateId: not null } card })
            CandidateList.SelectedItem = card;
        _panStart = e.GetPosition(this);
        _panOrigin = new Point(IncomingPan.X, IncomingPan.Y);
        ((UIElement)sender).CaptureMouse();
    }

    private void ComparisonPanMoved(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        IncomingPan.X = ComparisonPan.X = _panOrigin.X + delta.X;
        IncomingPan.Y = ComparisonPan.Y = _panOrigin.Y + delta.Y;
    }

    private void ComparisonPanEnded(object sender, MouseButtonEventArgs e)
    {
        _panStart = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
