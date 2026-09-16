using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Core.Atlas;

/// <summary>How much of one cell of a sheet is drawn on.</summary>
/// <param name="Index">Position in the grid, left to right then top to bottom.</param>
/// <param name="Fill">Share of the cell carrying content, 0 to 1.</param>
/// <param name="IsFrame">True when the name counts this cell as part of the animation.</param>
public sealed record AtlasCell(int Index, double Fill, bool IsFrame)
{
    /// <summary>True when the cell carries enough to be art rather than a smear of glow.</summary>
    public bool HasContent { get; init; }
}

/// <summary>What a sheet's pixels say, next to what its name says.</summary>
public sealed record AtlasInspection(
    AtlasLayout Layout,
    IReadOnlyList<AtlasCell> Cells,
    int CellsWithContent,
    int LastCellWithContent)
{
    /// <summary>Frames the name promises.</summary>
    public int FrameCount => Layout.FrameCount;

    /// <summary>
    /// True when what is drawn on the sheet does not match what its name counts.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud, never worth acting on by itself. VRChat writes the frame count into
    /// the name and it was right on 55 of the 56 sheets this was measured against, which beat every
    /// reading of the pixels; so the name decides what gets animated and this only raises an
    /// eyebrow.
    /// <para>
    /// It asks where the art ends as well as how much of it there is. Counting drawn cells alone
    /// calls a 3-frame sheet with art in cells 1, 2 and 4 an agreement - three cells drawn, three
    /// frames named - while the fourth cell's art is quietly dropped and the review pane, which
    /// goes by position, outlines it as art the name does not count. The picture a person is shown
    /// and the note they are given have to say the same thing.
    /// </para>
    /// </remarks>
    public bool DisagreesWithTheName =>
        CellsWithContent != FrameCount || LastCellWithContent + 1 > FrameCount;

    /// <summary>The count that would take in everything drawn, or the name's own when they agree.</summary>
    /// <remarks>
    /// Measured from where the art ends, not from how much of it there is. Counting the drawn cells
    /// would answer 10 for a sheet whose only stray art sits in cell 16, because it counts occupied
    /// cells rather than asking which cell is last - and a frame count has to reach the far end of
    /// the sheet to take that art in, however many gaps lie before it.
    /// </remarks>
    public int FrameCountThatWouldFit => Math.Max(FrameCount, LastCellWithContent + 1);
}

/// <summary>
/// Measures which cells of a sheet actually carry art.
/// </summary>
/// <remarks>
/// One definition, used both by the exporter - to say what it left out - and by the review pane,
/// to draw the grid over the sheet. Two measurements would eventually disagree, and the picture a
/// person is shown has to be the same one the export acted on.
/// </remarks>
public static class AtlasInspector
{
    /// <summary>
    /// Share of a cell that has to carry content before the cell counts as drawn on.
    /// </summary>
    /// <remarks>
    /// Measured against real sheets rather than guessed. VRChat art is full-bleed - on the sheets
    /// checked, every frame's art touched its own cell edge - so a soft edge or a glow routinely
    /// spills a sliver across the boundary into the blank cell next door. The emptiest genuine
    /// frame measured filled 21% of its cell. Two percent sits an order of magnitude below the real
    /// frame and far above the spill.
    /// </remarks>
    public const double CellOccupancyThreshold = 0.02;

    /// <summary>Smallest limit worth applying, so a tiny cell never ends up with a limit of zero.</summary>
    public const int MinimumOccupancyPixels = 4;

    /// <summary>
    /// Alpha at or below this counts as nothing drawn. Set above the faint tail of an antialiased
    /// or glowing edge, which is what bleeds across a cell boundary in the first place.
    /// </summary>
    public const int AlphaThreshold = 32;

    /// <summary>How far a channel may drift from the sheet's own corner and still read as empty.</summary>
    public const int ColourTolerance = 22;

    /// <summary>
    /// Reads the sheet at <paramref name="atlasPath"/> and measures it against the grid its name
    /// implies, or null when that many frames cannot divide the canvas into whole cells.
    /// </summary>
    /// <remarks>
    /// Synchronous and decoding on the calling thread, deliberately: the caller knows whether it is
    /// on a thread that can afford it, and hiding a decode behind an async signature would only
    /// disguise the cost.
    /// </remarks>
    public static AtlasInspection? Inspect(string atlasPath, EmojiAtlasName name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atlasPath);
        ArgumentNullException.ThrowIfNull(name);

        // Bounded like every other decode in the app. This one is reached by clicking a row in a
        // list, so an image far larger than it claims - or simply an enormous one - would otherwise
        // be decoded whole on a thread-pool thread with nothing to stop it.
        PathBoundary.EnsureNoReparsePoints(atlasPath, "Sheet");
        ImageResourceLimits.EnsureEncodedSizeSafe(atlasPath);
        var info = Image.Identify(atlasPath);
        ImageResourceLimits.EnsureSafe(info.Width, info.Height, 1);

        // A sheet is a still, so one frame is all that is ever wanted from it. Without the bound a
        // file that is really an animation would be decoded whole before anything noticed.
        using var atlas = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, atlasPath);
        return AtlasLayout.TryCreate(name.FrameCount, atlas.Width, atlas.Height, out var layout)
            ? Inspect(atlas, layout)
            : null;
    }

    public static AtlasInspection Inspect(Image<Rgba32> atlas, AtlasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(layout);

        var cells = layout.Columns * layout.Rows;
        var cellPixels = (long)layout.CellWidth * layout.CellHeight;
        var limit = Math.Max(MinimumOccupancyPixels, (long)(cellPixels * CellOccupancyThreshold));

        // A sheet with no transparency gives the alpha channel nothing to say - every pixel is
        // opaque, so every cell would read as drawn on. Those are measured against a sample of the
        // background taken from the very last pixel instead, which on a sheet that does not fill
        // its grid sits in empty space. On one that does, the sample lands on art and the whole
        // sheet reads as full; that is a corner this deliberately does not guess at, and it costs
        // nothing now that the answer is a note rather than a refusal.
        var transparent = HasTransparency(atlas);
        var background = transparent ? default : atlas[atlas.Width - 1, atlas.Height - 1];
        var used = new long[cells];

        // One pass over the sheet rather than one per cell: the accessor is entered once and every
        // row is attributed to whichever cell it falls in.
        atlas.ProcessPixelRows(accessor =>
        {
            for (var line = 0; line < atlas.Height; line++)
            {
                var row = line / layout.CellHeight;
                if (row >= layout.Rows)
                {
                    break;
                }

                var span = accessor.GetRowSpan(line);
                for (var pixel = 0; pixel < atlas.Width; pixel++)
                {
                    var column = pixel / layout.CellWidth;
                    if (column >= layout.Columns)
                    {
                        break;
                    }

                    var colour = span[pixel];
                    var occupied = transparent
                        ? colour.A > AlphaThreshold
                        : Math.Max(
                            Math.Abs(colour.R - background.R),
                            Math.Max(Math.Abs(colour.G - background.G), Math.Abs(colour.B - background.B)))
                            > ColourTolerance;
                    if (occupied)
                    {
                        used[(row * layout.Columns) + column]++;
                    }
                }
            }
        });

        var report = new AtlasCell[cells];
        var withContent = 0;
        var lastWithContent = -1;
        for (var index = 0; index < cells; index++)
        {
            var hasContent = used[index] > limit;
            if (hasContent)
            {
                withContent++;
                lastWithContent = index;
            }

            report[index] = new AtlasCell(index, used[index] / (double)cellPixels, index < layout.FrameCount)
            {
                HasContent = hasContent,
            };
        }

        return new AtlasInspection(layout, report, withContent, lastWithContent);
    }

    /// <summary>True when the alpha channel carries usable information at all.</summary>
    private static bool HasTransparency(Image<Rgba32> atlas)
    {
        var transparent = false;
        atlas.ProcessPixelRows(accessor =>
        {
            for (var line = 0; line < accessor.Height && !transparent; line++)
            {
                var span = accessor.GetRowSpan(line);
                for (var pixel = 0; pixel < span.Length; pixel++)
                {
                    if (span[pixel].A <= AlphaThreshold)
                    {
                        transparent = true;
                        break;
                    }
                }
            }
        });

        return transparent;
    }
}
