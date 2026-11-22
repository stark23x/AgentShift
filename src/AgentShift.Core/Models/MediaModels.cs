// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

namespace AgentShift.Core.Models;

/// <summary>Kind of media server a profile points at.</summary>
public enum ServerType
{
    Plex,
    Emby,
    Jellyfin,
}

/// <summary>Which database a show's episode numbering currently follows.</summary>
public enum OrderingKind
{
    Unknown,
    Tvdb,
    Tmdb,
    Other,
}

/// <summary>A TV library on the server.</summary>
public sealed record MediaLibrary(
    string Id,
    string Name,
    string? Source,
    OrderingKind DefaultOrderingKind,
    string? DefaultOrderingLabel)
{
    public override string ToString() => Name;
}

/// <summary>A TV show as the server currently has it.</summary>
public sealed class ShowInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; init; }
    public string? ImdbId { get; init; }

    /// <summary>Human-readable ordering, e.g. "TheTVDB (Aired)".</summary>
    public string OrderingLabel { get; set; } = "Unknown";
    public OrderingKind OrderingKind { get; set; } = OrderingKind.Unknown;
    public string? Path { get; init; }

    public string DisplayTitle => Year is { } y ? $"{Title} ({y})" : Title;
}

/// <summary>One episode slot as the server currently has it.</summary>
public sealed class EpisodeInfo
{
    public required string Id { get; init; }
    public required int Season { get; init; }
    public required int Episode { get; init; }
    public string Title { get; init; } = "";
    public DateOnly? AirDate { get; init; }
    public int? TmdbEpisodeId { get; init; }
    public int? TvdbEpisodeId { get; init; }
    public string? FilePath { get; init; }

    /// <summary>True when this slot is one of several episodes stored in a single file.</summary>
    public bool IsPartOfMultiEpisodeFile { get; init; }

    public string Code => $"S{Season:00}E{Episode:00}";
}

public sealed record ServerIdentity(string Name, string Version);
