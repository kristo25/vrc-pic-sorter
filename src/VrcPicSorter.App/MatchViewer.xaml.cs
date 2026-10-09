using System.Windows;
using System.Windows.Input;
using VrcPicSorter.App.Services;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace VrcPicSorter.App;

public partial class MatchViewer : Window
{
    private readonly string _path;
    private readonly ComparisonPlayback _playback = new();
    private Point? _dragStart;
    private Point _panStart;
    private bool _closed;

    public MatchViewer(string path)
    {
        _path = path;
        InitializeComponent();
        FileTitle.Text = System.IO.Path.GetFileName(path);
        FileTitle.ToolTip = path;
        Title = $"Match preview — {FileTitle.Text}";
    }

    private async void ViewerLoaded(object sender, RoutedEventArgs e)
    {
        await _playback.ShowAsync(Preview, _path);
        if (_closed) return;
        Status.Text = Preview.Source is null ? "This image could not be loaded. It may have moved or become unavailable." : "";
    }

    private void ViewerClosed(object? sender, EventArgs e) { _closed = true; _playback.Dispose(); }
    private void ViewerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void CloseViewer(object sender, RoutedEventArgs e) => Close();
    private void PlaybackChanged(object sender, RoutedEventArgs e) => _playback.Configure(false, Pause.IsChecked == true);
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Scale is not null) Scale.ScaleX = Scale.ScaleY = e.NewValue;
    }
    private void ResetView(object sender, RoutedEventArgs e) { Zoom.Value = 1; Pan.X = Pan.Y = 0; }
    private void ZoomWheel(object sender, MouseWheelEventArgs e) { Zoom.Value = Math.Clamp(Zoom.Value + (e.Delta > 0 ? .25 : -.25), 1, 8); e.Handled = true; }
    private void PanStarted(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this); _panStart = new(Pan.X, Pan.Y);
        ((UIElement)sender).CaptureMouse();
    }
    private void PanMoved(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        Pan.X = _panStart.X + delta.X; Pan.Y = _panStart.Y + delta.Y;
    }
    private void PanEnded(object sender, MouseButtonEventArgs e) { _dragStart = null; ((UIElement)sender).ReleaseMouseCapture(); }
}
