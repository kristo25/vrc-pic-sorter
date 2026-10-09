using System.IO;
using System.Windows;
using System.Windows.Controls;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;

namespace VrcPicSorter.App;

public partial class MainWindow
{
    private Task _pendingSettingsSave = Task.CompletedTask;

    public void ReportFilterRecycling(CategoryScanResult result)
    {
        if (result.FilterRecycled > 0 && !_busy)
            SetStatus($"Watching {result.Category}: {result.FilterRecycled} image(s) recycled by filters, {result.HeldForReview} queued for review.");
    }

    private void PlayerNameChanged(object sender, TextChangedEventArgs e)
    {
        if (RemoveMyImagesCheck is null) return;
        RemoveMyImagesCheck.IsEnabled = !string.IsNullOrWhiteSpace(PlayerNameField.Text);
        if (!RemoveMyImagesCheck.IsEnabled) RemoveMyImagesCheck.IsChecked = false;
    }

    private async void RescanArchiveFilters(object sender, RoutedEventArgs e)
    {
        try { await RescanArchiveFiltersAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            SetStatus($"Archive filter scan could not start: {error.Message}");
        }
    }

    private async Task RescanArchiveFiltersAsync()
    {
        if (_busy) return;
        BeginAutoSaveSettings();
        await _pendingSettingsSave;
        var draft = CaptureSettingsDraftOrNull();
        var saved = await _runtime.StateStore.LoadAsync();
        if (draft is null || draft.DescribeBlockingProblem(saved.Settings) is not null
            || draft.RemovePrintFrame != saved.Settings.RemovePrintFrame
            || draft.RemoveMyImages != saved.Settings.RemoveMyImages
            || draft.PlayerName != saved.Settings.PlayerName
            || !string.Equals(draft.OutputRootPath, saved.Settings.OutputRootPath, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Save valid removal filter settings before rescanning the archive.");
            return;
        }
        if (!saved.Settings.RemovePrintFrame && !saved.Settings.RemoveMyImages
            && !saved.ReviewQueue.Any(r => r.Status != ReviewStatus.Resolved && r.FilterReview?.IsArchive == true))
        {
            SetStatus("Enable a removal filter before rescanning the archive.");
            return;
        }
        await RunBusyAsync("Scanning current archive with removal filters…", async () =>
        {
            BeginScanProgress();
            var progress = new Progress<ScanProcessingProgress>(UpdateScanProcessingProgress);
            var result = await _runtime.Scanner.RescanArchiveFiltersAsync(progress, CurrentCancellation);
            SetStatus($"Archive filter scan complete: {result.Examined} examined, {result.Recycled} recycled, {result.ReviewNeeded} queued for review, {result.Failed} failed.");
            await ReportScanErrorsAsync(result.Errors);
            await RefreshAsync();
            ShowPage(ReviewPage);
        });
    }

    private static string FilterReasonText(RemovalFilterReason reason) => reason switch
    {
        RemovalFilterReason.PrintFrame => "Remove Print Frame",
        RemovalFilterReason.PlayerName => "Remove my images",
        _ => reason.ToString()
    };

    private void SetFilterReviewLayout(ReviewItem? review)
    {
        var filter = review?.FilterReview;
        FilterReviewActions.Visibility = filter is null ? Visibility.Collapsed : Visibility.Visible;
        NormalReviewActions.Visibility = filter is null ? Visibility.Visible : Visibility.Collapsed;
        ArchiveColumn.Visibility = filter is null ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(IncomingColumn, filter is null ? 1 : 3);
        IncomingOriginLabel.Text = filter?.IsArchive == true ? "Archive" : "Incoming";
        CandidateHint.TextWrapping = filter is null ? TextWrapping.NoWrap : TextWrapping.Wrap;
    }

    private async void KeepFilterReview(object sender, RoutedEventArgs e) => await ResolveFilterAsync(true);
    private async void RemoveFilterReview(object sender, RoutedEventArgs e) => await ResolveFilterAsync(false);

    private async Task ResolveFilterAsync(bool keep)
    {
        if (_busy || _groupLoading || !_validatedIncomingCurrent || SelectedReview is not { FilterReview: not null } review) return;
        await RunBusyAsync(keep ? "Keeping image…" : "Recycling image…", async () =>
        {
            try
            {
                await _runtime.Scanner.ResolveFilterReviewAsync(review.Id, keep, CurrentCancellation);
                CloseMatchViewers();
                await RefreshAsync();
                SetStatus(keep ? "Filter exception saved. Image kept." : "Image sent to the Recycle Bin.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or NotSupportedException or ArgumentException)
            {
                await RefreshAsync(review.Id);
                SetStatus($"Filter decision interrupted: {error.Message} Review the image and retry.");
            }
        });
    }
}
