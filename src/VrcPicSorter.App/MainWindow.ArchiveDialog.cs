using System.Windows;
using System.Windows.Controls;
using VrcPicSorter.Core.FileSystem;
using Button = System.Windows.Controls.Button;

namespace VrcPicSorter.App;

public partial class MainWindow
{
    private Window? _archiveMoveDialog;

    private async void OpenArchiveMoveDialog(object sender, RoutedEventArgs e) => await ShowArchiveMoveDialogAsync();

    private async Task ShowArchiveMoveDialogAsync()
    {
        if (_busy) return;
        if (_archiveMoveDialog is not null)
        {
            _archiveMoveDialog.Activate();
            await PrepareArchiveFoldersAsync();
            await RefreshRetainedArchivesAsync();
            return;
        }
        var parent = (System.Windows.Controls.Panel)RetainedArchivesPanel.Parent;
        var position = parent.Children.IndexOf(RetainedArchivesPanel);
        parent.Children.Remove(RetainedArchivesPanel);
        var content = new DockPanel { Margin = new Thickness(12) };
        var close = new Button { Content = "Close", HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        notice.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text") { Source = SettingsNoticeText });
        DockPanel.SetDock(notice, Dock.Top);
        content.Children.Add(notice);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = RetainedArchivesPanel };
        content.Children.Add(scroll);
        var dialog = new Window
        {
            Owner = this,
            Title = "Move previous archives",
            Width = 800,
            Height = 550,
            MinWidth = 500,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush"),
            Content = content
        };
        _archiveMoveDialog = dialog;
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) =>
        {
            scroll.Content = null;
            parent.Children.Insert(position, RetainedArchivesPanel);
            RetainedArchivesPanel.Visibility = Visibility.Collapsed;
            _archiveMoveDialog = null;
        };
        dialog.Show();
        await PrepareArchiveFoldersAsync();
        await RefreshRetainedArchivesAsync();
    }

    private async Task PrepareArchiveFoldersAsync()
    {
        await _uiMutationGate.WaitAsync();
        try
        {
            await _runtime.Scanner.RunExclusiveAsync(async () =>
            {
                var settings = (await _runtime.StateStore.LoadAsync()).Settings;
                var errors = await Task.Run(() => ArchiveRelocation.PrepareMissingArchiveFolders(settings));
                if (errors.Count > 0) ShowSettingsNotice(string.Join(Environment.NewLine, errors), true);
            });
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowSettingsNotice($"Could not prepare archive folders: {error.Message}", true);
        }
        finally { _uiMutationGate.Release(); }
    }
}
