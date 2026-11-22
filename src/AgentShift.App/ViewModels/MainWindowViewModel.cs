// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using AgentShift.Core.Comparison;
using AgentShift.Core.Models;
using AgentShift.Core.Scanning;
using AgentShift.Core.Servers;
using AgentShift.Core.Settings;
using AgentShift.Core.Tmdb;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentShift.App.ViewModels;

public sealed partial class LibraryChoice : ObservableObject
{
    public LibraryChoice(MediaLibrary library) => Library = library;
    public MediaLibrary Library { get; }
    public string Label => $"{Library.Name}  ·  {Library.DefaultOrderingLabel ?? Library.Source}";

    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<ShowResult> _all = new();
    private CancellationTokenSource? _cts;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        ReloadProfiles();
    }

    public ObservableCollection<ServerProfile> Profiles { get; } = new();
    public ObservableCollection<LibraryChoice> Libraries { get; } = new();
    public ObservableCollection<ShowResult> Results { get; } = new();

    [ObservableProperty] private ServerProfile? _selectedProfile;
    [ObservableProperty] private ShowResult? _selectedResult;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyChanges = true;
    [ObservableProperty] private bool _onlyChangedEpisodes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(LoadLibrariesCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private bool _progressIndeterminate;

    /// <summary>Raised when something needs the user's attention (title, message).</summary>
    public event Action<string, string>? MessageRequested;

    public bool HasResults => _all.Count > 0;
    public bool HasSelection => SelectedResult != null;
    public IReadOnlyList<ShowResult> AllResults => _all;

    public IEnumerable<EpisodeResult> SelectedEpisodes =>
        SelectedResult?.Episodes.Where(e => !OnlyChangedEpisodes || e.Change != EpisodeChange.None) ?? Enumerable.Empty<EpisodeResult>();

    partial void OnSelectedResultChanged(ShowResult? value)
    {
        OnPropertyChanged(nameof(SelectedEpisodes));
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnOnlyChangedEpisodesChanged(bool value) => OnPropertyChanged(nameof(SelectedEpisodes));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnOnlyChangesChanged(bool value) => ApplyFilter();

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        Libraries.Clear();
        if (value is null)
        {
            StatusText = "Open Settings to add your Plex, Emby or Jellyfin server.";
            return;
        }
        _services.Settings.SelectedProfileId = value.Id;
        _services.Save();
        StatusText = $"Click \"Load libraries\" to see the TV libraries on {value.Name}.";
    }

    public void ReloadProfiles()
    {
        var keep = SelectedProfile?.Id ?? _services.Settings.SelectedProfileId;
        Profiles.Clear();
        foreach (var p in _services.Settings.Profiles) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == keep) ?? Profiles.FirstOrDefault();
        if (SelectedProfile is null) OnSelectedProfileChanged(null);
    }

    private bool CanStart() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task LoadLibrariesAsync()
    {
        if (!TryCreateConnector(out var server)) return;
        IsBusy = true;
        ProgressIndeterminate = true;
        StatusText = $"Connecting to {SelectedProfile!.Name}…";
        try
        {
            using (server)
            {
                var id = await server.TestAsync();
                var libs = await server.GetTvLibrariesAsync();
                Libraries.Clear();
                foreach (var l in libs) Libraries.Add(new LibraryChoice(l));
                StatusText = libs.Count == 0
                    ? $"Connected to {id.Name}, but it has no TV libraries."
                    : $"Connected to {id.Name} {id.Version}. Pick libraries and click Scan.";
            }
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException or UriFormatException)
        {
            StatusText = "Couldn't connect.";
            MessageRequested?.Invoke("Couldn't connect", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ScanAsync()
    {
        var libs = Libraries.Where(l => l.IsSelected).Select(l => l.Library).ToList();
        if (libs.Count == 0)
        {
            MessageRequested?.Invoke("Nothing to scan", "Load libraries first, then tick at least one TV library.");
            return;
        }
        var key = _services.Secrets.Get(AppSettings.TmdbSecretKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageRequested?.Invoke("TMDB key needed",
                "AgentShift needs a free TMDB API key to look up TMDB's episode order. Add one in Settings.");
            return;
        }
        if (!TryCreateConnector(out var server)) return;

        var settings = _services.Settings;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        _all.Clear();
        Results.Clear();
        SelectedResult = null;
        OnPropertyChanged(nameof(HasResults));
        StatusText = "Checking your TMDB key…";

        try
        {
            using (server)
            using (var tmdb = new TmdbClient(key, settings.TmdbLanguage))
            {
                await tmdb.ValidateAsync(ct);
                var scanner = new LibraryScanner(server, tmdb, new ScanOptions
                {
                    CompareShowsAlreadyOnTmdb = settings.CompareShowsAlreadyOnTmdb,
                    UseEpisodeLinks = settings.UseEpisodeLinks,
                    CheckEpisodeGroups = settings.CheckEpisodeGroups,
                });
                var progress = new Progress<ScanProgress>(p =>
                {
                    StatusText = p.Total > 0 ? $"{p.Done} of {p.Total} shows checked. {p.Message}" : p.Message;
                    if (p.Total > 0)
                    {
                        ProgressIndeterminate = false;
                        ProgressMax = p.Total;
                        ProgressValue = p.Done;
                    }
                });
                await Task.Run(() => scanner.ScanAsync(libs, r => Dispatcher.UIThread.Post(() => AddResult(r)), progress, ct), ct);
            }
            // Let queued results land before summarising.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ApplyFilter();
            StatusText = $"Done. {_all.Count} shows checked.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled. {_all.Count} shows were checked before stopping.";
            ApplyFilter();
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException)
        {
            StatusText = "The scan stopped.";
            MessageRequested?.Invoke("The scan stopped", ex.Message);
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void AddResult(ShowResult r)
    {
        _all.Add(r);
        if (Matches(r)) Results.Add(r);
        UpdateSummary();
        if (_all.Count == 1) OnPropertyChanged(nameof(HasResults));
    }

    private bool Matches(ShowResult r) =>
        (!OnlyChanges || r.Severity > 0) &&
        (string.IsNullOrWhiteSpace(SearchText) || r.Title.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void ApplyFilter()
    {
        var keep = SelectedResult;
        Results.Clear();
        foreach (var r in _all.Where(Matches).OrderByDescending(r => r.Severity).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
            Results.Add(r);
        SelectedResult = keep != null && Results.Contains(keep) ? keep : Results.FirstOrDefault();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_all.Count == 0)
        {
            SummaryText = "";
            return;
        }
        var parts = _all.GroupBy(r => r.Status).OrderByDescending(g => (int)g.First().Severity)
            .Select(g => $"{g.Count()} {g.First().StatusText.ToLowerInvariant()}");
        SummaryText = $"{_all.Count} shows: " + string.Join(", ", parts);
    }

    private bool TryCreateConnector(out IMediaServerConnector server)
    {
        server = null!;
        var profile = SelectedProfile;
        if (profile is null)
        {
            MessageRequested?.Invoke("No server", "Open Settings and add your Plex, Emby or Jellyfin server first.");
            return false;
        }
        var token = _services.Secrets.Get(profile.SecretKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            var what = profile.Type == ServerType.Plex ? "Plex token" : "API key or sign-in";
            MessageRequested?.Invoke("Missing token", $"\"{profile.Name}\" has no {what} yet. Add it in Settings.");
            return false;
        }
        try
        {
            server = ConnectorFactory.Create(profile, token, _services.Settings.ClientId);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            MessageRequested?.Invoke("Check the server address", ex.Message);
            return false;
        }
    }
}
