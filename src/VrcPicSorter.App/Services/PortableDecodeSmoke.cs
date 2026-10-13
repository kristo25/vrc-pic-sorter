using System.IO;
using System.Runtime.InteropServices;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.App.Services;

internal static class PortableDecodeSmoke
{
    // Fixed independent samples, not encoded by RasterCodec: WPF PNG (red, transparent)
    // and a hand-encoded GIF89a with a red global palette entry and one red pixel.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAARSURBVBhXY/jPwPCfgYGBAQAM/AH/ppAQ0AAAAABJRU5ErkJggg==";
    private static readonly byte[] Gif = [
        71, 73, 70, 56, 57, 97, 1, 0, 1, 0, 128, 0, 0,
        255, 0, 0, 0, 0, 0,
        44, 0, 0, 0, 0, 1, 0, 1, 0, 0,
        2, 2, 68, 1, 0, 59];

    public static async Task<int> RunAsync()
    {
        try
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("Portable codec verification requires an x64 process.");
            await VerifyAsync(Convert.FromBase64String(Png), "PNG", 2, [255, 0, 0, 255, 0, 0, 0, 0]);
            await VerifyAsync(Gif, "GIF", 1, [255, 0, 0, 255]);
            return 0;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Portable codec verification failed: {exception}");
            return 1;
        }
    }

    private static async Task VerifyAsync(byte[] encoded, string format, int width, byte[] expected)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        var result = await new ImageDecoder().DecodeAsync(stream, $"portable-smoke.{format.ToLowerInvariant()}");
        if (!result.IsSuccess)
            throw new InvalidDataException(result.Failure!.Message);
        var image = result.Image!;
        if (image.Width != width || image.Height != 1 || image.FormatName != format || image.Frames.Count != 1
            || !image.Frames[0].RgbaPixels.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"Published {format} decoder returned unexpected pixels or dimensions.");
    }
}
