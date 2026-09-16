using System.Diagnostics;
using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class PathBoundaryTests
{
    [Fact]
    public async Task EnumerationIncludesFilesInOrdinaryNestedFolders()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var image = Path.Combine(nested, "image.png");
        await File.WriteAllTextAsync(image, "test");

        var files = PathBoundary.EnumerateFilesWithoutReparsePoints(root);

        Assert.Equal([image], files);
    }

    [Fact]
    public async Task EnumerationDoesNotFollowDirectoryJunctions()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var outsideImage = Path.Combine(outside, "outside.png");
        await File.WriteAllTextAsync(outsideImage, "test");
        var linked = Path.Combine(root, "linked");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }.WithArguments("/d", "/c", "mklink", "/J", linked, outside));
        Assert.NotNull(process);
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);

        try
        {
            var files = PathBoundary.EnumerateFilesWithoutReparsePoints(root);
            Assert.Empty(files);
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    /// <summary>
    /// A volume root keeps its trailing separator, which used to make it contain nothing at all:
    /// a whole drive chosen as the output root turned the overlap guards off and made every
    /// archive destination look like an escape from the folder it was already inside.
    /// </summary>
    [Theory]
    [InlineData(@"D:\", @"D:\Emoji", true)]
    [InlineData(@"D:\", @"D:\Emoji\Animated\one.gif", true)]
    [InlineData(@"D:\", @"D:\", true)]
    [InlineData(@"D:\", @"C:\Emoji", false)]
    [InlineData(@"D:\Archive", @"D:\Archive\Emoji", true)]
    [InlineData(@"D:\Archive", @"D:\ArchiveOther", false)]
    public void ContainsAnswersForAVolumeRootAsWellAsAFolder(string root, string candidate, bool expected) =>
        Assert.Equal(expected, PathBoundary.Contains(root, candidate));

    [Fact]
    public void AWholeDriveOverlapsAFolderOnIt() =>
        Assert.True(PathBoundary.Overlaps(@"D:\", @"D:\VRChat\Emoji"));

    /// <summary>
    /// The audit's sixth finding: the ancestor walk stopped at the first folder that did not exist.
    /// </summary>
    /// <remarks>
    /// A file about to be written usually has no parent folder yet, which is what CreateDirectory
    /// is for. Stopping there meant every existing folder above it went unexamined - and that is
    /// precisely where a junction sits: one standing in for an archive's Animated folder passed
    /// this check, because the nested folder below it had never been created.
    /// </remarks>
    [Fact]
    public async Task AJunctionIsFoundAboveFoldersThatDoNotExistYet()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(root, "Animated");
        Assert.True(await TryCreateJunctionAsync(linked, outside));

        try
        {
            var destination = Path.Combine(linked, "2026-09", "nested", "emoji.gif");
            Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));

            var refusal = Assert.Throws<InvalidOperationException>(
                () => PathBoundary.EnsureNoReparsePoints(destination, "Exported animation"));
            Assert.Contains("junction", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Fact]
    public void AnOrdinaryPathUnderFoldersThatDoNotExistYetIsAllowed()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        Directory.CreateDirectory(root);

        // The ordinary case, and the one that must not become collateral damage: nothing above
        // this path is a link, and most of it has yet to be created.
        PathBoundary.EnsureNoReparsePoints(
            Path.Combine(root, "Animated", "2026-09", "emoji.gif"),
            "Exported animation");
    }

    [Fact]
    public async Task ASafeDestinationMustAlsoStayInsideItsRoot()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        PathBoundary.EnsureSafeDestination(root, Path.Combine(root, "a", "b.gif"), "Exported animation");
        Assert.Throws<InvalidOperationException>(
            () => PathBoundary.EnsureSafeDestination(root, Path.Combine(outside, "b.gif"), "Exported animation"));

        var linked = Path.Combine(root, "linked");
        Assert.True(await TryCreateJunctionAsync(linked, outside));
        try
        {
            // Inside the root by its spelling, outside it in fact. The containment check alone
            // cannot tell; the link check is what does.
            Assert.Throws<InvalidOperationException>(
                () => PathBoundary.EnsureSafeDestination(root, Path.Combine(linked, "b.gif"), "Exported animation"));
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    internal static async Task<bool> TryCreateJunctionAsync(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }.WithArguments("/d", "/c", "mklink", "/J", link, target));
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
