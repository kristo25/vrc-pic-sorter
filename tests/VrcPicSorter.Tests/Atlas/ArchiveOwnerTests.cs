using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Tests.FileSystem;

namespace VrcPicSorter.Tests.Atlas;

public sealed class ArchiveOwnerTests
{
    [Fact]
    public void DuplicateCaseAndTrailingSeparatorMappingsHaveOneOwner()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive");
        Directory.CreateDirectory(root);
        var settings = new AppSettings
        {
            CategoryMappings = [new() { Category = VrcImageCategory.Emoji, ArchivePath = root }],
            LegacyArchiveMappings = [new() { Category = VrcImageCategory.Emoji, ArchivePath = root.ToUpperInvariant() + Path.DirectorySeparatorChar }],
        };
        Assert.Equal(root, ArchiveOwner.Resolve(settings, VrcImageCategory.Emoji, Path.Combine(root, "sheet.png")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("overlap")]
    [InlineData("unavailable")]
    public void UnprovenOwnerIsRejected(string scenario)
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("archive");
        if (scenario != "unavailable") Directory.CreateDirectory(root);
        var settings = new AppSettings
        {
            CategoryMappings = [new() { Category = VrcImageCategory.Emoji, ArchivePath = root }],
        };
        if (scenario == "overlap") settings.LegacyArchiveMappings.Add(new() { Category = VrcImageCategory.Emoji, ArchivePath = directory.GetPath("") });
        var path = scenario == "unknown" ? directory.GetPath("other", "sheet.png") : Path.Combine(root, "sheet.png");
        Assert.ThrowsAny<IOException>(() => ArchiveOwner.Resolve(settings, VrcImageCategory.Emoji, path));
    }
}
