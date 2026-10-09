using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SixLabors.ImageSharp;
using VrcPicSorter.App;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Services;

public sealed class GroupReviewWindowTests
{
    [Fact]
    public async Task GridPresetsSelectionAndMinimumLayoutWorkTogether()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                try
                {
                    var app = new VrcPicSorter.App.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    using var directory = new TestDirectory();
                    using var runtime = new AppRuntime(directory.GetPath("state"), allowStartupRegistration: false);
                    using var image = ImageFixtureFactory.CreatePattern(23);
                    var path = directory.GetPath("incoming.png");
                    await image.SaveAsPngAsync(path);
                    var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
                    var review = new ReviewItem
                    {
                        Id = Guid.NewGuid(),
                        Category = VrcImageCategory.Emoji,
                        IncomingOriginalPath = path,
                        HeldFilePath = path,
                        IncomingFingerprint = fingerprint.ExactIdentity,
                        IncomingImageFingerprint = fingerprint
                    };
                    for (var i = 0; i < 4; i++)
                    {
                        var candidate = directory.GetPath($"archived-{i}.png"); File.Copy(path, candidate);
                        review.Candidates.Add(new ReviewCandidate
                        {
                            Id = Guid.NewGuid(),
                            ArchivePath = candidate,
                            ExpectedFingerprint = fingerprint.ExactIdentity,
                            SimilarityScore = 1
                        });
                    }
                    await runtime.StateStore.UpdateAsync(s => { s.ReviewQueue.Add(review); return true; });
                    window = new MainWindow(runtime) { Width = 920, Height = 640 };
                    var queue = (ListBox)window.FindName("QueueList");
                    queue.ItemsSource = new[] { review };
                    queue.SelectedItem = review;
                    await (Task)Invoke(window, "LoadGroupReviewAsync")!;
                    var cards = (ListBox)window.FindName("CandidateList");
                    Assert.Equal(4, cards.Items.Count);
                    cards.SelectedIndex = 2;
                    object[] AllCards() => [((ContentControl)window.FindName("IncomingCard")).Content, .. cards.Items.Cast<object>()];
                    Assert.All(AllCards(), card => Assert.Equal(ReviewFileAction.Keep, Action(card)));
                    var decision = (ReviewDecisionRequest)Invoke(window, "CreateGroupRequest", false, false)!;
                    Assert.Equal(ReviewFileAction.Keep, decision.Files.Single(f => f.CandidateId is null).Action);
                    Assert.All(decision.Files.Where(f => f.CandidateId is not null), f => Assert.Equal(ReviewFileAction.Remove, f.Action));
                    var all = (ReviewDecisionRequest)Invoke(window, "CreateGroupRequest", false, true)!;
                    Assert.All(all.Files, f => Assert.Equal(ReviewFileAction.Keep, f.Action));
                    Assert.Equal("Keep all", ((Button)window.FindName("KeepAllButton")).Content);
                    var delete = (ReviewDecisionRequest)Invoke(window, "CreateGroupRequest", true, false)!;
                    Assert.Equal(ReviewFileAction.Remove, delete.Files.Single(f => f.CandidateId is null).Action);
                    Assert.All(delete.Files.Where(f => f.CandidateId is not null), f => Assert.Equal(ReviewFileAction.Unchanged, f.Action));
                    ((Slider)window.FindName("ZoomSlider")).Value = 2;
                    await window.RefreshFromExternalAsync();
                    for (var attempt = 0; attempt < 100 && (cards.Items.Count != 4 || !((Button)window.FindName("ApplyGroupButton")).IsEnabled); attempt++)
                        await Task.Delay(10);
                    Assert.Equal(2, cards.SelectedIndex);
                    Assert.Equal(4, cards.Items.Cast<object>().Count(card => Action(card) == ReviewFileAction.Keep));
                    Assert.Equal(2, ((Slider)window.FindName("ZoomSlider")).Value);
                    var preview = (System.Windows.Controls.Image)window.FindName("IncomingPreview");
                    for (var attempt = 0; attempt < 200 && preview.Source is null; attempt++) await Task.Delay(10);
                    Assert.NotNull(preview.Source);
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new System.Windows.Size(904, 600));
                    content.Arrange(new Rect(0, 0, 904, 600)); content.UpdateLayout();
                    var incomingCard = ((ContentControl)window.FindName("IncomingCard")).Content;
                    Assert.All(Descendants(content).OfType<Button>().Where(b => ReferenceEquals(b.DataContext, incomingCard)), b => Assert.False(b.IsVisible));
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("GroupSummary")).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("GroupAffectedFiles")).Visibility);
                    var selectedCard = cards.SelectedItem;
                    cards.ScrollIntoView(selectedCard); content.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    var commands = Descendants(content).OfType<Button>().Where(b => ReferenceEquals(b.DataContext, selectedCard)).ToArray();
                    Assert.Contains(commands, b => (string)b.Content == "Keep");
                    Assert.Contains(commands, b => (string)b.Content == "Delete");
                    Assert.DoesNotContain(commands, b => (string)b.Content == "Unchanged");
                    Assert.True(preview.ActualHeight >= 70, $"Preview height: {preview.ActualHeight}");
                    var incomingColumn = (FrameworkElement)window.FindName("IncomingColumn");
                    var archiveColumn = (FrameworkElement)window.FindName("ArchiveColumn");
                    Assert.True(incomingColumn.TransformToAncestor(content).Transform(new System.Windows.Point()).X
                        < archiveColumn.TransformToAncestor(content).Transform(new System.Windows.Point()).X);
                    var apply = (Button)window.FindName("ApplyGroupButton");
                    Assert.Equal("Keep incoming only", apply.Content);
                    Assert.True(apply.IsEnabled);
                    Assert.True(apply.TransformToAncestor(content).Transform(new System.Windows.Point(0, apply.ActualHeight)).Y <= 600);
                    foreach (var thumbnail in Descendants(content).OfType<System.Windows.Controls.Image>().Where(i => i.Tag is string))
                        thumbnail.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Task.Delay(200);
                    content.UpdateLayout();
                    Assert.All(AllCards(), card => Assert.Equal(ReviewFileAction.Keep, Action(card)));
                    Assert.True(apply.TransformToAncestor(content).Transform(new System.Windows.Point(0, apply.ActualHeight)).Y <= 600);
                    if (Environment.GetEnvironmentVariable("VRC_REVIEW_RENDER") is { Length: > 0 } renderPath)
                    {
                        var bitmap = new RenderTargetBitmap(904, 600, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(renderPath); encoder.Save(stream);
                    }
                    window.Height = 1220; window.Width = 1280;
                    Invoke(window, "UpdateReviewDensity");
                    content.Measure(new System.Windows.Size(1264, 1180));
                    content.Arrange(new Rect(0, 0, 1264, 1180)); content.UpdateLayout();
                    Assert.True(((FrameworkElement)preview.Parent).ActualHeight > 500);
                    foreach (var selection in new[] { 0, 2 })
                    {
                        cards.SelectedIndex = selection;
                        content.UpdateLayout();
                        foreach (var thumbnail in Descendants(cards).OfType<System.Windows.Controls.Image>().Where(i => i.Tag is string))
                            thumbnail.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        await Task.Delay(200);
                        var active = (System.Windows.Controls.Image?)typeof(MainWindow).GetField("_activeMatchPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                        Assert.NotNull(active);
                        Assert.Same(cards.SelectedItem, active.DataContext);
                        Assert.NotNull(active.Source);
                        Assert.True(active.ActualHeight >= 150);
                        Assert.Same(((System.Windows.Controls.Image)window.FindName("ComparisonPreview")).RenderTransform, active.RenderTransform);
                    }
                    if (Environment.GetEnvironmentVariable("VRC_REVIEW_RENDER") is { Length: > 0 } largeRenderPath)
                    {
                        var bitmap = new RenderTargetBitmap(1264, 1180, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.ChangeExtension(largeRenderPath, ".large.png")); encoder.Save(stream);
                    }
                    var keptCard = cards.SelectedItem;
                    Assert.Contains(Descendants(content).OfType<Button>(), b => ReferenceEquals(b.DataContext, keptCard) && (string?)b.Content == "Enlarge");
                    var viewerPath = (string)keptCard.GetType().GetProperty("Path")!.GetValue(keptCard)!;
                    var viewer = new MatchViewer(viewerPath);
                    try
                    {
                        viewer.Show();
                        var enlargedImage = (System.Windows.Controls.Image)viewer.FindName("Preview");
                        for (var attempt = 0; attempt < 300 && enlargedImage.Source is null; attempt++) await Task.Delay(10);
                        Assert.NotNull(enlargedImage.Source);
                        ((Slider)viewer.FindName("Zoom")).Value = 3;
                        Assert.Equal(3, ((ScaleTransform)viewer.FindName("Scale")).ScaleX);
                        Assert.Equal(4, cards.Items.Count);
                    }
                    finally { viewer.Close(); }
                    var decisionViewer = new MatchViewer(viewerPath);
                    ((System.Collections.IList)typeof(MainWindow).GetField("_matchViewers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Add(decisionViewer);
                    decisionViewer.Show();
                    Assert.True(window.IsEnabled);
                    var keptPath = (string)keptCard.GetType().GetProperty("Path")!.GetValue(keptCard)!;
                    var keepButton = Descendants(content).OfType<Button>().Single(b => ReferenceEquals(b.DataContext, keptCard) && (string?)b.Tag == "Keep");
                    keepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 300 && (cards.Items.Count != 3 || !apply.IsEnabled); attempt++) await Task.Delay(10);
                    Assert.Equal(3, cards.Items.Count);
                    Assert.False(decisionViewer.IsVisible);
                    Assert.True(File.Exists(keptPath)); Assert.True(File.Exists(path));
                    var saved = await runtime.StateStore.LoadAsync();
                    Assert.Equal(3, Assert.Single(saved.ReviewQueue).Candidates.Count);
                    Assert.Contains(saved.ManualKeeps, keep => keep.Path == keptPath);
                    var remainingDecision = (ReviewDecisionRequest)Invoke(window, "CreateGroupRequest", false, true)!;
                    Assert.Equal(4, remainingDecision.Files.Count);
                    Assert.All(remainingDecision.Files, f => Assert.Equal(ReviewFileAction.Keep, f.Action));
                    await runtime.StateStore.UpdateAsync(state =>
                    {
                        state.ReviewDecisions.Add(new ReviewGroupDecision
                        {
                            ReviewId = remainingDecision.ReviewId,
                            Category = VrcImageCategory.Emoji,
                            Files = remainingDecision.Files.ToList()
                        });
                        return true;
                    });
                    await (Task)Invoke(window, "RefreshAsync", null!, false, false)!;
                    Assert.False(((Button)window.FindName("ClearQueueButton")).IsEnabled);
                    queue.SelectedIndex = 0;
                    await (Task)Invoke(window, "LoadGroupReviewAsync")!;
                    var cancel = (Button)window.FindName("CancelGroupButton");
                    Assert.True(cancel.IsEnabled);
                    Assert.Equal(Visibility.Visible, cancel.Visibility);
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 300 && !apply.IsEnabled; attempt++) await Task.Delay(10);
                    Assert.Empty((await runtime.StateStore.LoadAsync()).ReviewDecisions);
                    Assert.True(File.Exists(path));
                    Assert.True(apply.IsEnabled);
                    _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                    await (Task)Invoke(window, "ShowArchiveMoveDialogAsync")!;
                    var archiveDialog = (Window)typeof(MainWindow).GetField("_archiveMoveDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    Assert.True(archiveDialog.IsVisible);
                    Assert.Equal("Move previous archives", archiveDialog.Title);
                    archiveDialog.Close();
                    Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("RetainedArchivesPanel")).Visibility);
                    window.Close(); window = null;
                    await RemovalFilterWindowTests.VerifyFilterReviewAsync();
                    completion.SetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { window?.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static ReviewFileAction Action(object card) => (ReviewFileAction)card.GetType().GetProperty("Action")!.GetValue(card)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
