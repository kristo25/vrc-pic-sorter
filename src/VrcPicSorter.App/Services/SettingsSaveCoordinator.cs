namespace VrcPicSorter.App.Services;

/// <summary>Versions user intent separately from serialized persistence.</summary>
public sealed class SettingsSaveCoordinator
{
    private readonly LatestRequestGuard _requests = new();
    public long Begin() => _requests.Begin();
    public bool IsCurrent(long request) => _requests.IsCurrent(request);

    public async Task<bool> SaveAsync<T>(long request, Func<Task<T>> prepare,
        Func<Func<Task>, Task> runExclusive, Func<T, Task> apply)
    {
        try
        {
            var prepared = await prepare();
            if (!IsCurrent(request)) return false;
            var applied = false;
            await runExclusive(async () =>
            {
                if (!IsCurrent(request)) return;
                await apply(prepared);
                applied = true;
            });
            return applied && IsCurrent(request);
        }
        catch when (!IsCurrent(request))
        {
            // A superseded failure must not replace the newest request's status.
            return false;
        }
    }
}
