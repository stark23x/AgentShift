// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Comparison;
using AgentShift.Core.Export;
using AgentShift.Core.Models;
using AgentShift.Core.Servers;
using AgentShift.Core.Settings;
using AgentShift.Core.Tmdb;
using Xunit;

namespace AgentShift.Core.Tests;

public class HelperTests
{
    [Theory]
    [InlineData("The Pilot (1)", "pilot part 1")]
    [InlineData("Pilot, Part One", "pilot part 1")]
    [InlineData("Café & Crème", "cafe and creme")]
    [InlineData("  What?!  ", "what")]
    public void Normalizes_titles(string input, string expected) =>
        Assert.Equal(expected, TitleMatcher.Normalize(input));

    [Theory]
    [InlineData("Episode 5")]
    [InlineData("Episode #1.5")]
    [InlineData("TBA")]
    [InlineData("")]
    public void Recognizes_placeholder_titles(string t) => Assert.True(TitleMatcher.IsGeneric(TitleMatcher.Normalize(t)));

    [Fact]
    public void Similar_titles_score_high_and_different_ones_low()
    {
        Assert.True(TitleMatcher.Similarity("The One Where Ross Finds Out", "The One Where Ross Finds Out!") >= 0.99);
        Assert.True(TitleMatcher.Similarity("Midnight Train", "Zebra Crossing") < 0.3);
        Assert.Null(TitleMatcher.Similarity("Episode 3", "Something"));
    }

    [Fact]
    public void Contains_title_handles_segment_pairs()
    {
        Assert.True(TitleMatcher.ContainsTitle("Daniel's Birthday / Daniel's Surprise", "Daniel's Surprise"));
        Assert.False(TitleMatcher.ContainsTitle("Daniel's Birthday", "Daniel's Birthday"));
    }

    [Theory]
    [InlineData("aired", OrderingKind.Tvdb, "TheTVDB (Aired)")]
    [InlineData("dvd", OrderingKind.Tvdb, "TheTVDB (DVD)")]
    [InlineData("absolute", OrderingKind.Tvdb, "TheTVDB (Absolute)")]
    [InlineData("tmdbAiring", OrderingKind.Tmdb, "The Movie Database (Aired)")]
    [InlineData("tvdbStreaming", OrderingKind.Tvdb, "TheTVDB (Streaming)")]
    [InlineData("tvdbAiring", OrderingKind.Tvdb, "TheTVDB (Aired)")]
    [InlineData("tvdbAmazon", OrderingKind.Tvdb, "TheTVDB (Amazon)")]
    public void Describes_plex_orderings(string value, OrderingKind kind, string label)
    {
        var (k, l) = PlexConnector.DescribeOrdering(value, OrderingKind.Unknown, "x");
        Assert.Equal(kind, k);
        Assert.Equal(label, l);
    }

    [Fact]
    public void Empty_plex_ordering_means_library_default()
    {
        var (k, l) = PlexConnector.DescribeOrdering("", OrderingKind.Tvdb, "Library default");
        Assert.Equal(OrderingKind.Tvdb, k);
        Assert.Equal("Library default", l);
    }

    [Fact]
    public void Reads_plex_guids_old_and_new()
    {
        int? tmdb = null, tvdb = null;
        string? imdb = null;
        PlexConnector.ReadGuid("tmdb://1399", ref tmdb, ref tvdb, ref imdb);
        PlexConnector.ReadGuid("tvdb://121361", ref tmdb, ref tvdb, ref imdb);
        PlexConnector.ReadGuid("imdb://tt0944947", ref tmdb, ref tvdb, ref imdb);
        Assert.Equal(1399, tmdb);
        Assert.Equal(121361, tvdb);
        Assert.Equal("tt0944947", imdb);

        int? t2 = null, v2 = null;
        PlexConnector.ReadLegacyGuid("com.plexapp.agents.thetvdb://121361?lang=en", ref t2, ref v2);
        Assert.Equal(121361, v2);
        int? t3 = null, v3 = null;
        PlexConnector.ReadLegacyGuid("com.plexapp.agents.hama://tvdb-81797?lang=en", ref t3, ref v3);
        Assert.Equal(81797, v3);
    }

    [Theory]
    [InlineData("TheTVDB", OrderingKind.Tvdb)]
    [InlineData("TheMovieDb", OrderingKind.Tmdb)]
    [InlineData("TheTVDB v4", OrderingKind.Tvdb)]
    [InlineData("OMDb", OrderingKind.Other)]
    [InlineData(null, OrderingKind.Unknown)]
    public void Maps_emby_and_jellyfin_fetchers(string? fetcher, OrderingKind kind) =>
        Assert.Equal(kind, EmbyFamilyConnector.KindFromFetcher(fetcher));

    [Theory]
    [InlineData("192.168.1.201", 32400, false, "", "http://192.168.1.201:32400/")]
    [InlineData("myserver.local", 8096, true, "/jellyfin", "https://myserver.local:8096/jellyfin/")]
    [InlineData("http://10.0.0.5:8920", 8096, false, "", "http://10.0.0.5:8920/")]
    [InlineData("::1", 8096, false, "", "http://[::1]:8096/")]
    public void Builds_server_urls(string host, int port, bool https, string basePath, string expected)
    {
        var p = new ServerProfile { Host = host, Port = port, UseHttps = https, BasePath = basePath };
        Assert.Equal(expected, p.BuildBaseUri().ToString());
    }

    [Fact]
    public void Csv_escapes_quotes_commas_and_formulas()
    {
        Assert.Equal("\"a, b\"", CsvExporter.Escape("a, b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
        Assert.Equal("'=SUM(A1)", CsvExporter.Escape("=SUM(A1)"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIxMjM0NTY3ODkwIiwic3ViIjoiYWJjIn0.c2lnbmF0dXJlc2lnbmF0dXJl", true)]
    public void Tells_tmdb_keys_from_tokens(string value, bool isToken) =>
        Assert.Equal(isToken, TmdbClient.IsReadAccessToken(value));

    [Fact]
    public void File_secret_store_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentshift-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSecretStore(dir);
            store.Set("k", "v");
            Assert.Equal("v", new FileSecretStore(dir).Get("k"));
            store.Delete("k");
            Assert.Null(store.Get("k"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Settings_round_trip_without_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentshift-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = new AppSettings();
            s.Profiles.Add(new ServerProfile { Name = "Home", Host = "192.168.1.201", Type = ServerType.Jellyfin });
            store.Save(s);
            var loaded = store.Load();
            Assert.Equal("Home", loaded.Profiles[0].Name);
            Assert.Equal(ServerType.Jellyfin, loaded.Profiles[0].Type);
            Assert.DoesNotContain("token", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
