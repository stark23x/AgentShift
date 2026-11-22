// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Comparison;
using AgentShift.Core.Models;
using AgentShift.Core.Tmdb;
using Xunit;

namespace AgentShift.Core.Tests;

public class ComparerTests
{
    private static readonly ShowInfo Show = new()
    {
        Id = "1", Title = "Test Show", LibraryId = "l", LibraryName = "TV",
        OrderingKind = OrderingKind.Tvdb, OrderingLabel = "TheTVDB (Aired)",
    };

    private static int _id = 1000;

    private static EpisodeInfo Cur(int s, int e, string title, string? date = null, int? tmdbId = null, string? file = null) => new()
    {
        Id = $"{s}-{e}", Season = s, Episode = e, Title = title,
        AirDate = date is null ? null : DateOnly.Parse(date), TmdbEpisodeId = tmdbId,
        FilePath = file ?? $"/tv/Test Show/S{s:00}E{e:00}.mkv",
    };

    private static TmdbEpisode Tm(int s, int e, string title, string? date = null, int? id = null) =>
        new(id ?? Interlocked.Increment(ref _id), s, e, title, date is null ? null : DateOnly.Parse(date));

    private static TmdbShow TShow(params TmdbEpisode[] eps)
    {
        var show = new TmdbShow
        {
            Id = 42, Name = "Test Show",
            SeasonNumbers = eps.Select(e => e.Season).Distinct().ToList(),
            EpisodeGroups = Array.Empty<TmdbEpisodeGroupInfo>(),
        };
        show.Episodes.AddRange(eps);
        return show;
    }

    [Fact]
    public void Identical_order_has_no_changes()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "Pilot", "2020-01-01"), Cur(1, 2, "The Second One", "2020-01-08") },
            TShow(Tm(1, 1, "Pilot", "2020-01-01"), Tm(1, 2, "The Second One", "2020-01-08")));

        Assert.Equal(ShowStatus.NoChanges, r.Status);
        Assert.All(r.Episodes, e => Assert.Equal(EpisodeChange.None, e.Change));
    }

    [Fact]
    public void Season_split_on_tvdb_becomes_renumbering()
    {
        var current = new List<EpisodeInfo>();
        var tmdb = new List<TmdbEpisode>();
        for (var i = 1; i <= 20; i++)
        {
            var date = new DateOnly(2019, 1, 1).AddDays(7 * i).ToString("yyyy-MM-dd");
            current.Add(Cur(i <= 10 ? 1 : 2, i <= 10 ? i : i - 10, $"Story number {i} begins", date));
            tmdb.Add(Tm(1, i, $"Story number {i} begins", date));
        }

        var r = ShowComparer.Compare(Show, current, TShow(tmdb.ToArray()));

        Assert.Equal(ShowStatus.Changes, r.Status);
        Assert.Equal(10, r.Episodes.Count(e => e.Change == EpisodeChange.Renumbered));
        var moved = r.Episodes.Single(e => e.Current.Code == "S02E01");
        Assert.Equal("S01E11", moved.NewCode);
        Assert.Contains(r.Notes, n => n.Contains("seasons 1–2") && n.Contains("season 1"));
    }

    [Fact]
    public void Two_segment_file_is_split_on_tmdb()
    {
        var r = ShowComparer.Compare(Show,
            new[]
            {
                Cur(1, 1, "Daniel's Birthday / Daniel's Surprise", "2012-09-03"),
                Cur(1, 2, "Daniel Goes to School / Safety Patrol", "2012-09-04"),
            },
            TShow(
                Tm(1, 1, "Daniel's Birthday", "2012-09-03"),
                Tm(1, 2, "Daniel's Surprise", "2012-09-03"),
                Tm(1, 3, "Daniel Goes to School", "2012-09-04"),
                Tm(1, 4, "Safety Patrol", "2012-09-04")));

        var first = r.Episodes[0];
        Assert.Equal(EpisodeChange.SplitOnTmdb, first.Change);
        Assert.Equal("S01E01 + S01E02", first.NewCode);
        Assert.Equal(EpisodeChange.SplitOnTmdb, r.Episodes[1].Change);
        Assert.Equal("S01E03 + S01E04", r.Episodes[1].NewCode);
    }

    [Fact]
    public void Two_part_pilot_is_combined_on_tmdb()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "Pilot (1)", "2004-09-22"), Cur(1, 2, "Pilot (2)", "2004-09-22"), Cur(1, 3, "Tabula Rasa", "2004-10-06") },
            TShow(Tm(1, 1, "Pilot", "2004-09-22"), Tm(1, 2, "Tabula Rasa", "2004-10-06")));

        Assert.Equal(EpisodeChange.MergedOnTmdb, r.Episodes[0].Change);
        Assert.Equal(EpisodeChange.MergedOnTmdb, r.Episodes[1].Change);
        Assert.Equal(EpisodeChange.Renumbered, r.Episodes[2].Change);
        Assert.Equal("S01E02", r.Episodes[2].NewCode);
    }

    [Fact]
    public void Special_that_tmdb_counts_as_regular()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(0, 1, "A Very Merry Holiday", "2015-12-20"), Cur(1, 1, "Opening Night", "2015-10-01") },
            TShow(Tm(1, 1, "Opening Night", "2015-10-01"), Tm(1, 9, "A Very Merry Holiday", "2015-12-20")));

        Assert.Equal(EpisodeChange.MovedFromSpecials, r.Episodes[0].Change);
        Assert.Equal("S01E09", r.Episodes[0].NewCode);
    }

    [Fact]
    public void Tmdb_link_beats_title_matching()
    {
        var real = Tm(2, 5, "Completely Different Name", "2001-01-01", id: 777);
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 5, "Old Name", "2001-01-01", tmdbId: 777) },
            TShow(Tm(1, 5, "Old Name"), real));

        Assert.Equal("TMDB link", r.Episodes[0].MatchMethod);
        Assert.Equal(EpisodeChange.Renumbered, r.Episodes[0].Change);
    }

    [Fact]
    public void Same_number_different_wording_is_title_only()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "The Beginning, Part One", "2010-01-01"), Cur(1, 2, "Escape from the Island", "2010-01-08") },
            TShow(Tm(1, 1, "Beginning (1)", "2010-01-01"), Tm(1, 2, "Island Escape", "2010-01-08")));

        Assert.Equal(EpisodeChange.None, r.Episodes[0].Change);
        Assert.Equal(EpisodeChange.TitleOnly, r.Episodes[1].Change);
        Assert.Equal(ShowStatus.TitlesOnly, r.Status);
    }

    [Fact]
    public void Unknown_episode_with_something_else_at_its_number_needs_review()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(3, 1, "Zebra Crossing", "2018-01-01") },
            TShow(Tm(3, 1, "Midnight Train", "2018-05-05")));

        Assert.Equal(EpisodeChange.Uncertain, r.Episodes[0].Change);
        Assert.Equal(ShowStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void Episode_with_no_counterpart_is_not_on_tmdb()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "Pilot", "2018-01-01"), Cur(1, 2, "Unaired Extra", null) },
            TShow(Tm(1, 1, "Pilot", "2018-01-01")));

        Assert.Equal(EpisodeChange.NotOnTmdb, r.Episodes[1].Change);
        Assert.Equal(ShowStatus.Changes, r.Status);
    }

    [Fact]
    public void Placeholder_titles_match_by_number()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "Episode 1"), Cur(1, 2, "Episode 2") },
            TShow(Tm(1, 1, "Episode 1"), Tm(1, 2, "Episode 2")));

        Assert.Equal(ShowStatus.NoChanges, r.Status);
    }

    [Fact]
    public void Matching_episode_group_is_suggested()
    {
        var current = new List<EpisodeInfo>();
        var tmdb = new List<TmdbEpisode>();
        var positions = new Dictionary<int, (int, int)>();
        for (var i = 1; i <= 12; i++)
        {
            current.Add(Cur(i <= 6 ? 1 : 2, i <= 6 ? i : i - 6, $"Chapter title {i} here"));
            var t = Tm(1, i, $"Chapter title {i} here");
            tmdb.Add(t);
            positions[t.Id] = (i <= 6 ? 1 : 2, i <= 6 ? i : i - 6);
        }
        var group = new TmdbEpisodeGroup(new TmdbEpisodeGroupInfo("abc", "DVD Order", 3, 12, 2), positions);

        var r = ShowComparer.Compare(Show, current, TShow(tmdb.ToArray()), new[] { group });

        Assert.NotNull(r.MatchingGroup);
        Assert.Equal("DVD Order", r.MatchingGroup!.Name);
        Assert.Equal(1.0, r.MatchingGroupScore);
    }

    [Fact]
    public void Duplicate_titles_do_not_steal_each_others_match()
    {
        var r = ShowComparer.Compare(Show,
            new[] { Cur(1, 1, "Homecoming", "2001-01-01"), Cur(2, 1, "Homecoming", "2002-01-01") },
            TShow(Tm(1, 1, "Homecoming", "2001-01-01"), Tm(2, 1, "Homecoming", "2002-01-01")));

        Assert.Equal(ShowStatus.NoChanges, r.Status);
    }
}
