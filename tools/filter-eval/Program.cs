using System.Text.Json;
using VrcPicSorter.Core.Imaging;

// Explicit visual labels, split by image family. No assumption that unlabelled files are negatives.
var root = args.Single();
var cases = new (string Prefix, string Stamp, string Split, bool Frame, PrintFrameMatch Expected)[]
{
    ("￰_2026-10-03_20-23-10", ".000_", "development", true, PrintFrameMatch.Confirmed),
    ("lemonbxmble_2026-10-03_20-26-23", ".000_", "development", true, PrintFrameMatch.Confirmed),
    ("ʚ moonlight ɞ_2025-12-10_23-44-20", ".000_", "development", true, PrintFrameMatch.Confirmed),
    ("Muttons_2026-10-03_20-27-52", ".000_", "holdout", true, PrintFrameMatch.Confirmed),
    ("nepou_2026-08-15_22-50-15", ".000_", "holdout", true, PrintFrameMatch.Confirmed),
    ("Ḉeltic_2026-04-27_02-15-52", ".525_", "holdout", true, PrintFrameMatch.Confirmed),
    ("VastHypz_2026-04-27_04-08-07", ".563_", "alternate-layout", true, PrintFrameMatch.Uncertain),
    ("￰_2026-10-03_20-23-10", ".388_", "development", false, PrintFrameMatch.None),
    ("ʚ moonlight ɞ_2025-12-10_23-44-20", ".119_", "development", false, PrintFrameMatch.None),
    ("Project-V_2026-10-03_20-23-35", ".000_", "negative", false, PrintFrameMatch.None),
    ("Project-V_2026-10-03_20-23-43", ".000_", "negative", false, PrintFrameMatch.None),
    ("zexc_2024-11-16_18-47-59", ".000_", "negative", false, PrintFrameMatch.None),
};
var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);
var results = new List<object>();
var failures = 0;
DecodedImage? reference = null;
foreach (var item in cases)
{
    var path = files.Single(path => Path.GetFileName(path).StartsWith(item.Prefix + item.Stamp, StringComparison.Ordinal));
    var decoded = await new ImageDecoder().DecodeAsync(path);
    if (!decoded.IsSuccess) throw new InvalidDataException(decoded.Failure?.Message);
    reference ??= decoded.Image;
    var outcome = PrintFrameDetector.Detect(decoded.Image!);
    if (outcome != item.Expected) failures++;
    results.Add(new { File = Path.GetFileName(path), item.Split, item.Frame, Expected = item.Expected.ToString(), Actual = outcome.ToString() });
}

// Real reference pixels with one required signal removed at a time. Originals are never written.
foreach (var region in new[]
{
    (Name: "caption removed", X: 60, Y: 1385, Width: 212, Height: 45, Expected: PrintFrameMatch.None),
    (Name: "name removed", X: 310, Y: 1385, Width: 1400, Height: 45, Expected: PrintFrameMatch.Uncertain),
    (Name: "calendar removed", X: 1760, Y: 1385, Width: 36, Height: 45, Expected: PrintFrameMatch.Uncertain),
    (Name: "date removed", X: 1798, Y: 1385, Width: 190, Height: 45, Expected: PrintFrameMatch.Uncertain),
})
{
    var rgba = (byte[])reference!.Frames[0].RgbaPixels.Clone();
    for (var y = region.Y; y < region.Y + region.Height; y++)
        for (var x = region.X; x < region.X + region.Width; x++)
            Array.Fill(rgba, (byte)255, (y * 2048 + x) * 4, 4);
    var outcome = PrintFrameDetector.Detect(new DecodedImage(2048, 1440, "PNG", [new(rgba, 0)]));
    if (outcome != region.Expected) failures++;
    results.Add(new { Mutation = region.Name, Expected = region.Expected.ToString(), Actual = outcome.ToString() });
}
var enlarged = new byte[4096 * 2880 * 4];
for (var y = 0; y < 2880; y++)
    for (var x = 0; x < 4096; x++)
        Array.Copy(reference!.Frames[0].RgbaPixels, ((y / 2) * 2048 + x / 2) * 4, enlarged, (y * 4096 + x) * 4, 4);
var resizedResult = PrintFrameDetector.Detect(new DecodedImage(4096, 2880, "PNG", [new(enlarged, 0)]));
if (resizedResult != PrintFrameMatch.Uncertain) failures++;
results.Add(new { Mutation = "2x resized frame", Expected = "Uncertain", Actual = resizedResult.ToString() });
Console.WriteLine(JsonSerializer.Serialize(new { Labelled = cases.Length, Mutations = 5, Failures = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;
