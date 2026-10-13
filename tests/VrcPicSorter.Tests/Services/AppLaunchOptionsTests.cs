using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Services;

public sealed class AppLaunchOptionsTests
{
    [Fact]
    public void CodecVerificationIsAnExplicitStandaloneCommand()
    {
        var options = AppLaunchOptions.Parse(["--verify-codec"]);
        Assert.True(options.VerifyCodec);
        Assert.False(options.Background);
        Assert.Null(options.DataDirectory);
        Assert.False(AppLaunchOptions.Parse([]).VerifyCodec);
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--verify-codec", "--background"]));
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--verify-codec", "--data-dir", Path.GetTempPath()]));
    }

    [Fact]
    public void DataDirectoryIsNormalizedAndUsesAnIsolatedInstanceName()
    {
        using var directory = new TestDirectory();

        var options = AppLaunchOptions.Parse(["--data-dir", directory.Path, "--background"]);

        Assert.True(options.Background);
        Assert.True(options.IsIsolated);
        Assert.Equal(Path.GetFullPath(directory.Path), options.DataDirectory);
        Assert.StartsWith("VrcPicSorter-", options.InstanceName);
        Assert.NotEqual("VrcPicSorter", options.InstanceName);
    }

    [Fact]
    public void MissingOrRelativeDataDirectoryIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-dir"]));
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-dir", "relative"]));
    }
}
