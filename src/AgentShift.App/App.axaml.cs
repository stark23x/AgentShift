// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using AgentShift.App.ViewModels;
using AgentShift.App.Views;
using AgentShift.Core.Comparison;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace AgentShift.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(AppServices.Instance.Settings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(AppServices.Instance);
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>"System" follows the OS light/dark setting (and updates live when it changes).</summary>
    public static void ApplyTheme(string? theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}

/// <summary>Colour for a show's status dot: green, blue, amber, red.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#22a06b"));
    public static readonly IBrush Minor = new SolidColorBrush(Color.Parse("#3b82f6"));
    public static readonly IBrush Review = new SolidColorBrush(Color.Parse("#e5a50a"));
    public static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#e01b24"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        0 => Ok,
        1 => Minor,
        2 => Review,
        _ => Bad,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ChangeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EpisodeChange.None => SeverityToBrushConverter.Ok,
        EpisodeChange.TitleOnly => SeverityToBrushConverter.Minor,
        EpisodeChange.Uncertain => SeverityToBrushConverter.Review,
        _ => SeverityToBrushConverter.Bad,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
