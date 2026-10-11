using System.IO;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcPicSorter.Core.Imaging;
using ImageControl = System.Windows.Controls.Image;

namespace VrcPicSorter.App.Services;

/// <summary>One elapsed-time clock drives both previews, including unequal GIF frame delays.</summary>
internal sealed class ComparisonPlayback : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<ImageControl, (Frames Frames, double Start)> _loaded = [];
    private readonly Dictionary<ImageControl, int> _versions = [];
    private int _nextVersion;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private bool _independent;
    private sealed record Frames(IReadOnlyList<BitmapSource> Images, IReadOnlyList<double> Ends, double Duration);

    public ComparisonPlayback() => _timer.Tick += (_, _) => Paint();

    public void Configure(bool independent, bool paused)
    {
        if (_independent != independent)
        {
            _independent = independent;
            _clock.Restart();
            foreach (var target in _loaded.Keys.ToArray()) _loaded[target] = (_loaded[target].Frames, 0);
        }
        if (paused) _clock.Stop(); else _clock.Start();
        Paint();
    }

    public async Task ShowAsync(ImageControl target, string path)
    {
        var version = ++_nextVersion;
        _versions[target] = version;
        _loaded.Remove(target);
        target.Source = null;
        Frames? frames = null;
        try
        {
            await _decodeGate.WaitAsync();
            try
            {
                if (_versions.GetValueOrDefault(target) != version) return;
                frames = await Task.Run(() => Decode(path));
            }
            finally { _decodeGate.Release(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or InvalidDataException or OverflowException or NotSupportedException)
        { }
        if (_versions.GetValueOrDefault(target) != version || frames is null) return;
        _loaded[target] = (frames, _independent ? _clock.Elapsed.TotalMilliseconds : 0);
        Paint();
        _timer.Start();
    }

    public void Clear()
    {
        foreach (var target in _versions.Keys.ToArray())
        {
            target.Source = null;
        }
        _loaded.Clear();
        _versions.Clear();
        _timer.Stop();
    }

    public void Stop(ImageControl target)
    {
        _versions.Remove(target);
        _loaded.Remove(target);
        if (_loaded.Count == 0) _timer.Stop();
    }

    private void Paint()
    {
        foreach (var (target, entry) in _loaded)
        {
            var index = FrameIndexAt(entry.Frames.Ends, entry.Frames.Duration, _clock.Elapsed.TotalMilliseconds - entry.Start);
            if (!ReferenceEquals(target.Source, entry.Frames.Images[index]))
                target.Source = entry.Frames.Images[index];
        }
    }

    internal static int FrameIndexAt(IReadOnlyList<double> ends, double duration, double elapsed)
    {
        var position = Math.Max(0, elapsed) % duration;
        var index = 0;
        while (index < ends.Count - 1 && position >= ends[index]) index++;
        return index;
    }

    private static Frames Decode(string path)
    {
        ImageResourceLimits.EnsureEncodedSizeSafe(path);
        var decoded = RasterCodec.Decode(path);
        var scale = Math.Min(1d, Math.Min(1600d / Math.Max(decoded.Width, decoded.Height),
            Math.Sqrt(64d * 1024 * 1024 / ((double)decoded.Width * decoded.Height * 4 * decoded.Frames.Count))));
        if (scale < 1) decoded = RasterCodec.Resize(decoded, Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)));
        var images = new List<BitmapSource>();
        var ends = new List<double>();
        double duration = 0;
        foreach (var frame in decoded.Frames)
        {
            var bytes = new byte[checked(decoded.Width * decoded.Height * 4)];
            frame.RgbaPixels.CopyTo(bytes, 0);
            for (var i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, bytes, decoded.Width * 4);
            bitmap.Freeze();
            images.Add(bitmap);
            duration += Math.Max(20, frame.DelayMilliseconds);
            ends.Add(duration);
        }
        return new Frames(images, ends, duration);
    }

    public void Dispose() => Clear();
}
