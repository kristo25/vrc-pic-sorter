using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Services;

public sealed class ComparisonPlaybackTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(40, 1, 0)]
    [InlineData(100, 2, 1)]
    [InlineData(120, 0, 1)]
    [InlineData(200, 2, 0)]
    public void SharedElapsedTimeRespectsEachAnimationsFrameDelays(double elapsed, int first, int second)
    {
        Assert.Equal(first, ComparisonPlayback.FrameIndexAt([40, 80, 120], 120, elapsed));
        Assert.Equal(second, ComparisonPlayback.FrameIndexAt([100, 200], 200, elapsed));
    }

    [Fact]
    public void IndependentlyStartedPreviewUsesItsOwnOffset()
    {
        Assert.Equal(2, ComparisonPlayback.FrameIndexAt([40, 80, 120], 120, 100));
        Assert.Equal(0, ComparisonPlayback.FrameIndexAt([40, 80, 120], 120, 100 - 90));
    }
}
