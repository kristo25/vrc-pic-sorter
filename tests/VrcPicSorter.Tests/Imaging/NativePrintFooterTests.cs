using System.IO.Compression;
using System.Reflection;
using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class NativePrintFooterTests
{
    // Captured native-image regression fixture, extracted from the 2048x1440 source
    // Intus_2026-09-20_03-42-40.000_prnt_eab58cb2-fbe0-43dd-a05c-3c6ccb4d3fd0.png.
    // SHA256: 7B0997FF0275848A0C8783912B2B2F04CA4723705459FF454EF0534BF68F59BA
    // Visually verified footer reads "Pris par Intus" and "09/14/2026".
    // Source rows 1390..1427 were thresholded at luminance 180; no production templates
    // or detector internals were consulted to construct this literal mask.
    private const string NativeFooterMask = "eNrt2bFu2zAQBuBjCYQZAnH1oEqvoCJDOQj1q+QRVHRohiBiYKAZu3bowzDIWqAv0IFblw7aqgKq2f9kO6kjCUiRpXTE4QAbMuCPIu+OEtE8xkcjoydcPsXQqej9oXmCodPRr+AQ6kdfmxPZvdWyzsnnUfvbEAJ5SW8gq4gMVTanViA4PZyrw/SLVtsA2TlRSVVT01oitCN+MfT303QUt7/LXRBWXLC/aAMFhTDc25V/mCrh7yckcn9a+ivpxCVhGxjjSSUI2eDayqoJv4rbz1zlxHui12SOMRfJsRPD1F4NnGt9GP4KNO/eEZ3SgtyVTMkJNeb/oKgTdMIpcpH19c/Kk2W/Lny8k6DgP/L2LdlTSrf+Kznm/34tg6IvNVLEAom/VfB/Cz5UlLR5tPef/YmHz71i/41YTPl/Xsuvin4gRepP8HouCN3Gf5HF6z/D/6/YX1DCfgO/GPMv7QrzcuHFi6TdXGAlPin8Pol3/yu/9ft7/40Yy/957y+doPR8c/6xEp8OwV/AX7HfO/i9+zXIZ5WTuvenTm78ff47BH9DGq0vFQVKWcP+xv0e9j+N2PpR9FKzq/+p7f1ZzH5ve79hf8X+yulspP9XVuoJfxmz34VkAX9akCTDfuNQBB/6a/Zf9v4uZ39Y3vtbEbHf1klKnAT4zrIfW9wM8z/70f/AHwb+RkXsp+zen7C/D6N+5PzUrlT60G91xPUfR7gTru/GCj4JmE0Y9aPZTe2tHPgp4v6HZQl8IdwKi5PggsOEn3q/YL/d85eH4Xd4EoAUMO1PkBx2/nrrP8O3JuLzHxhH7DOOPJM4TPtLdIfZnt+Tjtl/DoZiX8GnIJA4lJPrv/Q3It/325j9uJd83t/4De8FDumof7XCI4DGre78Cn7tb3USb/4rM2pwY9l/Rl2QHzWC1uP+IKmrbXi59fsc/jX6p3W89a/NXb31V+xfqz6Mr388BP68pHrxt79D/9TF2/80yuUo/Hf+TiC00+/FdjvDPv7dyX/9/uNfR0rPe8z+2T/7Z//sn8czGH8A+4rA4g==";

    [Fact]
    public void RealNativeDateWithDigitNineIsRecognized()
    {
        var image = CreateNativeFooterFixture();
        Assert.True(RecognizesNativeDate(image));
    }

    [Fact]
    public void RemovingTheNativeDateIsNotRecognized()
    {
        var image = CreateNativeFooterFixture();
        // Erase the actual date/calendar block; leave native caption, person and name.
        var pixels = image.Frames[0].RgbaPixels;
        for (var y = 1390; y < 1428; y++)
            for (var x = 1760; x < 1988; x++)
                pixels.AsSpan((y * 2048 + x) * 4, 4).Fill(255);
        Assert.False(RecognizesNativeDate(image));
    }

    // The source caption is French, outside the English caption recognizer's contract.
    // Exercise its date parser directly; this proves native glyphs, not whole-frame removal.
    private static bool RecognizesNativeDate(DecodedImage image)
    {
        var pixelType = typeof(PrintFrameDetector).GetNestedType("FramePixels", BindingFlags.NonPublic)!;
        var pixels = Activator.CreateInstance(pixelType, image)!;
        return (bool)typeof(PrintFrameDetector).GetMethod("HasRecognizedDate", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [pixels, 1798])!;
    }

    private static DecodedImage CreateNativeFooterFixture()
    {
        using var encoded = new MemoryStream(Convert.FromBase64String(NativeFooterMask));
        using var zlib = new ZLibStream(encoded, CompressionMode.Decompress);
        var mask = new byte[2048 * 38 / 8];
        zlib.ReadExactly(mask);
        var rgba = new byte[2048 * 1440 * 4];
        Array.Fill(rgba, (byte)255);
        for (var y = 0; y < 38; y++)
            for (var x = 0; x < 2048; x++)
            {
                var bit = y * 2048 + x;
                if ((mask[bit / 8] & (1 << (bit % 8))) == 0) continue;
                var offset = ((1390 + y) * 2048 + x) * 4;
                rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = 0;
            }
        return new DecodedImage(2048, 1440, "PNG", [new DecodedImageFrame(rgba, 0)]);
    }
}
