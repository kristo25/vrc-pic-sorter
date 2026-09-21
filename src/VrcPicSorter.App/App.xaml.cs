using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VrcPicSorter.App.Services;
using MessageBox = System.Windows.MessageBox;

namespace VrcPicSorter.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private AppRuntime? _runtime;
    private TrayService? _tray;
    private bool _explicitExit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLaunchOptions options;
        try
        {
            options = AppLaunchOptions.Parse(e.Args);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "VRC Pic Sorter", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _singleInstance = new SingleInstanceService(options.InstanceName);
        if (!_singleInstance.TryAcquireOwnership())
        {
            _singleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _singleInstance.StartActivationListener(ActivateMainWindow);

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _runtime = new AppRuntime(options.DataDirectory, allowStartupRegistration: !options.IsIsolated);
            await _runtime.InitializeAsync();
            var window = new MainWindow(_runtime);
            MainWindow = window;
            window.Closing += MainWindowClosing;
            _tray = new TrayService(
                ActivateMainWindow,
                window.ScanFromTrayAsync,
                ReportTrayScanFailureAsync,
                ExitApplication);
            _runtime.Watcher.ScanCompleted += WatcherScanCompleted;
            _runtime.Watcher.ScanFailed += WatcherScanFailed;
            _runtime.Watcher.ChangesDetected += WatcherChangesDetected;

            if (options.Background && !_runtime.Watcher.IsRunning)
            {
                await _runtime.StartWatchingAsync();
            }

            if (!options.Background)
            {
                window.Show();
            }
        }
        catch (Exception exception)
        {
            var diagnosticDirectory = options.DataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VrcPicSorter");
            Directory.CreateDirectory(diagnosticDirectory);
            File.WriteAllText(Path.Combine(diagnosticDirectory, "startup-error.log"), exception.ToString());
            MessageBox.Show(
                exception.Message,
                "VRC Pic Sorter could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private string DiagnosticDirectory => _runtime?.StateDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcPicSorter");

    /// <summary>
    /// Keeps the process alive after an unexpected UI failure. Terminating here would abandon
    /// any file operation whose journal entry still needs reconciliation, so the safer outcome
    /// is to report the failure and let the user retry or use Operation recovery.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var logPath = DiagnosticLog.TryWrite(DiagnosticDirectory, e.Exception);
        try
        {
            if (MainWindow is MainWindow window)
            {
                window.ReportBackgroundFailure("Unexpected error", e.Exception.Message, logPath);
            }

            var details = logPath is null ? string.Empty : $"\n\nDetails were written to:\n{logPath}";
            MessageBox.Show(
                $"{ActionOutcomeText.Interrupted(cancelled: false)}\n\n{e.Exception.Message}{details}",
                "VRC Pic Sorter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception reportingException)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to report an unhandled exception: {reportingException}");
        }
    }

    /// <summary>
    /// The runtime terminates after this callback, so it only records what happened.
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _ = DiagnosticLog.TryWrite(DiagnosticDirectory, exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        _ = DiagnosticLog.TryWrite(DiagnosticDirectory, e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _runtime?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ActivateMainWindow()
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (MainWindow is null)
                {
                    return;
                }

                if (MainWindow.WindowState == WindowState.Minimized)
                {
                    MainWindow.WindowState = WindowState.Normal;
                }

                MainWindow.Show();
                MainWindow.Activate();
            });
    }

    private void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_explicitExit)
        {
            return;
        }

        if (_runtime?.Watcher.IsRunning != true)
        {
            _explicitExit = true;
            _ = Dispatcher.BeginInvoke(new Action(Shutdown));
            return;
        }

        e.Cancel = true;
        MainWindow?.Hide();
        _tray?.ShowNotification("VRC Pic Sorter", "Monitoring continues in the system tray.");
    }

    private void WatcherScanCompleted(object? sender, Core.Scanning.CategoryScanResult result)
    {
        _ = AsyncCommandRunner.RunAsync(
            () => HandleWatcherScanCompletedAsync(result),
            exception => ReportBackgroundEventFailureAsync("Watcher completion", exception));
    }

    private async Task HandleWatcherScanCompletedAsync(Core.Scanning.CategoryScanResult result)
    {
        if (_runtime is null)
        {
            return;
        }

        var state = await _runtime.StateStore.LoadAsync();
        var refreshTask = await Dispatcher.InvokeAsync(
            () =>
            {
                if (MainWindow is MainWindow window)
                {
                    return window.RefreshFromExternalAsync();
                }

                return Task.CompletedTask;
            });
        await refreshTask;

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (result.HeldForReview > 0)
                {
                    _tray?.ShowNotification(
                        "Images ready for review",
                        $"{result.HeldForReview} {result.Category} image(s) need a decision.");
                    if (state.Settings.BringReviewForwardWhenHeld)
                    {
                        ActivateMainWindow();
                    }
                }
            });
    }

    private void WatcherChangesDetected(object? sender, WatchDetectionEventArgs detection)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.ReportWatchDetection(detection.Category, detection.FileName);
                }
            });
    }

    private void WatcherScanFailed(object? sender, WatcherFailureEventArgs failure)
    {
        _ = AsyncCommandRunner.RunAsync(
            () => HandleWatcherScanFailedAsync(failure),
            exception => ReportBackgroundEventFailureAsync("Watcher failure reporting", exception));
    }

    private async Task HandleWatcherScanFailedAsync(WatcherFailureEventArgs failure)
    {
        if (_runtime is null)
        {
            return;
        }

        var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, failure.Exception);
        await Dispatcher.InvokeAsync(
            () =>
            {
                _tray?.ShowNotification(
                    "Folder monitoring needs attention",
                    $"The {failure.Category} scan failed. Open VRC Pic Sorter for details.");
                if (MainWindow is MainWindow window)
                {
                    window.ReportWatcherFailure(failure.Category, failure.Exception.Message, logPath);
                }
            });
    }

    private async Task ReportTrayScanFailureAsync(Exception exception)
    {
        var stateDirectory = _runtime?.StateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcPicSorter");
        var logPath = await DiagnosticLog.TryWriteAsync(stateDirectory, exception);
        await Dispatcher.InvokeAsync(
            () =>
            {
                _tray?.ShowNotification("Scan failed", "Open VRC Pic Sorter for details.");
                if (MainWindow is MainWindow window)
                {
                    window.ReportBackgroundFailure("Tray scan", exception.Message, logPath);
                }
            });
    }

    private async Task ReportBackgroundEventFailureAsync(string operation, Exception exception)
    {
        var stateDirectory = _runtime?.StateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcPicSorter");
        var logPath = await DiagnosticLog.TryWriteAsync(stateDirectory, exception);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            () =>
            {
                _tray?.ShowNotification("Background operation failed", "Open VRC Pic Sorter for details.");
                if (MainWindow is MainWindow window)
                {
                    window.ReportBackgroundFailure(operation, exception.Message, logPath);
                }
            });
    }

    private void ExitApplication()
    {
        _explicitExit = true;
        Shutdown();
    }
}
