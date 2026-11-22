// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Comparison;
using AgentShift.Core.Models;
using AgentShift.Core.Servers;
using AgentShift.Core.Tmdb;

namespace AgentShift.Core.Scanning;

public sealed class ScanOptions
{
    public bool CompareShowsAlreadyOnTmdb { get; init; }
    public bool UseEpisodeLinks { get; init; } = true;
    public bool CheckEpisodeGroups { get; init; } = true;

    /// <summary>Shows scanned at the same time.</summary>
    public int Parallelism { get; init; } = 4;
}

public sealed record ScanProgress(int Done, int Total, string Message);

/// <summary>Runs the whole scan: list shows, fetch TMDB data, compare. Read-only on both sides.</summary>
public sealed class LibraryScanner
{
    private readonly IMediaServerConnector _server;
    private readonly TmdbClient _tmdb;
    private readonly ScanOptions _options;

    public LibraryScanner(IMediaServerConnector server, TmdbClient tmdb, ScanOptions options)
    {
        _server = server;
        _tmdb = tmdb;
        _options = options;
    }

    public async Task ScanAsync(IEnumerable<MediaLibrary> libraries, Action<ShowResult> onResult,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var shows = new List<ShowInfo>();
        foreach (var lib in libraries)
        {
            progress?.Report(new ScanProgress(0, 0, $"Reading shows in \"{lib.Name}\"…"));
            shows.AddRange(await _server.GetShowsAsync(lib, ct));
        }

        var done = 0;
        progress?.Report(new ScanProgress(0, shows.Count, $"Found {shows.Count} shows."));
        using var gate = new SemaphoreSlim(Math.Max(1, _options.Parallelism));
        var tasks = shows.Select(async show =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await ScanShowAsync(show, ct);
                onResult(r);
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress(n, shows.Count, $"Checked {show.DisplayTitle}"));
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task<ShowResult> ScanShowAsync(ShowInfo show, CancellationToken ct)
    {
        try
        {
            if (show.OrderingKind == OrderingKind.Tmdb && !_options.CompareShowsAlreadyOnTmdb)
                return new ShowResult
                {
                    Show = show,
                    Status = ShowStatus.AlreadyTmdb,
                    TmdbId = show.TmdbId,
                    Summary = "Already follows TMDB order, so switching changes nothing.",
                };

            var tmdbId = await ResolveTmdbIdAsync(show, ct);
            if (tmdbId is null)
                return new ShowResult
                {
                    Show = show,
                    Status = ShowStatus.NotOnTmdb,
                    Summary = "Couldn't find this show on TMDB from its TVDB or IMDb id.",
                };
            show.TmdbId = tmdbId;

            var episodesTask = _server.GetEpisodesAsync(show, ct);
            var tmdbTask = _tmdb.GetShowWithEpisodesAsync(tmdbId.Value, ct);
            var episodes = await episodesTask;
            var tmdbShow = await tmdbTask;
            if (tmdbShow is null)
                return new ShowResult
                {
                    Show = show,
                    Status = ShowStatus.NotOnTmdb,
                    TmdbId = tmdbId,
                    Summary = $"TMDB id {tmdbId} no longer exists on TMDB.",
                };

            var result = ShowComparer.Compare(show, episodes, tmdbShow);

            // Second pass with TVDB links on TMDB episodes, only for shows that still have loose ends.
            if (_options.UseEpisodeLinks && NeedsLinks(result, episodes))
            {
                await _tmdb.LoadEpisodeTvdbIdsAsync(tmdbId.Value, tmdbShow.Episodes, ct);
                result = ShowComparer.Compare(show, episodes, tmdbShow);
            }

            if (_options.CheckEpisodeGroups && result.Status is ShowStatus.Changes or ShowStatus.NeedsReview &&
                tmdbShow.EpisodeGroups.Count > 0)
            {
                var groups = new List<TmdbEpisodeGroup>();
                foreach (var info in tmdbShow.EpisodeGroups.Take(12))
                    if (await _tmdb.GetEpisodeGroupAsync(info, ct) is { } g) groups.Add(g);
                result = ShowComparer.Compare(show, episodes, tmdbShow, groups);
            }

            if (show.OrderingKind == OrderingKind.Tmdb)
                result.Notes.Insert(0, "This show already follows TMDB order; it was compared because you asked for all shows.");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ShowResult { Show = show, Status = ShowStatus.Error, Summary = ex.Message };
        }
    }

    private static bool NeedsLinks(ShowResult r, IReadOnlyList<EpisodeInfo> episodes) =>
        episodes.Any(e => e.TvdbEpisodeId != null) &&
        r.Episodes.Any(e => e.Match is null || e.MatchMethod is "Air date only" or "Number (placeholder title)" ||
                            (e.IsChanged && e.MatchMethod != "TMDB link"));

    private async Task<int?> ResolveTmdbIdAsync(ShowInfo show, CancellationToken ct)
    {
        if (show.TmdbId is { } id) return id;
        if (show.TvdbId is { } tvdb && await _tmdb.FindShowByTvdbIdAsync(tvdb, ct) is { } viaTvdb) return viaTvdb;
        if (!string.IsNullOrEmpty(show.ImdbId) && await _tmdb.FindShowByImdbIdAsync(show.ImdbId, ct) is { } viaImdb) return viaImdb;
        return null;
    }
}
