using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class NetworkRecyclingTests
{
    [Theory]
    [InlineData(@"\\server\share\image.gif")]
    [InlineData(@"\\?\UNC\server\share\image.gif")]
    public void RealUncClassificationUsesLocalRecyclePolicy(string path)
    {
        using var directory = new TestDirectory();
        var roots = new List<string>();
        var service = new WindowsRecycleBinService(root => { roots.Add(root); return false; },
            stagingRoot: directory.GetPath("stage"));
        Assert.True(service.CanRecycle(path));
        Assert.Equal(Path.GetPathRoot(directory.Path), Assert.Single(roots));
        Assert.False(new WindowsRecycleBinService(_ => true, stagingRoot: directory.GetPath("stage")).CanRecycle(path));
    }

    [Fact]
    public async Task FailedRetriesReuseOneVerifiedCopyAndThenSucceed()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        var stage = directory.GetPath("stage");
        File.WriteAllText(source, "original");
        for (var attempt = 0; attempt < 3; attempt++)
            await Assert.ThrowsAsync<IOException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
                source, stage, _ => throw new IOException("Simulated failure"), default));
        var copy = Assert.Single(Directory.GetFiles(stage, "*.gif", SearchOption.AllDirectories));
        Assert.Equal("original", File.ReadAllText(copy));
        Assert.Empty(Directory.GetFiles(stage, "*.partial", SearchOption.AllDirectories));
        await VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(source, stage,
            path => File.Move(path, directory.GetPath("simulated-bin.gif")), default);
        Assert.False(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(directory.GetPath("simulated-bin.gif")));
    }

    [Fact]
    public async Task CancellationInterruptsAnInProgressRead()
    {
        using var input = new WaitingReadStream();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var operation = VerifiedArchiveTransfer.CopyAndVerifyAsync(input, output, [], cancellation.Token);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, output.Length);
    }

    private sealed class WaitingReadStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 10;
        public override long Position { get; set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task InterruptedPartialIsRebuiltButChangedVerifiedCopyIsPreserved()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        var stage = directory.GetPath("stage");
        File.WriteAllText(source, "original");
        await Assert.ThrowsAsync<IOException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
            source, stage, _ => throw new IOException("Simulated failure"), default));
        var copy = Assert.Single(Directory.GetFiles(stage, "*.gif", SearchOption.AllDirectories));
        File.WriteAllText(copy, "changed");
        await Assert.ThrowsAsync<IOException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
            source, stage, _ => Assert.Fail("Must not recycle changed staging"), default));
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Equal("changed", File.ReadAllText(copy));
        File.Move(copy, copy + ".partial");
        await VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(source, stage,
            path => File.Move(path, directory.GetPath("simulated-bin.gif")), default);
        Assert.False(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(directory.GetPath("simulated-bin.gif")));
        Assert.False(File.Exists(copy + ".partial"));
    }

    [Theory]
    [InlineData(false, DriveType.Fixed, true)]
    [InlineData(true, DriveType.Fixed, false)]
    [InlineData(false, DriveType.Removable, false)]
    public void NetworkRecyclingRequiresLocalEnabledBin(bool disabled, DriveType localType, bool expected)
    {
        var service = new WindowsRecycleBinService(_ => disabled,
            root => root.StartsWith(@"\\") ? DriveType.Network : localType,
            stagingRoot: @"C:\staging");
        Assert.Equal(expected, service.CanRecycle(@"\\server\share\image.gif"));
    }

    [Fact]
    public async Task VerifiedCopyIsRecycledBeforeOriginalIsRemoved()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        var bin = directory.GetPath("simulated-bin.gif");
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(source, bytes);
        await VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(source, directory.GetPath("stage"), copy =>
        {
            Assert.True(File.Exists(source));
            Assert.Equal(bytes, File.ReadAllBytes(copy));
            Assert.Equal("image.gif", Path.GetFileName(copy));
            Assert.Equal(source, File.ReadAllText(copy + ".origin.txt"));
            Assert.Throws<IOException>(() => File.WriteAllText(source, "changed"));
            File.Move(copy, bin);
        }, default);
        Assert.False(File.Exists(source));
        Assert.Equal(bytes, File.ReadAllBytes(bin));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrIncompleteRecycleKeepsOriginal(bool throws)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        File.WriteAllText(source, "original");
        await Assert.ThrowsAsync<IOException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
            source, directory.GetPath("stage"), _ =>
            {
                if (throws) throw new IOException("Simulated recycle failure");
            }, default));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task CancellationKeepsOriginal()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        File.WriteAllText(source, "original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
            source, directory.GetPath("stage"), _ => Assert.Fail("Must not recycle"), new CancellationToken(true)));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task UnavailableStagingKeepsOriginal()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("image.gif");
        var stage = directory.GetPath("stage");
        File.WriteAllText(source, "original");
        File.WriteAllText(stage, "not a directory");
        await Assert.ThrowsAnyAsync<IOException>(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(
            source, stage, _ => Assert.Fail("Must not recycle"), default));
        Assert.Equal("original", File.ReadAllText(source));
    }
}
