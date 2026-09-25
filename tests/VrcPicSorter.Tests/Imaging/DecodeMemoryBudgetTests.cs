using VrcPicSorter.Core.Imaging;

namespace VrcPicSorter.Tests.Imaging;

public sealed class DecodeMemoryBudgetTests
{
    [Fact]
    public async Task SmallImagesStillShareBudgetAndAnimatedFramesCount()
    {
        using var budget = new DecodeMemoryBudget();
        using var one = await budget.AcquireAsync(DecodeMemoryBudget.EstimateBytes(128, 128, 1), default);
        using var two = await budget.AcquireAsync(DecodeMemoryBudget.EstimateBytes(128, 128, 1), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DecodeMemoryBudget.EstimateBytes(128, 128, 1) * 100,
            DecodeMemoryBudget.EstimateBytes(128, 128, 100));
    }

    [Fact]
    public async Task CompressedLargeImagesCannotDecodeTogetherBeyondBudget()
    {
        using var budget = new DecodeMemoryBudget();
        var cost = DecodeMemoryBudget.EstimateBytes(4096, 4096, 1);
        Assert.Equal(192L * 1024 * 1024, cost);
        using var first = await budget.AcquireAsync(cost, default);
        var second = budget.AcquireAsync(cost, default);
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var released = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledReservationReturnsPartiallyAcquiredCapacity()
    {
        using var budget = new DecodeMemoryBudget();
        using var first = await budget.AcquireAsync(128L * 1024 * 1024, default);
        using var cancellation = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(192L * 1024 * 1024, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using var all = await budget.AcquireAsync(512L * 1024 * 1024, default).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
