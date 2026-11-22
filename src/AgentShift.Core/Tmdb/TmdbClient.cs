// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentShift.Core.Servers;

namespace AgentShift.Core.Tmdb;

public sealed record TmdbEpisode(int Id, int Season, int Episode, string Name, DateOnly? AirDate)
{
    /// <summary>TVDB episode id linked on TMDB. Only filled in when looked up (it costs one request per episode).</summary>
    public int? TvdbId { get; set; }
    public string Code => $"S{Season:00}E{Episode:00}";
}

public sealed record TmdbEpisodeGroupInfo(string Id, string Name, int Type, int EpisodeCount, int GroupCount)
{
    public string TypeName => Type switch
    {
        1 => "Original air date",
        2 => "Absolute",
        3 => "DVD",
        4 => "Digital",
        5 => "Story arc",
        6 => "Production",
        7 => "TV",
        _ => "Other",
    };
}

/// <summary>An episode group's layout: TMDB episode id → (season, episode) it would get in that group.</summary>
public sealed record TmdbEpisodeGroup(TmdbEpisodeGroupInfo Info, IReadOnlyDictionary<int, (int Season, int Episode)> Positions);

public sealed class TmdbShow
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public int? Year { get; init; }
    public int? TvdbId { get; init; }
    public required IReadOnlyList<int> SeasonNumbers { get; init; }
    public required IReadOnlyList<TmdbEpisodeGroupInfo> EpisodeGroups { get; init; }
    public List<TmdbEpisode> Episodes { get; } = new();
}

/// <summary>Talks to the TMDB v3 API. Accepts either a v3 API key or a v4 read access token.</summary>
public sealed class TmdbClient : IDisposable
{
    private const string Label = "TMDB";
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _language;
    private readonly SemaphoreSlim _gate = new(8);
    private readonly ConcurrentDictionary<string, Task<int?>> _findCache = new();

    public TmdbClient(string keyOrToken, string language = "en-US", HttpMessageHandler? handler = null)
    {
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
        _http = handler is null
            ? HttpFactory.Create(new Uri("https://api.themoviedb.org/3/"), false, TimeSpan.FromSeconds(30))
            : new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };

        keyOrToken = keyOrToken.Trim();
        if (IsReadAccessToken(keyOrToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyOrToken);
        else
            _apiKey = keyOrToken;
    }

    /// <summary>v4 read access tokens are JWTs (three dot-separated parts); v3 keys are 32 hex characters.</summary>
    internal static bool IsReadAccessToken(string s) => s.Length > 40 && s.Count(c => c == '.') == 2;

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        using var _ = await Get("configuration", ct);
    }

    public Task<int?> FindShowByTvdbIdAsync(int tvdbId, CancellationToken ct = default) =>
        _findCache.GetOrAdd($"tvdb:{tvdbId}", _ => Find(tvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), "tvdb_id", ct));

    public Task<int?> FindShowByImdbIdAsync(string imdbId, CancellationToken ct = default) =>
        _findCache.GetOrAdd($"imdb:{imdbId}", _ => Find(imdbId, "imdb_id", ct));

    private async Task<int?> Find(string id, string source, CancellationToken ct)
    {
        using var doc = await Get($"find/{Uri.EscapeDataString(id)}?external_source={source}", ct);
        foreach (var r in doc.RootElement.Arr("tv_results"))
            if (r.Int("id") is { } tmdb) return tmdb;
        return null;
    }

    /// <summary>Loads a show with every episode of every season (one request per season).</summary>
    public async Task<TmdbShow?> GetShowWithEpisodesAsync(int tmdbId, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await Get($"tv/{tmdbId}?append_to_response=external_ids,episode_groups", ct);
        }
        catch (ServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        TmdbShow show;
        using (doc)
        {
            var root = doc.RootElement;
            var seasons = root.Arr("seasons").Select(s => s.Int("season_number")).OfType<int>().Distinct().OrderBy(n => n).ToList();
            var groups = new List<TmdbEpisodeGroupInfo>();
            var eg = root.Prop("episode_groups");
            if (eg is { } egv)
                foreach (var g in egv.Arr("results"))
                    if (g.Str("id") is { } gid)
                        groups.Add(new TmdbEpisodeGroupInfo(gid, g.Str("name") ?? "", g.Int("type") ?? 0,
                            g.Int("episode_count") ?? 0, g.Int("group_count") ?? 0));

            var first = root.Date("first_air_date");
            show = new TmdbShow
            {
                Id = tmdbId,
                Name = root.Str("name") ?? "",
                Year = first?.Year,
                TvdbId = root.Prop("external_ids")?.Int("tvdb_id"),
                SeasonNumbers = seasons,
                EpisodeGroups = groups,
            };
        }

        var seasonTasks = show.SeasonNumbers.Select(n => GetSeason(tmdbId, n, ct)).ToList();
        foreach (var eps in await Task.WhenAll(seasonTasks))
            show.Episodes.AddRange(eps);
        show.Episodes.Sort((a, b) => a.Season != b.Season ? a.Season.CompareTo(b.Season) : a.Episode.CompareTo(b.Episode));
        return show;
    }

    private async Task<List<TmdbEpisode>> GetSeason(int tmdbId, int season, CancellationToken ct)
    {
        try
        {
            using var doc = await Get($"tv/{tmdbId}/season/{season}", ct);
            var list = new List<TmdbEpisode>();
            foreach (var e in doc.RootElement.Arr("episodes"))
            {
                if (e.Int("id") is not { } id || e.Int("episode_number") is not { } num) continue;
                list.Add(new TmdbEpisode(id, e.Int("season_number") ?? season, num, e.Str("name") ?? "", e.Date("air_date")));
            }
            return list;
        }
        catch (ServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new List<TmdbEpisode>();
        }
    }

    /// <summary>Fills in <see cref="TmdbEpisode.TvdbId"/> for the given episodes.</summary>
    public async Task LoadEpisodeTvdbIdsAsync(int tmdbId, IEnumerable<TmdbEpisode> episodes, CancellationToken ct = default)
    {
        await Task.WhenAll(episodes.Where(e => e.TvdbId is null).Select(async e =>
        {
            try
            {
                using var doc = await Get($"tv/{tmdbId}/season/{e.Season}/episode/{e.Episode}/external_ids", ct);
                e.TvdbId = doc.RootElement.Int("tvdb_id");
            }
            catch (ServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        }));
    }

    public async Task<TmdbEpisodeGroup?> GetEpisodeGroupAsync(TmdbEpisodeGroupInfo info, CancellationToken ct = default)
    {
        try
        {
            using var doc = await Get($"tv/episode_group/{Uri.EscapeDataString(info.Id)}", ct);
            var positions = new Dictionary<int, (int, int)>();
            var groups = doc.RootElement.Arr("groups").ToList();
            static bool IsSpecials(JsonElement g) =>
                (g.Str("name") ?? "").Contains("special", StringComparison.OrdinalIgnoreCase);
            // Groups are numbered by "order". Some start the first real season at 0, others keep 0 for specials.
            var offset = groups.Any(g => (g.Int("order") ?? 0) == 0 && !IsSpecials(g)) ? 1 : 0;
            foreach (var g in groups)
            {
                var season = IsSpecials(g) ? 0 : (g.Int("order") ?? 0) + offset;
                foreach (var e in g.Arr("episodes"))
                    if (e.Int("id") is { } id && e.Int("order") is { } order)
                        positions.TryAdd(id, (season, order + 1));
            }
            return new TmdbEpisodeGroup(info, positions);
        }
        catch (ServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        var url = path + (path.Contains('?') ? "&" : "?") + "language=" + Uri.EscapeDataString(_language);
        if (_apiKey != null) url += "&api_key=" + Uri.EscapeDataString(_apiKey);

        for (var attempt = 0; ; attempt++)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return await HttpFactory.GetJsonAsync(_http, url, Label, ct);
            }
            catch (ServerException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < 6)
            {
                // Back off and retry when TMDB says we're going too fast.
            }
            catch (ServerException ex) when (ex.StatusCode is null && attempt < 2)
            {
                // Network blip: try again.
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
