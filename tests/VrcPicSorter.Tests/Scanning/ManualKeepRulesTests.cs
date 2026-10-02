using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Scanning;

public sealed class ManualKeepRulesTests
{
    [Fact]
    public async Task ProtectionFollowsVerifiedRelocationWhenIdenticalDestinationHasDifferentTimestamp()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("old");
        var targetRoot = directory.GetPath("new");
        Directory.CreateDirectory(sourceRoot); Directory.CreateDirectory(targetRoot);
        var source = Path.Combine(sourceRoot, "a.png");
        var target = Path.Combine(targetRoot, "a.png");
        await File.WriteAllTextAsync(source, "identical bytes");
        File.Copy(source, target);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
        var info = new FileInfo(source);
        var state = new AppStateDocument();
        state.ManualKeeps.Add(new(source, "pixels", info.Length, info.LastWriteTimeUtc));
        var result = await ArchiveRelocation.RelocateAsync([new(VrcImageCategory.Emoji, sourceRoot, targetRoot, 1, info.Length)]);
        ArchiveRelocation.RebaseIndex(state, result.Moves);
        Assert.Single(result.Moves);
        var landed = new FileInfo(result.Moves[0].To);
        Assert.True(ManualKeepRules.IsProtected(state, new IndexedImageRecord
        { Path = landed.FullName, ExactFingerprint = "pixels", FileSize = landed.Length, LastWriteUtc = landed.LastWriteTimeUtc }));
    }

    [Fact]
    public void ProtectionMatchesOnlyTheReviewedIdentity()
    {
        var image = new IndexedImageRecord { Path = @"C:\archive\copy.png", ExactFingerprint = "pixels", FileSize = 42, LastWriteUtc = DateTimeOffset.UtcNow };
        var state = new AppStateDocument();
        state.ManualKeeps.Add(new ManualKeepProtection(image.Path, image.ExactFingerprint, image.FileSize, image.LastWriteUtc));
        Assert.True(ManualKeepRules.IsProtected(state, image));
        image.Path = @"C:\archive\COPY.png";
        Assert.True(ManualKeepRules.IsProtected(state, image));
        image.Path = @"C:\archive\new-copy.png";
        Assert.False(ManualKeepRules.IsProtected(state, image));
        image.Path = @"C:\archive\copy.png";
        image.LastWriteUtc = image.LastWriteUtc.AddSeconds(1);
        Assert.False(ManualKeepRules.IsProtected(state, image));
        image.LastWriteUtc = state.ManualKeeps[0].LastWriteUtc;
        image.ExactFingerprint = "replacement";
        Assert.False(ManualKeepRules.IsProtected(state, image));
        image.ExactFingerprint = "pixels";
        image.FileSize++;
        Assert.False(ManualKeepRules.IsProtected(state, image));
    }

    [Fact]
    public void RelocationMovesOnlyRecordedProtectionPaths()
    {
        var stamp = DateTimeOffset.UtcNow;
        var state = new AppStateDocument();
        state.ManualKeeps.Add(new ManualKeepProtection(@"C:\old\a.png", "a", 10, stamp));
        state.ManualKeeps.Add(new ManualKeepProtection(@"C:\old\b.png", "b", 20, stamp));
        ArchiveRelocation.RebaseIndex(state, [new ArchiveRelocationMove(VrcImageCategory.Emoji, @"C:\old\a.png", @"C:\new\a.png")]);
        Assert.Equal(@"C:\new\a.png", state.ManualKeeps[0].Path);
        Assert.Equal(@"C:\old\b.png", state.ManualKeeps[1].Path);
        Assert.Equal(stamp, state.ManualKeeps[0].LastWriteUtc);
    }

    [Fact]
    public void SuppressionFollowsEmojiAndParametersAcrossFoldersAndCopySuffixes()
    {
        const string stem = "emoji_inv_11111111-2222-3333-4444-555555555555_4frames_20fps_linearloopStyle";
        var key = ManualKeepRules.AnimationKey($@"C:\old\{stem} (2).gif");
        Assert.NotNull(key);
        var state = new AppStateDocument();
        state.SuppressedAnimations.Add(key!);
        Assert.True(ManualKeepRules.IsAnimationSuppressed(state, $@"C:\new\{stem}.png"));
        Assert.False(ManualKeepRules.IsAnimationSuppressed(state, $@"C:\new\{stem.Replace("20fps", "30fps")}.png"));
        Assert.Null(ManualKeepRules.AnimationKey(@"C:\archive\still.png"));
        Assert.Null(ManualKeepRules.AnimationKey(@"C:\archive\unknown.gif"));
    }
}
