// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using AgentShift.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;

namespace AgentShift.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow() : this(new SettingsViewModel(AppServices.Instance)) { }

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Func<Uri, Task> open = async uri => await Launcher.LaunchUriAsync(uri);
        vm.OpenBrowser = open;
        foreach (var p in vm.Profiles) p.OpenBrowser = open;
        DataContext = vm;
        Title = "AgentShift Settings  ·  " + vm.SecretStoreDescription;
        Opened += (_, _) =>
        {
            if (vm.Profiles.Count == 0) vm.AddProfileCommand.Execute(null);
        };
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Save();
            Close(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save settings", ex.Message);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnTmdbSignupClick(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://www.themoviedb.org/settings/api"));
}

/// <summary>http/https picker: false ↔ 0, true ↔ 1.</summary>
public sealed class BoolToIndexConverter : IValueConverter
{
    public static readonly BoolToIndexConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 1;
}
