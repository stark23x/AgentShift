// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using AgentShift.Core.Models;
using AgentShift.Core.Scanning;
using AgentShift.Core.Comparison;
using AgentShift.Core.Servers;
using AgentShift.Core.Settings;
using AgentShift.Core.Tmdb;
using Xunit;

namespace AgentShift.Core.Tests;

/// <summary>Answers requests from a table of path prefix → JSON, and records what was asked.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(string Prefix, string Json)> _routes = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHandler On(string pathAndQueryPrefix, string json)
    {
        _routes.Add((pathAndQueryPrefix, json));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var pq = request.RequestUri!.PathAndQuery;
        foreach (var (prefix, json) in _routes.OrderByDescending(r => r.Prefix.Length))
            if (pq.StartsWith(prefix, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}

public class ConnectorTests
{
    private static readonly ServerProfile PlexProfile = new() { Type = ServerType.Plex, Host = "192.168.1.201", Port = 32400 };

    private const string PlexSections = """
        {"MediaContainer":{"Directory":[
          {"key":"2","type":"show","title":"TV Shows","agent":"tv.plex.agents.series"},
          {"key":"3","type":"movie","title":"Movies","agent":"tv.plex.agents.movie"},
          {"key":"4","type":"show","title":"Old TV","agent":"com.plexapp.agents.thetvdb"}]}}
        """;

    [Fact]
    public async Task Plex_lists_tv_libraries_with_their_ordering()
    {
        var h = new FakeHandler()
            .On("/library/sections/2/prefs", """{"MediaContainer":{"Setting":[{"id":"showOrdering","value":"aired","default":"tmdbAiring"}]}}""")
            .On("/library/sections", PlexSections);
        using var plex = new PlexConnector(PlexProfile, "tok", "cid", h);

        var libs = await plex.GetTvLibrariesAsync();

        Assert.Equal(2, libs.Count);
        Assert.Equal(OrderingKind.Tvdb, libs[0].DefaultOrderingKind);
        Assert.Equal("TheTVDB (Aired)", libs[0].DefaultOrderingLabel);
        Assert.Equal(OrderingKind.Tvdb, libs[1].DefaultOrderingKind);
        Assert.Contains(h.Requests, r => r.Headers.GetValues("X-Plex-Token").Single() == "tok");
    }

    [Fact]
    public async Task Plex_reads_shows_show_ordering_and_episodes()
    {
        var h = new FakeHandler()
            .On("/library/sections/2/all", """
                {"MediaContainer":{"Metadata":[
                  {"ratingKey":"100","title":"Lost","year":2004,"guid":"plex://show/abc","showOrdering":"tvdbDvd",
                   "Guid":[{"id":"imdb://tt0411008"},{"id":"tmdb://4607"},{"id":"tvdb://73739"}]}]}}
                """)
            .On("/library/metadata/100/allLeaves", """
                {"MediaContainer":{"Metadata":[
                  {"ratingKey":"201","parentIndex":1,"index":1,"title":"Pilot (1)","originallyAvailableAt":"2004-09-22",
                   "Guid":[{"id":"tmdb://330678"},{"id":"tvdb://127131"}],
                   "Media":[{"Part":[{"file":"/tv/Lost/S01E01-E02.mkv"}]}]},
                  {"ratingKey":"202","parentIndex":1,"index":2,"title":"Pilot (2)","originallyAvailableAt":"2004-09-22",
                   "Media":[{"Part":[{"file":"/tv/Lost/S01E01-E02.mkv"}]}]},
                  {"ratingKey":"203","parentIndex":null,"index":5,"title":"No season"}]}}
                """);
        using var plex = new PlexConnector(PlexProfile, "tok", "cid", h);
        var lib = new MediaLibrary("2", "TV Shows", "Plex Series", OrderingKind.Tmdb, "The Movie Database (Aired)");

        var shows = await plex.GetShowsAsync(lib);
        var show = Assert.Single(shows);
        Assert.Equal(4607, show.TmdbId);
        Assert.Equal(73739, show.TvdbId);
        Assert.Equal(OrderingKind.Tvdb, show.OrderingKind);
        Assert.Equal("TheTVDB (DVD)", show.OrderingLabel);

        var eps = await plex.GetEpisodesAsync(show);
        Assert.Equal(2, eps.Count);
        Assert.Equal(330678, eps[0].TmdbEpisodeId);
        Assert.Equal(new DateOnly(2004, 9, 22), eps[0].AirDate);
        Assert.True(eps[0].IsPartOfMultiEpisodeFile);
    }

    [Fact]
    public async Task Jellyfin_reads_libraries_shows_and_expands_multi_episode_files()
    {
        var profile = new ServerProfile { Type = ServerType.Jellyfin, Host = "jf", Port = 8096, UserId = "u1" };
        var h = new FakeHandler()
            .On("/Library/VirtualFolders", """
                [{"Name":"Shows","ItemId":"lib1","CollectionType":"tvshows",
                  "LibraryOptions":{"TypeOptions":[{"Type":"Series","MetadataFetchers":["TheTVDB","TheMovieDb"],
                                                    "MetadataFetcherOrder":["TheTVDB","TheMovieDb"]}]}},
                 {"Name":"Films","ItemId":"lib2","CollectionType":"movies"}]
                """)
            .On("/Items?ParentId=lib1", """
                {"Items":[{"Id":"s1","Name":"Bluey","ProductionYear":2018,"ProviderIds":{"Tvdb":"353546","Tmdb":"82728"}}]}
                """)
            .On("/Shows/s1/Episodes", """
                {"Items":[
                  {"Id":"e1","Name":"Magic Xylophone / Hospital","ParentIndexNumber":1,"IndexNumber":1,"IndexNumberEnd":2,
                   "PremiereDate":"2018-10-01T00:00:00.0000000Z","Path":"/tv/Bluey/S01E01-E02.mkv"},
                  {"Id":"e3","Name":"Missing one","ParentIndexNumber":1,"IndexNumber":3,"LocationType":"Virtual"}]}
                """);
        using var jf = new EmbyFamilyConnector(profile, "key", "cid", h);

        var libs = await jf.GetTvLibrariesAsync();
        var lib = Assert.Single(libs);
        Assert.Equal(OrderingKind.Tvdb, lib.DefaultOrderingKind);

        var show = Assert.Single(await jf.GetShowsAsync(lib));
        Assert.Equal(82728, show.TmdbId);

        var eps = await jf.GetEpisodesAsync(show);
        Assert.Equal(2, eps.Count);
        Assert.Equal(new[] { 1, 2 }, eps.Select(e => e.Episode));
        Assert.All(eps, e => Assert.True(e.IsPartOfMultiEpisodeFile));
        Assert.All(h.Requests, r => Assert.Contains("Token=\"key\"", r.Headers.GetValues("Authorization").Single()));
        Assert.Contains(h.Requests, r => r.RequestUri!.Query.Contains("UserId=u1"));
    }

    [Fact]
    public async Task Full_scan_end_to_end_with_fake_servers()
    {
        var plex = new FakeHandler()
            .On("/library/sections/2/all", """
                {"MediaContainer":{"Metadata":[{"ratingKey":"100","title":"Split Show","year":2020,"Guid":[{"id":"tvdb://555"}]}]}}
                """)
            .On("/library/metadata/100/allLeaves", """
                {"MediaContainer":{"Metadata":[
                  {"ratingKey":"1","parentIndex":1,"index":1,"title":"Arrival","originallyAvailableAt":"2020-01-01"},
                  {"ratingKey":"2","parentIndex":2,"index":1,"title":"Departure","originallyAvailableAt":"2020-01-08"}]}}
                """);
        var tmdb = new FakeHandler()
            .On("/3/find/555", """{"tv_results":[{"id":9000}]}""")
            .On("/3/tv/9000/season/1", """
                {"episodes":[{"id":1,"season_number":1,"episode_number":1,"name":"Arrival","air_date":"2020-01-01"},
                             {"id":2,"season_number":1,"episode_number":2,"name":"Departure","air_date":"2020-01-08"}]}
                """)
            .On("/3/tv/9000?", """{"id":9000,"name":"Split Show","first_air_date":"2020-01-01","seasons":[{"season_number":1}],"episode_groups":{"results":[]}}""");

        using var server = new PlexConnector(PlexProfile, "tok", "cid", plex);
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", tmdb);
        var scanner = new LibraryScanner(server, client, new ScanOptions());
        var lib = new MediaLibrary("2", "TV Shows", "Plex Series", OrderingKind.Tvdb, "TheTVDB (Aired)");

        var results = new List<ShowResult>();
        await scanner.ScanAsync(new[] { lib }, r => { lock (results) results.Add(r); }, null, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal(ShowStatus.Changes, r.Status);
        Assert.Equal("S01E02", r.Episodes[1].NewCode);
        Assert.Contains(tmdb.Requests, q => q.RequestUri!.Query.Contains("api_key=0123456789abcdef"));
    }

    [Fact]
    public async Task Shows_already_on_tmdb_are_skipped_by_default()
    {
        using var server = new PlexConnector(PlexProfile, "tok", "cid", new FakeHandler());
        using var client = new TmdbClient("k", "en-US", new FakeHandler());
        var scanner = new LibraryScanner(server, client, new ScanOptions());
        var show = new ShowInfo { Id = "1", Title = "X", LibraryId = "2", LibraryName = "TV", OrderingKind = OrderingKind.Tmdb };

        var r = await scanner.ScanShowAsync(show, CancellationToken.None);

        Assert.Equal(ShowStatus.AlreadyTmdb, r.Status);
    }
}
