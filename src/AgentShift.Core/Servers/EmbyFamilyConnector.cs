// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentShift.Core.Models;
using AgentShift.Core.Settings;

namespace AgentShift.Core.Servers;

/// <summary>
/// Reads TV libraries from Emby or Jellyfin. Jellyfin began as a fork of Emby, so their APIs are close enough
/// to share one connector; the differences are handled inline.
/// </summary>
public sealed class EmbyFamilyConnector : IMediaServerConnector
{
    private readonly HttpClient _http;
    private readonly ServerProfile _profile;
    private readonly string _label;
    private string? _userId;

    public ServerType Type => _profile.Type;

    public EmbyFamilyConnector(ServerProfile profile, string token, string clientId, HttpMessageHandler? testHandler = null)
    {
        _profile = profile;
        _label = profile.Type == ServerType.Jellyfin ? "the Jellyfin server" : "the Emby server";
        _userId = profile.UserId;
        _http = HttpFactory.Create(profile.BuildBaseUri(), profile.AllowInvalidCertificate, null, testHandler);
        ApplyAuthHeaders(_http, clientId, token);
    }

    internal static void ApplyAuthHeaders(HttpClient http, string clientId, string? token)
    {
        var auth = $"MediaBrowser Client=\"{AppInfo.Name}\", Device=\"{Environment.MachineName}\", " +
                   $"DeviceId=\"{clientId}\", Version=\"{AppInfo.Version}\"" +
                   (string.IsNullOrEmpty(token) ? "" : $", Token=\"{token}\"");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);   // Jellyfin
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization", auth); // Emby
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", token);
    }

    /// <summary>Signs in with a username and password and returns an access token. The password is not kept.</summary>
    public static async Task<(string Token, string UserId, string UserName)> SignInAsync(
        ServerProfile profile, string clientId, string username, string password, CancellationToken ct = default)
    {
        using var http = HttpFactory.Create(profile.BuildBaseUri(), profile.AllowInvalidCertificate);
        ApplyAuthHeaders(http, clientId, null);
        var label = profile.Type == ServerType.Jellyfin ? "the Jellyfin server" : "the Emby server";
        using var req = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = username, Pw = password }),
        };
        req.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var doc = await HttpFactory.SendJsonAsync(http, req, label, ct);
            var token = doc.RootElement.Str("AccessToken") ?? throw new ServerException($"{label} didn't return a token.");
            var user = doc.RootElement.Prop("User");
            return (token, user?.Str("Id") ?? "", user?.Str("Name") ?? username);
        }
        catch (ServerException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ServerException("Wrong username or password.", ex.StatusCode, ex);
        }
    }

    public async Task<ServerIdentity> TestAsync(CancellationToken ct = default)
    {
        using var doc = await Get("System/Info", ct);
        var name = doc.RootElement.Str("ServerName") ?? (Type == ServerType.Jellyfin ? "Jellyfin" : "Emby");
        var product = doc.RootElement.Str("ProductName");
        if (Type == ServerType.Jellyfin && product != null && product.Contains("Emby", StringComparison.OrdinalIgnoreCase))
            throw new ServerException("This looks like an Emby server. Change the server type to Emby.");
        return new ServerIdentity(name, doc.RootElement.Str("Version") ?? "");
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetTvLibrariesAsync(CancellationToken ct = default)
    {
        using var doc = await Get("Library/VirtualFolders", ct);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.Arr("Items").ToList();

        var list = new List<MediaLibrary>();
        foreach (var f in items)
        {
            if (!string.Equals(f.Str("CollectionType"), "tvshows", StringComparison.OrdinalIgnoreCase)) continue;
            var id = f.Str("ItemId") ?? f.Str("Id");
            if (id is null) continue;
            var fetcher = PrimarySeriesFetcher(f);
            var kind = KindFromFetcher(fetcher);
            list.Add(new MediaLibrary(id, f.Str("Name") ?? id, fetcher ?? "Unknown", kind,
                fetcher is null ? "Unknown metadata source" : $"{fetcher} (first metadata source)"));
        }
        return list;
    }

    public async Task<IReadOnlyList<ShowInfo>> GetShowsAsync(MediaLibrary library, CancellationToken ct = default)
    {
        var query = $"Items?ParentId={Uri.EscapeDataString(library.Id)}&IncludeItemTypes=Series&Recursive=true" +
                    "&Fields=ProviderIds,Path,ProductionYear,DisplayOrder&EnableImages=false";
        using var doc = await GetWithUser(query, ct);
        var shows = new List<ShowInfo>();
        foreach (var s in doc.RootElement.Arr("Items"))
        {
            var ids = s.Prop("ProviderIds");
            var show = new ShowInfo
            {
                Id = s.Str("Id")!,
                Title = s.Str("Name") ?? "(untitled)",
                Year = s.Int("ProductionYear"),
                LibraryId = library.Id,
                LibraryName = library.Name,
                TmdbId = ids is { } a ? Json.ParseInt(a.Str("Tmdb")) : null,
                TvdbId = ids is { } b ? Json.ParseInt(b.Str("Tvdb")) : null,
                ImdbId = ids?.Str("Imdb"),
                Path = s.Str("Path"),
                OrderingKind = library.DefaultOrderingKind,
                OrderingLabel = library.DefaultOrderingLabel ?? "Unknown",
            };
            var order = s.Str("DisplayOrder");
            if (!string.IsNullOrEmpty(order) && !order.Equals("aired", StringComparison.OrdinalIgnoreCase))
                show.OrderingLabel += $", display order: {order}";
            shows.Add(show);
        }
        return shows;
    }

    public async Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(ShowInfo show, CancellationToken ct = default)
    {
        var query = $"Shows/{Uri.EscapeDataString(show.Id)}/Episodes?Fields=ProviderIds,Path,PremiereDate&EnableImages=false";
        using var doc = await GetWithUser(query, ct);
        var eps = new List<EpisodeInfo>();
        foreach (var e in doc.RootElement.Arr("Items"))
        {
            // Placeholders for episodes you don't have ("missing episodes") aren't part of the library.
            if (string.Equals(e.Str("LocationType"), "Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var season = e.Int("ParentIndexNumber");
            var first = e.Int("IndexNumber");
            if (season is null || first is null) continue;
            var last = Math.Max(first.Value, e.Int("IndexNumberEnd") ?? first.Value);
            var ids = e.Prop("ProviderIds");
            var multi = last > first;

            // A file holding S01E01-E02 becomes two slots so each number can be compared on its own.
            for (var n = first.Value; n <= last && n - first.Value < 20; n++)
            {
                eps.Add(new EpisodeInfo
                {
                    Id = multi ? $"{e.Str("Id")}#{n}" : e.Str("Id")!,
                    Season = season.Value,
                    Episode = n,
                    Title = e.Str("Name") ?? "",
                    AirDate = e.Date("PremiereDate"),
                    TmdbEpisodeId = multi ? null : (ids is { } a ? Json.ParseInt(a.Str("Tmdb")) : null),
                    TvdbEpisodeId = multi ? null : (ids is { } b ? Json.ParseInt(b.Str("Tvdb")) : null),
                    FilePath = e.Str("Path"),
                    IsPartOfMultiEpisodeFile = multi,
                });
            }
        }
        return eps.OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }

    // ---- helpers -------------------------------------------------------------------------

    private static string? PrimarySeriesFetcher(JsonElement folder)
    {
        var opts = folder.Prop("LibraryOptions");
        if (opts is null) return null;
        foreach (var t in opts.Value.Arr("TypeOptions"))
        {
            if (!string.Equals(t.Str("Type"), "Series", StringComparison.OrdinalIgnoreCase)) continue;
            var enabled = t.Arr("MetadataFetchers").Select(x => x.GetString()).Where(x => x != null).ToList();
            var order = t.Arr("MetadataFetcherOrder").Select(x => x.GetString()).Where(x => x != null).ToList();
            var first = order.FirstOrDefault(o => enabled.Contains(o)) ?? enabled.FirstOrDefault();
            if (first != null) return first;
        }
        return null;
    }

    internal static OrderingKind KindFromFetcher(string? fetcher)
    {
        if (string.IsNullOrEmpty(fetcher)) return OrderingKind.Unknown;
        var f = fetcher.Replace(" ", "", StringComparison.Ordinal);
        if (f.Contains("TVDB", StringComparison.OrdinalIgnoreCase)) return OrderingKind.Tvdb;
        if (f.Contains("MovieDb", StringComparison.OrdinalIgnoreCase) || f.Contains("TMDB", StringComparison.OrdinalIgnoreCase))
            return OrderingKind.Tmdb;
        return OrderingKind.Other;
    }

    private Task<JsonDocument> Get(string url, CancellationToken ct) => HttpFactory.GetJsonAsync(_http, url, _label, ct);

    /// <summary>Some server versions require a user for item queries; an API key has none, so pick an administrator.</summary>
    private async Task<JsonDocument> GetWithUser(string query, CancellationToken ct)
    {
        if (_userId is null)
        {
            try { return await Get(query, ct); }
            catch (ServerException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
                                                 or HttpStatusCode.InternalServerError)
            {
                _userId = await FindAdminUserId(ct);
            }
        }
        var sep = query.Contains('?') ? '&' : '?';
        return await Get($"{query}{sep}UserId={Uri.EscapeDataString(_userId!)}", ct);
    }

    private async Task<string> FindAdminUserId(CancellationToken ct)
    {
        using var doc = await Get("Users", ct);
        var users = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.Arr("Items").ToList();
        var admin = users.FirstOrDefault(u => u.Prop("Policy")?.Str("IsAdministrator") == "true");
        var pick = admin.ValueKind == JsonValueKind.Object ? admin : users.FirstOrDefault();
        return pick.ValueKind == JsonValueKind.Object && pick.Str("Id") is { } id
            ? id
            : throw new ServerException($"{_label} has no user AgentShift can read the library as.");
    }

    public void Dispose() => _http.Dispose();
}
