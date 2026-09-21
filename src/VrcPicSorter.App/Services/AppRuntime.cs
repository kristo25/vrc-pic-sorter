using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.App.Services;

public sealed class AppRuntime : IDisposable
{
    internal static readonly TimeSpan ProductionFileSettleDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>Where this application kept its data before the rename, or null when isolated.</summary>
    private readonly string? _previousStateDirectory;
    private readonly WatcherLifecycleCoordinator _watcherLifecycle;

    public AppRuntime(string? stateDirectory = null, bool allowStartupRegistration = true)
    {
        var isolated = stateDirectory is not null;
        if (stateDirectory is not null)
        {
            StateDirectory = Path.GetFullPath(stateDirectory);
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            StateDirectory = Path.Combine(localAppData, "VrcPicSorter");
            _previousStateDirectory = Path.Combine(localAppData, LocalDataMigration.PreviousFolderName);

            // The application answered to another name until 1.4.0, and everything it remembers
            // lives in a folder named after it. Carried across here rather than anywhere later,
            // because the state store below reads that folder the moment it is constructed. A
            // folder given with --data-dir is left alone: it was named by whoever passed it.
            LocalDataMigration.CarryOver(_previousStateDirectory, StateDirectory);
        }
        AllowStartupRegistration = allowStartupRegistration;
        StateStore = new JsonStateStore(
            StateDirectory,
            () => isolated
                ? AppStateDefaults.Create(
                    Path.Combine(StateDirectory, "Profile"),
                    StateDirectory,
                    StateDirectory)
                : AppStateDefaults.Create(stateDirectoryPath: StateDirectory));
        Decoder = new ImageDecoder();
        Indexer = new ArchiveIndexer(StateStore, Decoder);
        Router = new FileRouter(
            StateStore,
            Decoder,
            new WindowsRecycleBinService(RecycleBinPolicy.IsDisabledForVolume));
        Scanner = new ScanCoordinator(
            StateStore,
            Indexer,
            Decoder,
            Router,
            ProductionFileSettleDelay);
        Watcher = new WatchService(Scanner, Indexer);
        _watcherLifecycle = new WatcherLifecycleCoordinator(StartWatcherFromSavedSettingsAsync, Watcher.StopAsync);
        Startup = new StartupRegistrationService();
    }

    public JsonStateStore StateStore { get; }

    public string StateDirectory { get; }

    public bool AllowStartupRegistration { get; }

    public ImageDecoder Decoder { get; }

    public ArchiveIndexer Indexer { get; }

    public FileRouter Router { get; }

    public ScanCoordinator Scanner { get; }

    public WatchService Watcher { get; }

    public bool WatchingRequested => _watcherLifecycle.IsRequested;

    public StartupRegistrationService Startup { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _ = await StateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Before recovery, not after. Moving the data folder leaves the paths recorded inside the
        // document still naming the old one, and recovery is the first thing to act on them: an
        // unfinished operation naming the vanished folder found neither its source nor its
        // destination and went straight to Needs attention, where the repair that would have made
        // it reconcilable arrived a moment too late to help. Repaired on every start rather than
        // only in the run that moved the folder, because a crash between the two would otherwise
        // strand a path pointing at a folder that is gone.
        if (_previousStateDirectory is { } previousDirectory)
        {
            await RepairMigratedPathsAsync(previousDirectory, cancellationToken).ConfigureAwait(false);
        }

        _ = await Router.RecoverPendingOperationsAsync(cancellationToken).ConfigureAwait(false);

        // A start-with-Windows registration made under the old name would otherwise keep launching
        // whatever now sits at the old executable's path, while Settings reported the option as
        // off. This is the first point where the executable's own path is known.
        if (AllowStartupRegistration && Environment.ProcessPath is { } executablePath)
        {
            Startup.CarryOverPreviousName(executablePath);
        }
    }

    private async Task RepairMigratedPathsAsync(string previousDirectory, CancellationToken cancellationToken)
    {
        // Checked before writing: every start would otherwise rewrite the state document to say
        // exactly what it already said.
        var state = await StateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!LocalDataMigration.NeedsRebase(state, previousDirectory, StateDirectory))
        {
            return;
        }

        _ = await StateStore.UpdateAsync(
                document => LocalDataMigration.RebasePaths(
                    document,
                    previousDirectory,
                    StateDirectory),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ApplyAutomationSettingsAsync(bool updateStartupRegistration)
    {
        await _watcherLifecycle.RestartAsync().ConfigureAwait(false);
        var state = await StateStore.LoadAsync().ConfigureAwait(false);

        if (updateStartupRegistration && AllowStartupRegistration)
        {
            Startup.SetEnabled(
                state.Settings.Automation.StartWithWindows,
                Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable."));
        }
    }

    public Task StartWatchingAsync() => _watcherLifecycle.StartAsync();

    private async Task StartWatcherFromSavedSettingsAsync()
    {
        var state = await StateStore.LoadAsync().ConfigureAwait(false);
        Watcher.Start(
            state.Settings.CategoryMappings,
            state.Settings.LegacyArchiveMappings,
            SweepInterval(state.Settings.Automation),
            state.Settings.Automation.WatchMode == WatchMode.OnDetection);
    }

    public Task StopWatchingAsync() => _watcherLifecycle.StopAsync();

    private static TimeSpan? SweepInterval(AutomationSettings automation) =>
        automation.WatchMode == WatchMode.OnInterval ? automation.WatchScanInterval : null;

    public void Dispose()
    {
        Watcher.Dispose();
        StateStore.Dispose();
    }
}
