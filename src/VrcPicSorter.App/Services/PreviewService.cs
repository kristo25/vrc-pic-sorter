using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.App.Services;

public sealed class PreviewService : IDisposable
{
    /// <summary>
    /// What every preview decode is allowed to cost. The header checks refuse an image that says
    /// it is too big; this refuses one whose header understated it.
    /// </summary>
    private static readonly DecoderOptions BoundedOptions = new() { MaxFrames = ImageResourceLimits.MaximumFrames };

    private readonly Dictionary<System.Windows.Controls.Image, PreviewAnimation> _animations = [];

    /// <summary>
    /// Which load each control is waiting for, so a load that finishes after the control moved on
    /// can tell. Weak keys: the candidate list recycles its controls, and a plain dictionary kept
    /// every one of them - and the visual tree hanging off it - alive for the life of the window.
    /// </summary>
    private readonly ConditionalWeakTable<System.Windows.Controls.Image, StrongBox<int>> _versions = new();

    public BitmapSource? Load(string? path, int decodeWidth = 960)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            ImageResourceLimits.EnsureEncodedSizeSafe(path);
            var info = SixLabors.ImageSharp.Image.Identify(path);
            ImageResourceLimits.EnsureSafe(info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count));
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(BoundedOptions, path);
            image.Mutate(context => context.AutoOrient());
            if (image.Width > decodeWidth)
            {
                image.Mutate(context => context.Resize(decodeWidth, 0));
            }

            return CreateBitmapSource(image.Frames.RootFrame, image.Width, image.Height);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or UnknownImageFormatException
                or InvalidImageContentException
                or InvalidDataException
                or OverflowException
                or NotSupportedException)
        {
            return null;
        }
    }

    public async Task ShowAsync(
        System.Windows.Controls.Image target,
        string? path,
        int decodeWidth = 960)
    {
        ArgumentNullException.ThrowIfNull(target);
        Stop(target);
        var version = NextVersion(target);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            target.Source = null;
            return;
        }

        // Load decodes the file at its original size, resizes it and then walks every pixel to
        // swap red and blue. On the UI thread a single 4K screenshot stopped the window repainting
        // for the whole of that, twice per queue selection. The BitmapSource it returns is frozen,
        // so it is free to cross back.
        if (!Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var still = await Task.Run(() => Load(path, decodeWidth));
            if (CurrentVersion(target) != version)
            {
                return;
            }

            target.Source = still;
            return;
        }

        try
        {
            var (frames, delays) = await Task.Run(() => LoadAnimation(path, decodeWidth));
            if (CurrentVersion(target) != version)
            {
                return;
            }

            target.Source = frames[0];
            if (frames.Count > 1)
            {
                var animation = new PreviewAnimation(target, frames, delays);
                _animations[target] = animation;
                animation.Start();
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or UnknownImageFormatException
                or InvalidImageContentException
                or InvalidDataException
                or OverflowException)
        {
            var still = await Task.Run(() => Load(path, decodeWidth));
            if (CurrentVersion(target) == version)
            {
                target.Source = still;
            }
        }
    }

    private static (List<BitmapSource> Frames, List<TimeSpan> Delays) LoadAnimation(
        string path,
        int decodeWidth)
    {
        ImageResourceLimits.EnsureEncodedSizeSafe(path);
        var info = SixLabors.ImageSharp.Image.Identify(path);
        ImageResourceLimits.EnsureSafe(info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count));
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(BoundedOptions, path);
        image.Mutate(context => context.AutoOrient());
        if (image.Width > decodeWidth)
        {
            image.Mutate(context => context.Resize(decodeWidth, 0));
        }

        var frames = new List<BitmapSource>(image.Frames.Count);
        var delays = new List<TimeSpan>(image.Frames.Count);
        foreach (var frame in image.Frames)
        {
            frames.Add(CreateBitmapSource(frame, image.Width, image.Height));
            var milliseconds = Math.Max(20, frame.Metadata.GetGifMetadata().FrameDelay * 10);
            delays.Add(TimeSpan.FromMilliseconds(milliseconds));
        }

        return (frames, delays);
    }

    private int NextVersion(System.Windows.Controls.Image target)
    {
        var box = _versions.GetValue(target, _ => new StrongBox<int>(0));
        box.Value++;
        return box.Value;
    }

    private int CurrentVersion(System.Windows.Controls.Image target) =>
        _versions.TryGetValue(target, out var box) ? box.Value : 0;

    private static BitmapSource CreateBitmapSource(ImageFrame<Rgba32> frame, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        frame.CopyPixelDataTo(rgba);
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            (rgba[offset], rgba[offset + 2]) = (rgba[offset + 2], rgba[offset]);
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            rgba,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public void Stop(System.Windows.Controls.Image target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Moving the version on matters as much as unregistering. A load started for this control
        // and still running would otherwise finish, see its own version, and start a timer on a
        // control that has already left the screen - one nothing would ever stop again, holding
        // every decoded frame with it.
        NextVersion(target);
        if (_animations.Remove(target, out var animation))
        {
            animation.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var animation in _animations.Values)
        {
            animation.Dispose();
        }

        _animations.Clear();
        _versions.Clear();
    }

    private sealed class PreviewAnimation(
        System.Windows.Controls.Image target,
        IReadOnlyList<BitmapSource> frames,
        IReadOnlyList<TimeSpan> delays) : IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer = new();
        private int _frameIndex;

        public void Start()
        {
            _timer.Interval = delays[0];
            _timer.Tick += Tick;
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Tick;
        }

        private void Tick(object? sender, EventArgs e)
        {
            _frameIndex = (_frameIndex + 1) % frames.Count;
            target.Source = frames[_frameIndex];
            _timer.Interval = delays[_frameIndex];
        }
    }
}
