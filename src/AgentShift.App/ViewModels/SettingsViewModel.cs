// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using AgentShift.Core.Models;
using AgentShift.Core.Servers;
using AgentShift.Core.Settings;
using AgentShift.Core.Tmdb;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentShift.App.ViewModels;

/// <summary>Editable copy of a server profile. Nothing is saved until the user clicks Save.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly Func<string> _clientId;

    public ProfileViewModel(ServerProfile p, string? secret, Func<string> clientId)
    {
        _clientId = clientId;
        Id = p.Id;
        _name = p.Name;
        _type = p.Type;
        _useHttps = p.UseHttps;
        _host = p.Host;
        _port = p.Port;
        _basePath = p.BasePath;
        _allowInvalidCertificate = p.AllowInvalidCertificate;
        _secret = secret ?? "";
        UserId = p.UserId;
        _userName = p.UserName;
    }

    public Guid Id { get; }
    public string? UserId { get; private set; }

    /// <summary>Set by the window so the view model can open the Plex sign-in page.</summary>
    public Func<Uri, Task>? OpenBrowser { get; set; }

    public static IReadOnlyList<ServerType> ServerTypes { get; } = Enum.GetValues<ServerType>();

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlex), nameof(IsEmbyFamily), nameof(SecretLabel), nameof(SecretHelp))]
    private ServerType _type;

    [ObservableProperty] private bool _useHttps;
    [ObservableProperty] private string _host;
    [ObservableProperty] private decimal? _port;
    [ObservableProperty] private string _basePath;
    [ObservableProperty] private bool _allowInvalidCertificate;
    [ObservableProperty] private string _secret;
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private string _signInUser = "";
    [ObservableProperty] private string _signInPassword = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isWorking;

    public bool IsPlex => Type == ServerType.Plex;
    public bool IsEmbyFamily => Type != ServerType.Plex;
    public string SecretLabel => IsPlex ? "Plex token" : "API key or access token";
    public string SecretHelp => IsPlex
        ? "Click \"Sign in with Plex\" to get this automatically, or paste a token you already have."
        : $"Create an API key in the {Type} dashboard (Dashboard → API Keys), or sign in with a username below.";

    partial void OnTypeChanged(ServerType oldValue, ServerType newValue)
    {
        if (Port is null || (int)Port.Value == ServerProfile.DefaultPort(oldValue))
            Port = ServerProfile.DefaultPort(newValue);
    }

    public ServerProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "My server" : Name.Trim(),
        Type = Type,
        UseHttps = UseHttps,
        Host = (Host ?? "").Trim(),
        Port = (int)(Port ?? ServerProfile.DefaultPort(Type)),
        BasePath = (BasePath ?? "").Trim(),
        AllowInvalidCertificate = AllowInvalidCertificate,
        UserId = IsEmbyFamily ? UserId : null,
        UserName = IsEmbyFamily ? UserName : null,
    };

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsWorking = true;
        TestResult = "Testing…";
        try
        {
            using var server = ConnectorFactory.Create(ToProfile(), Secret.Trim(), _clientId());
            var id = await server.TestAsync();
            var libs = await server.GetTvLibrariesAsync();
            TestResult = $"✔ Connected to {id.Name} {id.Version}. TV libraries: " +
                         (libs.Count == 0 ? "none" : string.Join(", ", libs.Select(l => l.Name)));
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException or UriFormatException)
        {
            TestResult = "✖ " + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task PlexSignInAsync()
    {
        IsWorking = true;
        TestResult = "Opening plex.tv in your browser. Sign in there, then come back here…";
        try
        {
            using var auth = new PlexAuth(_clientId());
            var pin = await auth.CreatePinAsync();
            if (OpenBrowser != null) await OpenBrowser(pin.SignInUrl);
            var token = await auth.WaitForTokenAsync(pin, TimeSpan.FromMinutes(5));
            if (token is null)
            {
                TestResult = "✖ Sign-in timed out. Try again.";
                return;
            }
            Secret = token;
            TestResult = "✔ Signed in. Token filled in.";

            if (string.IsNullOrWhiteSpace(Host))
            {
                var servers = await auth.GetServersAsync(token);
                var conn = servers.SelectMany(s => s.Connections.Select(c => (s, c)))
                    .OrderByDescending(x => x.c.Local).ThenBy(x => x.c.Protocol == "https" ? 1 : 0)
                    .FirstOrDefault();
                if (conn.c != null)
                {
                    Host = conn.c.Address;
                    Port = conn.c.Port;
                    UseHttps = conn.c.Protocol == "https";
                    if (Name is "My server" or "") Name = conn.s.Name;
                    TestResult += $" Found \"{conn.s.Name}\" at {conn.c.Address}:{conn.c.Port}.";
                }
            }
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException)
        {
            TestResult = "✖ " + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task EmbySignInAsync()
    {
        if (string.IsNullOrWhiteSpace(SignInUser))
        {
            TestResult = "Enter a username first.";
            return;
        }
        IsWorking = true;
        TestResult = "Signing in…";
        try
        {
            var (token, userId, userName) = await EmbyFamilyConnector.SignInAsync(ToProfile(), _clientId(), SignInUser.Trim(), SignInPassword);
            Secret = token;
            UserId = string.IsNullOrEmpty(userId) ? null : userId;
            UserName = userName;
            SignInPassword = "";
            TestResult = $"✔ Signed in as {userName}. The password isn't saved, only the access token.";
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException or UriFormatException)
        {
            TestResult = "✖ " + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HashSet<Guid> _removed = new();

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;
        foreach (var p in s.Profiles)
            Profiles.Add(new ProfileViewModel(p, services.Secrets.Get(p.SecretKey), () => _services.Settings.ClientId));
        _selectedProfile = Profiles.FirstOrDefault(p => p.Id == s.SelectedProfileId) ?? Profiles.FirstOrDefault();
        _tmdbKey = services.Secrets.Get(AppSettings.TmdbSecretKey) ?? "";
        _tmdbLanguage = s.TmdbLanguage;
        _useEpisodeLinks = s.UseEpisodeLinks;
        _compareShowsAlreadyOnTmdb = s.CompareShowsAlreadyOnTmdb;
        _checkEpisodeGroups = s.CheckEpisodeGroups;
    }

    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();
    public string SecretStoreDescription => $"Tokens and keys are stored in {_services.Secrets.Description}.";
    public bool SecretStoreIsInsecure => !_services.Secrets.IsSecure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    private ProfileViewModel? _selectedProfile;

    [ObservableProperty] private string _tmdbKey;
    [ObservableProperty] private string _tmdbLanguage;
    [ObservableProperty] private bool _useEpisodeLinks;
    [ObservableProperty] private bool _compareShowsAlreadyOnTmdb;
    [ObservableProperty] private bool _checkEpisodeGroups;
    [ObservableProperty] private string _tmdbTestResult = "";

    public bool HasProfile => SelectedProfile != null;

    public Func<Uri, Task>? OpenBrowser { get; set; }

    partial void OnSelectedProfileChanged(ProfileViewModel? value)
    {
        if (value != null) value.OpenBrowser = OpenBrowser;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var p = new ProfileViewModel(new ServerProfile { Name = Profiles.Count == 0 ? "Home" : $"Server {Profiles.Count + 1}" },
            null, () => _services.Settings.ClientId) { OpenBrowser = OpenBrowser };
        Profiles.Add(p);
        SelectedProfile = p;
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile is not { } p) return;
        _removed.Add(p.Id);
        var i = Profiles.IndexOf(p);
        Profiles.Remove(p);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(i, Profiles.Count - 1)];
    }

    [RelayCommand]
    private async Task TestTmdbAsync()
    {
        if (string.IsNullOrWhiteSpace(TmdbKey))
        {
            TmdbTestResult = "Paste your TMDB API key first.";
            return;
        }
        TmdbTestResult = "Testing…";
        try
        {
            using var c = new TmdbClient(TmdbKey, TmdbLanguage);
            await c.ValidateAsync();
            TmdbTestResult = "✔ TMDB accepted the key.";
        }
        catch (ServerException ex)
        {
            TmdbTestResult = "✖ " + ex.Message;
        }
    }

    /// <summary>Writes settings.json and the secure store.</summary>
    public void Save()
    {
        var s = _services.Settings;
        s.Profiles = Profiles.Select(p => p.ToProfile()).ToList();
        s.SelectedProfileId = SelectedProfile?.Id ?? s.Profiles.FirstOrDefault()?.Id;
        s.TmdbLanguage = string.IsNullOrWhiteSpace(TmdbLanguage) ? "en-US" : TmdbLanguage.Trim();
        s.UseEpisodeLinks = UseEpisodeLinks;
        s.CompareShowsAlreadyOnTmdb = CompareShowsAlreadyOnTmdb;
        s.CheckEpisodeGroups = CheckEpisodeGroups;

        foreach (var p in Profiles)
        {
            var key = p.ToProfile().SecretKey;
            if (string.IsNullOrWhiteSpace(p.Secret)) _services.Secrets.Delete(key);
            else _services.Secrets.Set(key, p.Secret.Trim());
        }
        foreach (var id in _removed)
            _services.Secrets.Delete(new ServerProfile { Id = id }.SecretKey);

        if (string.IsNullOrWhiteSpace(TmdbKey)) _services.Secrets.Delete(AppSettings.TmdbSecretKey);
        else _services.Secrets.Set(AppSettings.TmdbSecretKey, TmdbKey.Trim());

        _services.Save();
    }
}
