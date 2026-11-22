// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.App.ViewModels;
using AgentShift.Core.Export;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AgentShift.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.MessageRequested += (title, text) => _ = Dialogs.ShowMessageAsync(this, title, text);
        };
        Opened += (_, _) =>
        {
            // First run: go straight to Settings so people can add their server.
            if (Vm.Profiles.Count == 0) OnSettingsClick(this, new RoutedEventArgs());
        };
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(new SettingsViewModel(AppServices.Instance));
        var saved = await win.ShowDialog<bool>(this);
        if (saved) Vm.ReloadProfiles();
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    private async void OnOpenTmdbClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedResult?.TmdbUrl is { Length: > 0 } url)
            await Launcher.LaunchUriAsync(new Uri(url));
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export episodes to CSV",
            SuggestedFileName = $"AgentShift episodes {DateTime.Now:yyyy-MM-dd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            var results = Vm.AllResults.OrderByDescending(r => r.Severity).ThenBy(r => r.Title).ToList();
            CsvExporter.WriteEpisodes(path, results);
            var showsPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " - shows.csv");
            CsvExporter.WriteShows(showsPath, results);
            Vm.StatusText = $"Saved {Path.GetFileName(path)} and {Path.GetFileName(showsPath)}.";
        }
        catch (IOException ex)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save", ex.Message);
        }
    }
}
