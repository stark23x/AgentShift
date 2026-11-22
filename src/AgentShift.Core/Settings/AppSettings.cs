// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentShift.Core.Models;

namespace AgentShift.Core.Settings;

/// <summary>A saved server connection. Secrets (tokens, API keys) are kept in the secret store, not here.</summary>
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My server";
    public ServerType Type { get; set; } = ServerType.Plex;
    public bool UseHttps { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 32400;

    /// <summary>Optional URL prefix, e.g. "/jellyfin" when the server sits behind a reverse proxy.</summary>
    public string BasePath { get; set; } = "";

    /// <summary>Accept self-signed or mismatched HTTPS certificates (common on a LAN).</summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>Emby/Jellyfin: the user the access token belongs to, if signed in with a username.</summary>
    public string? UserId { get; set; }
    public string? UserName { get; set; }

    [JsonIgnore]
    public string SecretKey => $"server:{Id:N}";

    public static int DefaultPort(ServerType type) => type == ServerType.Plex ? 32400 : 8096;

    public Uri BuildBaseUri()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("No server address has been set for this profile.");

        var host = Host.Trim();
        // Allow people to paste a full URL into the host box.
        if (host.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(host, UriKind.Absolute, out var pasted))
        {
            var b = new UriBuilder(pasted);
            if (pasted.IsDefaultPort && !host.Contains($":{pasted.Port}", StringComparison.Ordinal)) b.Port = Port;
            b.Path = NormalizePath(string.IsNullOrEmpty(BasePath) ? pasted.AbsolutePath : BasePath);
            return b.Uri;
        }

        // UriBuilder adds the brackets for an IPv6 address itself.
        var builder = new UriBuilder(UseHttps ? "https" : "http", host.Trim('[', ']'), Port)
        {
            Path = NormalizePath(BasePath),
        };
        return builder.Uri;
    }

    private static string NormalizePath(string? p)
    {
        p = (p ?? "").Trim().Trim('/');
        return p.Length == 0 ? "/" : "/" + p + "/";
    }

    public ServerProfile Clone() => (ServerProfile)MemberwiseClone();
}

public sealed class AppSettings
{
    public List<ServerProfile> Profiles { get; set; } = new();
    public Guid? SelectedProfileId { get; set; }

    /// <summary>Language for TMDB titles, e.g. "en-US".</summary>
    public string TmdbLanguage { get; set; } = "en-US";

    /// <summary>When episodes can't be matched by TMDB link, title or date, look up TVDB links on TMDB episodes (slower).</summary>
    public bool UseEpisodeLinks { get; set; } = true;

    /// <summary>Also check shows that already follow TMDB ordering.</summary>
    public bool CompareShowsAlreadyOnTmdb { get; set; }

    /// <summary>Check TMDB episode groups for one that keeps the current numbering.</summary>
    public bool CheckEpisodeGroups { get; set; } = true;

    /// <summary>Stable id this install uses when talking to Plex / Emby / Jellyfin.</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    public const string TmdbSecretKey = "tmdb";
}

/// <summary>Loads and saves settings.json in the per-user config folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public SettingsStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.ConfigDirectory;
        FilePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A damaged settings file shouldn't stop the app from starting; keep a copy and start fresh.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { /* ignore */ }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class AppPaths
{
    public static string ConfigDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentShift");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "AgentShift");

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "AgentShift");
        }
    }
}
