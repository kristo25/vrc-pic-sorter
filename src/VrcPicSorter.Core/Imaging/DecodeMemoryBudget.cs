namespace VrcPicSorter.Core.Imaging;

/// <summary>Bounds concurrent decode estimates; a single oversized image runs exclusively.</summary>
internal sealed class DecodeMemoryBudget : IDisposable
{
    private const long UnitBytes = 1024 * 1024;
    private const int Units = 256;
    private long _waits;
    internal long Waits => Interlocked.Read(ref _waits);
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly SemaphoreSlim _available = new(Units, Units);

    internal static long EstimateBytes(int width, int height, int frames)
    {
        ImageResourceLimits.EnsureSafe(width, height, frames);
        // Decoder pixels, returned frame bytes, and fingerprint workspace can coexist.
        return checked((long)width * height * frames * 4 * 3);
    }

    public async Task<IDisposable> AcquireAsync(long estimatedBytes, CancellationToken token)
    {
        var units = (int)Math.Clamp((estimatedBytes + UnitBytes - 1) / UnitBytes, 1, Units);
        if (_available.CurrentCount < units) Interlocked.Increment(ref _waits);
        await _admission.WaitAsync(token).ConfigureAwait(false);
        var acquired = 0;
        try
        {
            while (acquired < units)
            {
                await _available.WaitAsync(token).ConfigureAwait(false);
                acquired++;
            }
            return new Lease(_available, acquired);
        }
        catch
        {
            if (acquired > 0) _available.Release(acquired);
            throw;
        }
        finally { _admission.Release(); }
    }

    private sealed class Lease(SemaphoreSlim available, int units) : IDisposable
    {
        private int _units = units;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _units, 0);
            if (released > 0) available.Release(released);
        }
    }

    public void Dispose() { _admission.Dispose(); _available.Dispose(); }
}
