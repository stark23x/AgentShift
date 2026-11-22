// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Models;
using AgentShift.Core.Tmdb;

namespace AgentShift.Core.Comparison;

public enum EpisodeChange
{
    None,
    TitleOnly,
    Renumbered,
    MovedToSpecials,
    MovedFromSpecials,
    MergedOnTmdb,
    SplitOnTmdb,
    NotOnTmdb,
    Uncertain,
}

public enum ShowStatus
{
    NoChanges,
    TitlesOnly,
    Changes,
    NeedsReview,
    AlreadyTmdb,
    NotOnTmdb,
    Error,
}

public sealed class EpisodeResult
{
    public required EpisodeInfo Current { get; init; }

    /// <summary>The TMDB episode that has the same season and episode number as the file today.</summary>
    public TmdbEpisode? SlotEpisode { get; init; }

    /// <summary>The TMDB episode this file actually is.</summary>
    public TmdbEpisode? Match { get; set; }

    /// <summary>For splits: the other TMDB episodes this file also contains.</summary>
    public List<TmdbEpisode> ExtraMatches { get; } = new();

    public string MatchMethod { get; set; } = "";
    public double MatchScore { get; set; }
    public EpisodeChange Change { get; set; }
    public string Note { get; set; } = "";

    // Display helpers (used by the grid and the CSV export).
    public string CurrentCode => Current.Code;
    public string CurrentTitle => Current.Title;
    public string SlotTitle => SlotEpisode?.Name ?? "(no episode at this number)";
    public string NewCode => Match is null
        ? "—"
        : ExtraMatches.Count == 0 ? Match.Code : string.Join(" + ", new[] { Match }.Concat(ExtraMatches).Select(e => e.Code));
    public string NewTitle => Match is null
        ? ""
        : ExtraMatches.Count == 0 ? Match.Name : string.Join(" / ", new[] { Match }.Concat(ExtraMatches).Select(e => e.Name));
    public string ChangeText => Describe(Change);
    public bool IsChanged => Change is not (EpisodeChange.None or EpisodeChange.TitleOnly);
    public string FileName => Current.FilePath is { } p ? Path.GetFileName(p.Replace('\\', '/')) : "";

    public static string Describe(EpisodeChange c) => c switch
    {
        EpisodeChange.None => "No change",
        EpisodeChange.TitleOnly => "Title differs",
        EpisodeChange.Renumbered => "Different number",
        EpisodeChange.MovedToSpecials => "Becomes a special",
        EpisodeChange.MovedFromSpecials => "Special becomes regular",
        EpisodeChange.MergedOnTmdb => "Combined on TMDB",
        EpisodeChange.SplitOnTmdb => "Split on TMDB",
        EpisodeChange.NotOnTmdb => "Not on TMDB",
        EpisodeChange.Uncertain => "Check by hand",
        _ => c.ToString(),
    };
}

public sealed class ShowResult
{
    public required ShowInfo Show { get; init; }
    public ShowStatus Status { get; set; }
    public int? TmdbId { get; set; }
    public string? TmdbName { get; set; }
    public List<EpisodeResult> Episodes { get; } = new();
    public List<string> Notes { get; } = new();
    public string Summary { get; set; } = "";

    /// <summary>A TMDB episode group that would keep the current numbering, if one exists.</summary>
    public TmdbEpisodeGroupInfo? MatchingGroup { get; set; }
    public double MatchingGroupScore { get; set; }

    public int EpisodeCount => Episodes.Count;
    public int ChangedCount => Episodes.Count(e => e.IsChanged);
    public int TitleOnlyCount => Episodes.Count(e => e.Change == EpisodeChange.TitleOnly);

    // Display helpers.
    public string Title => Show.DisplayTitle;
    public string Library => Show.LibraryName;
    public string Ordering => Show.OrderingLabel;
    public string StatusText => Status switch
    {
        ShowStatus.NoChanges => "No changes",
        ShowStatus.TitlesOnly => "Titles only",
        ShowStatus.Changes => "Changes",
        ShowStatus.NeedsReview => "Check by hand",
        ShowStatus.AlreadyTmdb => "Already on TMDB",
        ShowStatus.NotOnTmdb => "Not found on TMDB",
        ShowStatus.Error => "Error",
        _ => Status.ToString(),
    };

    /// <summary>0 = fine, 1 = minor, 2 = review, 3 = changes / problems.</summary>
    public int Severity => Status switch
    {
        ShowStatus.NoChanges or ShowStatus.AlreadyTmdb => 0,
        ShowStatus.TitlesOnly => 1,
        ShowStatus.NeedsReview => 2,
        _ => 3,
    };

    public string Suggestion => MatchingGroup is null
        ? ""
        : $"TMDB episode group \"{MatchingGroup.Name}\" keeps {MatchingGroupScore:P0} of your numbering";

    public string NotesText => string.Join(Environment.NewLine, Notes);
    public string TmdbUrl => TmdbId is { } id ? $"https://www.themoviedb.org/tv/{id}" : "";
}
