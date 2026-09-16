using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Atlas;

public sealed class AtlasGifExporterTests
{
    [Theory]
    [InlineData(10, 10, 10)]
    [InlineData(25, 4, 25)]
    [InlineData(31, 3, 33)]
    // GIF cannot express these, and a viewer that sees a delay under two hundredths silently
    // plays it at ten frames a second. Clamping keeps the file honest about its own rate.
    [InlineData(60, 2, 50)]
    [InlineData(64, 2, 50)]
    [InlineData(120, 2, 50)]
    public void TheFrameDelayIsWhatAViewerWillActuallyHonour(int fps, int delay, int effective)
    {
        Assert.Equal(delay, AtlasGifExporter.FrameDelayFor(fps));
        Assert.Equal(effective, 100 / AtlasGifExporter.FrameDelayFor(fps));
    }

    [Fact]
    public async Task ExportsOneFramePerCellAtTheRateTheNameAsksFor()
    {
        using var directory = new TestDirectory();
        var source = WriteSheet(directory, "x_a_16frames_10fps_linearloopStyle.png", frames: 16, canvas: 512);
        var destination = directory.GetPath("out", "emoji.gif");

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            destination,
            Parse("x_a_16frames_10fps_linearloopStyle.png"));

        Assert.Equal(16, result.FrameCount);
        Assert.Equal(10, result.FrameDelayCentiseconds);
        Assert.Equal(10, result.EffectiveFramesPerSecond);

        using var gif = await Image.LoadAsync<Rgba32>(destination);
        Assert.Equal(16, gif.Frames.Count);
        Assert.Equal(128, gif.Width);
        Assert.Equal(128, gif.Height);
        Assert.Equal(0, (int)gif.Metadata.GetGifMetadata().RepeatCount);
        foreach (var frame in gif.Frames)
        {
            Assert.Equal(10, frame.Metadata.GetGifMetadata().FrameDelay);
        }
    }

    [Fact]
    public async Task PingPongDoublesBackWithoutRepeatingEitherEnd()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_pingpongloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            directory.GetPath("out", "pingpong.gif"),
            Parse(name));

        Assert.Equal(6, result.FrameCount);
    }

    [Fact]
    public async Task TheSheetItselfIsNeverTouched()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var before = await File.ReadAllBytesAsync(source);

        await new AtlasGifExporter().ExportAsync(source, directory.GetPath("out", "a.gif"), Parse(name));

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ExportingTwiceLeavesNoTemporaryFileBehind()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var destination = directory.GetPath("out", "a.gif");
        var exporter = new AtlasGifExporter();

        await exporter.ExportAsync(source, destination, Parse(name));
        await exporter.ExportAsync(source, destination, Parse(name));

        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public async Task RefusesASheetTheFramesCannotSitOnEvenly()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_20frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 20, canvas: 1020);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AtlasGifExporter().ExportAsync(
                source,
                directory.GetPath("out", "a.gif"),
                Parse(name)));
    }

    [Fact]
    public async Task AnAlreadyAnimatedFileIsRefused()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("sheets", "x_a_4frames_10fps_linearloopStyle.gif");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var animated = new Image<Rgba32>(64, 64))
        {
            animated.Frames.AddFrame(animated.Frames.RootFrame);
            animated.SaveAsGif(path);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AtlasGifExporter().ExportAsync(
                path,
                directory.GetPath("out", "a.gif"),
                new EmojiAtlasName(4, 10, AtlasLoopStyle.Linear)));

        Assert.Contains("already animated", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheNameDecidesHowManyFramesTheAnimationHas()
    {
        using var directory = new TestDirectory();
        // Three frames claimed, but all four cells of the 2x2 grid carry art. The name wins: the
        // animation is three frames long and the fourth cell is simply not in it. VRChat writes
        // that count itself and it is right far more often than any reading of the pixels, so
        // refusing the sheet was the more common mistake by a wide margin.
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                        var cell = ((y / 64) * 2) + (x / 64);
                        span[x] = inset ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255) : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task ASheetThatUsesFewerCellsThanItsGridStillExports()
    {
        using var directory = new TestDirectory();
        // Three frames on a 2x2 grid: the fourth cell is empty, which is exactly what a sheet that
        // does not fill its grid looks like and must not be mistaken for a mismatch.
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 3, canvas: 128);

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
    }

    [Fact]
    public async Task AnOpaqueSheetIsJudgedAgainstItsBackgroundRatherThanItsAlpha()
    {
        using var directory = new TestDirectory();
        // Three frames on a 2x2 grid with no transparency anywhere. Judged on alpha alone the
        // empty fourth cell would look occupied and the sheet could never leave review.
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            var background = new Rgba32(18, 18, 24, 255);
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var cell = ((y / 64) * 2) + (x / 64);
                        span[x] = cell < 3
                            ? new Rgba32((byte)(60 + (cell * 60)), 120, 200, 255)
                            : background;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
    }


    [Fact]
    public async Task AFaintEdgeSpillingPastTheLastFrameIsNotMistakenForOne()
    {
        // Three frames on a 2x2 grid, with a one-pixel band of the fourth cell drawn on: what a
        // soft edge or a glow does when it crosses a cell boundary. Real VRChat art is full-bleed,
        // so this is ordinary, and it used to park a sheet in review permanently.
        using var directory = new TestDirectory();
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var cell = ((y / 64) * 2) + (x / 64);
                        var spill = cell == 3 && y == 64 && x < 104;
                        span[x] = cell < 3 || spill
                            ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255)
                            : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
    }

    [Fact]
    public async Task ArtLeftOutOfTheAnimationIsStillReported()
    {
        // Left out, but not passed over in silence: the export says what it did not include and
        // which count would have taken it in, so a person can judge without being stopped.
        using var directory = new TestDirectory();
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                        var cell = ((y / 64) * 2) + (x / 64);
                        span[x] = inset ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255) : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
        Assert.NotNull(result.Note);
        Assert.Contains("art in 4 of 4 cells", result.Note, StringComparison.Ordinal);
        Assert.Contains("4 frames would take in everything drawn", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtInACellTheNameDoesNotReachIsReportedEvenWhenTheCountsAgree()
    {
        // Three cells drawn and three frames named, so counting alone calls this an agreement - but
        // one of the three sits past the last frame and is dropped. Position has to be part of the
        // question, or the note stays silent about art the review pane is busy outlining.
        using var directory = new TestDirectory();
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                        var cell = ((y / 64) * 2) + (x / 64);
                        // Cells 0, 1 and 3 are drawn on; cell 2 is a blank frame, which is ordinary.
                        span[x] = inset && cell != 2
                            ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255)
                            : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(3, result.FrameCount);
        Assert.NotNull(result.Note);
        Assert.Contains("4 frames would take in everything drawn", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankCellsInsideTheNamedRangeAreReportedAsBlankFrames()
    {
        // The case the wording used to get backwards. Four frames named, art in two of them, and
        // the art ends well inside the named range - so nothing is left out at all. Both blank
        // cells are exported and play as empty frames, and the note has to say that rather than
        // claiming cells were dropped.
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                        var cell = ((y / 64) * 2) + (x / 64);
                        // Only cells 0 and 1 are drawn on; 2 and 3 are blank.
                        span[x] = inset && cell < 2
                            ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255)
                            : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(4, result.FrameCount);
        Assert.NotNull(result.Note);
        Assert.Contains("art in 2 of 4 cells", result.Note, StringComparison.Ordinal);
        Assert.Contains("2 cells with nothing drawn on them play as blank frames", result.Note, StringComparison.Ordinal);

        // The other branch's sentence, and the one this used to wrongly borrow.
        Assert.DoesNotContain("left out", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would take in everything drawn", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASingleBlankCellIsReportedInTheSingular()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var image = new Image<Rgba32>(128, 128))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = 0; x < span.Length; x++)
                    {
                        var inset = (x % 64) is > 8 and < 56 && (y % 64) is > 8 and < 56;
                        var cell = ((y / 64) * 2) + (x / 64);
                        // Cells 0, 1 and 2 drawn; only cell 3 is blank.
                        span[x] = inset && cell < 3
                            ? new Rgba32((byte)(40 + (cell * 50)), 120, 200, 255)
                            : default;
                    }
                }
            });
            image.SaveAsPng(path);
        }

        var result = await new AtlasGifExporter().ExportAsync(
            path,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.NotNull(result.Note);
        Assert.Contains("the one cell with nothing drawn on it plays as a blank frame", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASheetWithNothingPastItsLastFrameSaysNothing()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_3frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 3, canvas: 128);

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Null(result.Note);
    }

    [Theory]
    // A rate GIF cannot divide evenly lands on the nearest hundredth, and both the tab and the
    // export result have to name the same one. These used to disagree by a frame a second.
    [InlineData(17, 17)]
    [InlineData(8, 8)]
    [InlineData(30, 33)]
    [InlineData(60, 50)]
    public void OneAnswerForTheRateAFileWillReallyPlayAt(int requested, int effective)
    {
        Assert.Equal(effective, AtlasGifExporter.EffectiveFramesPerSecondFor(requested));
    }

    [Fact]
    public async Task TheExportReportsTheSameRateTheCatalogWould()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_17fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            directory.GetPath("out", "a.gif"),
            Parse(name));

        Assert.Equal(AtlasGifExporter.EffectiveFramesPerSecondFor(17), result.EffectiveFramesPerSecond);
    }

    /// <summary>
    /// The audit's sixth finding, at the exporter: the source was checked and the destination was
    /// not, so a junction in place of the Animated folder took the export out of the archive.
    /// </summary>
    [Fact]
    public async Task AnExportIsRefusedWhenAJunctionWouldRedirectIt()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var archiveRoot = directory.GetPath("archive");
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(archiveRoot, "Animated");
        Assert.True(await global::VrcPicSorter.Tests.FileSystem.PathBoundaryTests.TryCreateJunctionAsync(linked, outside));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new AtlasGifExporter().ExportAsync(
                    source,
                    Path.Combine(linked, "2026-09", "emoji.gif"),
                    Parse(name),
                    archiveRoot));

            // Nothing was written, and nothing outside the archive was touched.
            Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Fact]
    public async Task AnExportIsRefusedWhenItWouldLandOutsideTheArchive()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var archiveRoot = directory.GetPath("archive");
        Directory.CreateDirectory(archiveRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AtlasGifExporter().ExportAsync(
                source,
                directory.GetPath("elsewhere", "emoji.gif"),
                Parse(name),
                archiveRoot));

        Assert.False(Directory.Exists(directory.GetPath("elsewhere")));
    }

    [Fact]
    public async Task AnOrdinaryNewFolderInsideTheArchiveIsStillFine()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var archiveRoot = directory.GetPath("archive");
        Directory.CreateDirectory(archiveRoot);
        var destination = Path.Combine(archiveRoot, "Animated", "2026-09", "emoji.gif");

        var result = await new AtlasGifExporter().ExportAsync(source, destination, Parse(name), archiveRoot);

        Assert.Equal(destination, result.Path);
        Assert.True(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(archiveRoot, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The audit's extra concern: the sheet was loaded whole and asked about its size afterwards.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. A sheet wide enough to be refused is cheap to build at one pixel tall,
    /// and it proves the limit is applied to the sheet path; that it is applied to the header
    /// rather than to a decoded image is what the ordering in ExportAsync now says, and what
    /// <see cref="TheEncodedFileIsMeasuredBeforeADecoderSeesIt"/> covers from the other end. No
    /// test here deliberately exhausts memory to make its point.
    /// </remarks>
    [Fact]
    public async Task ASheetPastTheResourceLimitIsRefused()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("sheets", "x_a_4frames_10fps_linearloopStyle.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var wide = new Image<Rgba32>(ImageResourceLimits.MaximumWidth + 8, 1))
        {
            await wide.SaveAsPngAsync(path);
        }

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => new AtlasGifExporter().ExportAsync(
                path,
                directory.GetPath("out", "a.gif"),
                new EmojiAtlasName(4, 10, AtlasLoopStyle.Linear)));

        Assert.Contains("resource limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(directory.GetPath("out")));
    }

    /// <summary>
    /// The gate that runs before anything is opened, let alone decoded.
    /// </summary>
    /// <remarks>
    /// It measures the file on the path rather than anything that has been read, which is the whole
    /// point: it is the only check available before a decoder has been handed the bytes. A file
    /// that is not there is not its business - the open reports that in its own words.
    /// </remarks>
    [Fact]
    public async Task TheEncodedFileIsMeasuredBeforeADecoderSeesIt()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("sheets", "ordinary.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var small = new Image<Rgba32>(8, 8))
        {
            await small.SaveAsPngAsync(path);
        }

        ImageResourceLimits.EnsureEncodedSizeSafe(path);
        ImageResourceLimits.EnsureEncodedSizeSafe(directory.GetPath("sheets", "missing.png"));
        Assert.True(new FileInfo(path).Length < ImageResourceLimits.MaximumEncodedBytes);
    }

    private static EmojiAtlasName Parse(string name)
    {
        Assert.True(EmojiAtlasName.TryParse(name, out var parsed));
        return parsed;
    }

    /// <summary>A square sheet whose cells each hold one distinct solid colour.</summary>
    private static string WriteSheet(TestDirectory directory, string name, int frames, int canvas)
    {
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var side = AtlasLayout.SideFor(frames);
        var cell = canvas / side;
        using var image = new Image<Rgba32>(canvas, canvas);
        for (var index = 0; index < frames; index++)
        {
            var row = index / side;
            var column = index % side;
            var colour = new Rgba32((byte)(20 + (index * 11 % 220)), (byte)(40 + (index * 37 % 200)), 90, 255);
            image.ProcessPixelRows(accessor =>
            {
                for (var y = row * cell; y < (row + 1) * cell; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = column * cell; x < (column + 1) * cell; x++)
                    {
                        span[x] = colour;
                    }
                }
            });
        }

        image.SaveAsPng(path);
        return path;
    }
}
