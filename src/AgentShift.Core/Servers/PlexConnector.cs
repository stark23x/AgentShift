// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using AgentShift.Core.Models;
using AgentShift.Core.Settings;

namespace AgentShift.Core.Servers;

/// <summary>Reads TV libraries from Plex Media Server over its HTTP API.</summary>
public sealed partial class PlexConnector : IMediaServerConnector
{
    private const string Label = "the Plex server";
    private readonly HttpClient _http;

    public ServerType Type => ServerType.Plex;

    public PlexConnector(ServerProfile profile, string token, string clientId, HttpMessageHandler? testHandler = null)
    {
        _http = HttpFactory.Create(profile.BuildBaseUri(), profile.AllowInvalidCertificate, null, testHandler);
        _http.DefaultRequestHeaders.Add("X-Plex-Token", token);
        _http.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", clientId);
        _http.DefaultRequestHeaders.Add("X-Plex-Product", AppInfo.Name);
        _http.DefaultRequestHeaders.Add("X-Plex-Version", AppInfo.Version);
    }

    public async Task<ServerIdentity> TestAsync(CancellationToken ct = default)
    {
        using var doc = await Get("", ct);
        var mc = Container(doc);
        return new ServerIdentity(mc.Str("friendlyName") ?? "Plex Media Server", mc.Str("version") ?? "");
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetTvLibrariesAsync(CancellationToken ct = default)
    {
        using var doc = await Get("library/sections", ct);
        var list = new List<MediaLibrary>();
        foreach (var d in Container(doc).Arr("Directory"))
        {
            if (d.Str("type") != "show") continue;
            var key = d.Str("key")!;
            var agent = d.Str("agent") ?? "";
            var (kind, label) = (OrderingKind.Unknown, (string?)null);

            if (IsLegacyTvdb(agent)) (kind, label) = (OrderingKind.Tvdb, "TheTVDB (legacy agent)");
            else if (IsLegacyTmdb(agent)) (kind, label) = (OrderingKind.Tmdb, "The Movie Database (legacy agent)");
            else if (agent == "tv.plex.agents.series")
            {
                var value = await TryGetLibraryOrdering(key, ct);
                (kind, label) = DescribeOrdering(value, OrderingKind.Tmdb, "The Movie Database (Aired)");
            }
            else (kind, label) = (OrderingKind.Other, AgentName(agent));

            list.Add(new MediaLibrary(key, d.Str("title") ?? key, AgentName(agent), kind, label));
        }
        return list;
    }

    public async Task<IReadOnlyList<ShowInfo>> GetShowsAsync(MediaLibrary library, CancellationToken ct = default)
    {
        using var doc = await Get($"library/sections/{library.Id}/all?type=2&includeGuids=1", ct);
        var shows = new List<ShowInfo>();
        foreach (var m in Container(doc).Arr("Metadata"))
        {
            var guid = m.Str("guid") ?? "";
            int? tmdb = null, tvdb = null;
            string? imdb = null;
            foreach (var g in m.Arr("Guid"))
                ReadGuid(g.Str("id"), ref tmdb, ref tvdb, ref imdb);
            ReadLegacyGuid(guid, ref tmdb, ref tvdb);

            var show = new ShowInfo
            {
                Id = m.Str("ratingKey")!,
                Title = m.Str("title") ?? "(untitled)",
                Year = m.Int("year"),
                LibraryId = library.Id,
                LibraryName = library.Name,
                TmdbId = tmdb,
                TvdbId = tvdb,
                ImdbId = imdb,
                OrderingKind = library.DefaultOrderingKind,
                OrderingLabel = library.DefaultOrderingLabel ?? "Library default",
            };

            // A show-level "showOrdering" attribute (present in the list when set) overrides the library default.
            var own = m.Str("showOrdering");
            if (!string.IsNullOrEmpty(own))
                (show.OrderingKind, show.OrderingLabel) = DescribeOrdering(own, library.DefaultOrderingKind, show.OrderingLabel);
            shows.Add(show);
        }

        return shows;
    }

    public async Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(ShowInfo show, CancellationToken ct = default)
    {
        using var doc = await Get($"library/metadata/{show.Id}/allLeaves?includeGuids=1", ct);
        var eps = new List<EpisodeInfo>();
        var fileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var raw = new List<(JsonElement M, string? File)>();

        foreach (var m in Container(doc).Arr("Metadata"))
        {
            string? file = null;
            foreach (var media in m.Arr("Media"))
            {
                foreach (var part in media.Arr("Part")) { file = part.Str("file"); break; }
                if (file != null) break;
            }
            if (file != null) fileCounts[file] = fileCounts.GetValueOrDefault(file) + 1;
            raw.Add((m, file));
        }

        foreach (var (m, file) in raw)
        {
            int? tmdb = null, tvdb = null;
            string? imdb = null;
            foreach (var g in m.Arr("Guid")) ReadGuid(g.Str("id"), ref tmdb, ref tvdb, ref imdb);

            var season = m.Int("parentIndex");
            var number = m.Int("index");
            if (season is null || number is null) continue; // unnumbered items (date-based or broken) can't be compared

            eps.Add(new EpisodeInfo
            {
                Id = m.Str("ratingKey")!,
                Season = season.Value,
                Episode = number.Value,
                Title = m.Str("title") ?? "",
                AirDate = m.Date("originallyAvailableAt"),
                TmdbEpisodeId = tmdb,
                TvdbEpisodeId = tvdb,
                FilePath = file,
                IsPartOfMultiEpisodeFile = file != null && fileCounts[file] > 1,
            });
        }
        return eps.OrderBy(e => e.Season).ThenBy(e => e.Episode).ToList();
    }

    // ---- helpers -------------------------------------------------------------------------

    private async Task<string?> TryGetLibraryOrdering(string sectionKey, CancellationToken ct)
    {
        try
        {
            using var doc = await Get($"library/sections/{sectionKey}/prefs", ct);
            foreach (var s in Container(doc).Arr("Setting"))
                if (s.Str("id") == "showOrdering") return s.Str("value") is { Length: > 0 } v ? v : s.Str("default");
        }
        catch (ServerException) { /* older servers: fall back to the agent default */ }
        return null;
    }

    /// <summary>Turns Plex's showOrdering value into a kind and label. Empty means "library default".</summary>
    internal static (OrderingKind Kind, string Label) DescribeOrdering(string? value, OrderingKind fallbackKind, string fallbackLabel)
    {
        if (string.IsNullOrWhiteSpace(value)) return (fallbackKind, fallbackLabel);
        return value switch
        {
            "tmdbAiring" => (OrderingKind.Tmdb, "The Movie Database (Aired)"),
            "aired" or "tvdbAiring" => (OrderingKind.Tvdb, "TheTVDB (Aired)"),
            "tvdbDvd" => (OrderingKind.Tvdb, "TheTVDB (DVD)"),
            "tvdbAbsolute" => (OrderingKind.Tvdb, "TheTVDB (Absolute)"),
            "dvd" => (OrderingKind.Tvdb, "TheTVDB (DVD)"),
            "absolute" => (OrderingKind.Tvdb, "TheTVDB (Absolute)"),
            _ when value.StartsWith("tvdb", StringComparison.OrdinalIgnoreCase) => (OrderingKind.Tvdb, "TheTVDB (" + Pretty(value[4..]) + ")"),
            _ when value.StartsWith("tmdb", StringComparison.OrdinalIgnoreCase) => (OrderingKind.Tmdb, "The Movie Database (" + Pretty(value[4..]) + ")"),
            _ => (OrderingKind.Unknown, value),
        };
    }

    private static string Pretty(string s) => s.Length == 0 ? "Default" : char.ToUpperInvariant(s[0]) + s[1..];

    internal static void ReadGuid(string? id, ref int? tmdb, ref int? tvdb, ref string? imdb)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (id.StartsWith("tmdb://", StringComparison.Ordinal)) tmdb ??= Json.ParseInt(id[7..]);
        else if (id.StartsWith("tvdb://", StringComparison.Ordinal)) tvdb ??= Json.ParseInt(id[7..]);
        else if (id.StartsWith("imdb://", StringComparison.Ordinal)) imdb ??= id[7..];
    }

    internal static void ReadLegacyGuid(string guid, ref int? tmdb, ref int? tvdb)
    {
        var m = LegacyGuid().Match(guid);
        if (!m.Success) return;
        var id = Json.ParseInt(m.Groups["id"].Value);
        switch (m.Groups["agent"].Value)
        {
            case "thetvdb": tvdb ??= id; break;
            case "themoviedb": tmdb ??= id; break;
            case "hama" when m.Groups["src"].Value == "tvdb": tvdb ??= id; break;
        }
    }

    [GeneratedRegex(@"^com\.plexapp\.agents\.(?<agent>thetvdb|themoviedb|hama)://(?:(?<src>[a-z]+)\d*-)?(?<id>\d+)")]
    private static partial Regex LegacyGuid();

    private static bool IsLegacyTvdb(string agent) => agent == "com.plexapp.agents.thetvdb";
    private static bool IsLegacyTmdb(string agent) => agent == "com.plexapp.agents.themoviedb";

    private static string AgentName(string agent) => agent switch
    {
        "tv.plex.agents.series" => "Plex Series",
        "com.plexapp.agents.thetvdb" => "TheTVDB (legacy)",
        "com.plexapp.agents.themoviedb" => "The Movie Database (legacy)",
        "com.plexapp.agents.hama" => "HAMA",
        "tv.plex.agents.none" => "Personal Media",
        "" => "Unknown",
        _ => agent,
    };

    private Task<JsonDocument> Get(string url, CancellationToken ct) => HttpFactory.GetJsonAsync(_http, url, Label, ct);

    private static JsonElement Container(JsonDocument doc) =>
        doc.RootElement.Prop("MediaContainer") ?? throw new ServerException("The Plex server sent an unexpected reply.");

    public void Dispose() => _http.Dispose();
}
