// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Servers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace AgentShift.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://AgentShift/Assets/THIRD-PARTY-NOTICES.txt"));
            using var r = new StreamReader(s);
            NoticesBox.Text = r.ReadToEnd();
        }
        catch (Exception)
        {
            NoticesBox.Text = "See THIRD-PARTY-NOTICES.txt in the source repository.";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnSourceClick(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(AppInfo.RepositoryUrl));

    private async void OnLicenseClick(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://www.gnu.org/licenses/gpl-3.0.html"));
}
