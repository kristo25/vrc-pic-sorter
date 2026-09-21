using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using System.Security.Cryptography;

namespace VrcPicSorter.Core.Atlas;

public sealed record AtlasExportResult(
    string Path,
    int FrameCount,
    int FrameDelayCentiseconds,
    int EffectiveFramesPerSecond,
    string? Note = null);

/// <summary>
/// Turns a VRChat emoji sheet into an animated GIF, using the frame count, rate and loop
/// direction taken from the file name.
/// </summary>
public sealed class AtlasGifExporter
{
    /// <summary>
    /// GIF stores a delay in hundredths of a second, and browsers and chat clients silently
    /// promote anything under two hundredths to ten - so a sheet asking for 60fps would play at
    /// 10fps almost everywhere. Clamping here means the exported file plays at the rate it claims.
    /// </summary>
    public const int MinimumFrameDelayCentiseconds = 2;

    public const string GifExtension = ".gif";

    private static readonly GifEncoder Encoder = new()
    {
        Quantizer = new WuQuantizer(new QuantizerOptions { Dither = KnownDitherings.FloydSteinberg }),
    };

    /// <summary>Highest rate a GIF can express without being rewritten by the viewer.</summary>
    public static int MaximumRepresentableFramesPerSecond => 100 / MinimumFrameDelayCentiseconds;

    /// <summary>
    /// The rate an exported file will really play at, which is not always the one asked for: GIF
    /// stores whole hundredths of a second, so most rates land on the nearest expressible one.
    /// </summary>
    /// <remarks>
    /// The single definition of this on purpose. It used to be worked out in two places with two
    /// different roundings, so the Animations tab and the export result disagreed about the same
    /// file - a sheet at 17fps was reported as playing at 16 in one and 17 in the other.
    /// </remarks>
    public static int EffectiveFramesPerSecondFor(int framesPerSecond) =>
        Math.Max(1, (int)Math.Round(100.0 / FrameDelayFor(framesPerSecond), MidpointRounding.AwayFromZero));

    public static int FrameDelayFor(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        var delay = (int)Math.Round(100.0 / framesPerSecond, MidpointRounding.AwayFromZero);
        return Math.Max(MinimumFrameDelayCentiseconds, delay);
    }

    /// <summary>
    /// Writes <paramref name="destinationPath"/> from the sheet at <paramref name="sourcePath"/>.
    /// The sheet is only ever read. The GIF is built beside its destination and moved into place,
    /// so an interrupted export leaves either the previous file or nothing, never a partial one.
    /// </summary>
    public async Task<AtlasExportResult> ExportAsync(
        string sourcePath,
        string destinationPath,
        EmojiAtlasName name,
        string? allowedDestinationRoot = null,
        CancellationToken cancellationToken = default,
        bool overwrite = true,
        string? expectedDestinationHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(name);
        PathBoundary.EnsureNoReparsePoints(sourcePath, "Atlas image");

        // Where the finished file will land is checked before a single pixel is read. The source
        // was always checked and the destination never was, so a junction standing in for the
        // Animated folder took every export out of the archive - to somewhere nobody would think
        // to look, over whatever happened to be sitting there.
        var temporaryPath = destinationPath + ".tmp";
        EnsureDestinationIsSafe(destinationPath, allowedDestinationRoot);
        EnsureDestinationIsSafe(temporaryPath, allowedDestinationRoot);
        await VerifyDestinationAsync(destinationPath, expectedDestinationHash, cancellationToken).ConfigureAwait(false);

        // Measured before it is opened, and its header read before it is decoded. Loading first
        // and asking about the size afterwards means the allocation this limit exists to prevent
        // has already happened by the time the limit is consulted.
        ImageResourceLimits.EnsureEncodedSizeSafe(sourcePath);

        // A file that already carries several frames is an animation, not a sheet. VRChat's own
        // exported GIF sits beside its sheet under the identical name, so this is the only thing
        // that separates them once a caller has bypassed the name check.
        await using (var probe = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var info = await Image.IdentifyAsync(probe, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                throw new InvalidDataException("The image header could not be read.");
            }

            if (info.FrameMetadataCollection.Count > 1)
            {
                throw new InvalidOperationException(
                    "This image is already animated. A sprite sheet has to be a single still image.");
            }

            ImageResourceLimits.EnsureSafe(info.Width, info.Height, 1);
        }

        var options = new DecoderOptions { MaxFrames = 1 };
        using var atlas = await Image
            .LoadAsync<Rgba32>(options, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        ImageResourceLimits.EnsureSafe(atlas.Width, atlas.Height, 1);

        if (!AtlasLayout.TryCreate(name.FrameCount, atlas.Width, atlas.Height, out var layout))
        {
            throw new InvalidOperationException(
                $"A {atlas.Width}x{atlas.Height} image cannot hold {name.FrameCount} frames on a whole-pixel grid.");
        }

        // The name decides how many frames the animation has, full stop. VRChat writes the count
        // itself and it was right on 55 of the 56 sheets this was measured against, which is better
        // than any reading of the pixels managed. Art sitting past the last named frame is noted and
        // then left out - it is not a reason to refuse a sheet a person asked for, and refusing was
        // the more common mistake by a wide margin.
        var inspection = AtlasInspector.Inspect(atlas, layout);
        string? note = null;
        if (inspection.DisagreesWithTheName)
        {
            // Two disagreements are possible here and they have opposite consequences, so they get
            // opposite sentences. Art sitting past the last named frame is dropped from the
            // animation; blank cells inside the named range are kept and played as empty frames.
            // Only the first is anything being left out, and naming a larger count is only worth
            // doing when it is a different number from the one already in use.
            string advice;
            if (inspection.FrameCountThatWouldFit > name.FrameCount)
            {
                advice = $"The name decided; {inspection.FrameCountThatWouldFit} frames would take in everything drawn.";
            }
            else
            {
                var blank = name.FrameCount - inspection.CellsWithContent;
                advice = blank == 1
                    ? "The name decided, and the one cell with nothing drawn on it plays as a blank frame."
                    : $"The name decided, and the {blank} cells with nothing drawn on them play as blank frames.";
            }

            note = $"the sheet carries art in {inspection.CellsWithContent} of "
                + $"{layout.Columns * layout.Rows} cells, and its name counts {name.FrameCount} frames. "
                + advice;
        }

        var order = layout.PlaybackOrder(name.LoopStyle);
        var exportedBytes = checked((long)order.Count * layout.CellWidth * layout.CellHeight * 4);
        if (exportedBytes > ImageResourceLimits.MaximumDecodedBytes)
        {
            throw new InvalidOperationException(
                "The animation would exceed the configured decoded-memory resource limit.");
        }

        var delay = FrameDelayFor(name.FramesPerSecond);
        Image<Rgba32>? animation = null;
        try
        {
            foreach (var index in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (x, y, width, height) = layout.GetFrame(index);
                using var cell = atlas.Clone(context => context.Crop(new Rectangle(x, y, width, height)));
                if (animation is null)
                {
                    animation = cell.Clone();
                }
                else
                {
                    animation.Frames.AddFrame(cell.Frames.RootFrame);
                }
            }

            if (animation is null)
            {
                throw new InvalidOperationException("The sheet produced no frames.");
            }

            animation.Metadata.GetGifMetadata().RepeatCount = 0;
            foreach (var frame in animation.Frames)
            {
                var metadata = frame.Metadata.GetGifMetadata();
                metadata.FrameDelay = delay;

                // Each cell is drawn whole and carries its own transparency. Without clearing
                // between frames a transparent pixel keeps whatever the previous frame left
                // there, which smears the animation instead of replacing it.
                metadata.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Checked again now the folder exists. The first check proved no link stood between
            // here and the nearest folder that did exist; this one covers anything that appeared
            // while the frames were being cut, including a folder created for us.
            EnsureDestinationIsSafe(destinationPath, allowedDestinationRoot);
            EnsureDestinationIsSafe(temporaryPath, allowedDestinationRoot);
            await animation.SaveAsync(temporaryPath, Encoder, cancellationToken).ConfigureAwait(false);
            EnsureDestinationIsSafe(destinationPath, allowedDestinationRoot);
            await VerifyDestinationAsync(destinationPath, expectedDestinationHash, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite);
            return new AtlasExportResult(
                destinationPath,
                order.Count,
                delay,
                EffectiveFramesPerSecondFor(name.FramesPerSecond),
                note);
        }
        finally
        {
            animation?.Dispose();
        }
    }

    internal static async Task<string> ReadContentHashAsync(string path, CancellationToken cancellationToken)
    {
        PathBoundary.EnsureNoReparsePoints(path, "Existing animation");
        ImageResourceLimits.EnsureEncodedSizeSafe(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task VerifyDestinationAsync(string path, string? expectedHash, CancellationToken cancellationToken)
    {
        if (expectedHash is not null && !string.Equals(expectedHash,
            await ReadContentHashAsync(path, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The existing animation changed. Refresh the list and confirm the replacement again.");
        }
    }

    /// <summary>
    /// Refuses a destination a link would redirect, and one outside the folder the caller allowed.
    /// </summary>
    /// <remarks>
    /// A caller that names no root still gets the link check. Refusing to export at all without one
    /// would break every direct use of the exporter, and the link check is the half that stops the
    /// file leaving the folder it was addressed to.
    /// </remarks>
    private static void EnsureDestinationIsSafe(string path, string? allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
        {
            PathBoundary.EnsureNoReparsePoints(path, "Exported animation");
            return;
        }

        PathBoundary.EnsureSafeDestination(allowedRoot, path, "Exported animation");
    }
}
