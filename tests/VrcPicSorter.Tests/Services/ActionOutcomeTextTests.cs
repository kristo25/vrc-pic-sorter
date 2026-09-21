using SixLabors.ImageSharp;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Services;

public sealed class ActionOutcomeTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterRemovalDoesNotClaimRollback(bool cancelled)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("incoming");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        var incoming = Path.Combine(source, "copy.png");
        var keeper = Path.Combine(archive, "keeper.png");
        using var image = ImageFixtureFactory.CreatePattern(23);
        await image.SaveAsPngAsync(incoming);
        await image.SaveAsPngAsync(keeper);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = FileRouterTests.CreateStore(directory, source, archive);
        var router = new FileRouter(store, new ImageDecoder(), new FailAfterRemoval(cancelled));
        string? message = null;
        await AsyncCommandRunner.RunAsync(
            () => router.AutoKeepArchivedAsync(incoming, VrcImageCategory.Emoji, fingerprint, keeper, fingerprint.ExactIdentity),
            error => { message = ActionOutcomeText.Interrupted(error is OperationCanceledException); return Task.CompletedTask; });
        Assert.False(File.Exists(incoming));
        Assert.True(File.Exists(keeper));
        Assert.NotEmpty((await store.LoadAsync()).OperationJournal);
        Assert.Contains("pending recovery", message);
        Assert.DoesNotContain("No image was deleted", message);
        Assert.DoesNotContain("nothing was left half-moved", message);
    }

    private sealed class FailAfterRemoval(bool cancelled) : IRecycleBinService
    {
        public bool CanRecycle(string path) => true;
        public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
        {
            File.Delete(path);
            throw cancelled ? new OperationCanceledException() : new IOException("Failure after the file effect.");
        }
    }
}
