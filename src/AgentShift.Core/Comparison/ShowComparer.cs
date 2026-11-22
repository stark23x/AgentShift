// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Models;
using AgentShift.Core.Tmdb;

namespace AgentShift.Core.Comparison;

/// <summary>
/// Works out, for every episode file in a show, which TMDB episode it really is and what TMDB would call
/// the number it sits at today. Pure logic: no network, so it's easy to test.
/// </summary>
public static class ShowComparer
{
    private sealed class Candidate
    {
        public required TmdbEpisode Ep { get; init; }
        public required string Norm { get; init; }
    }

    public static ShowResult Compare(ShowInfo show, IReadOnlyList<EpisodeInfo> current, TmdbShow tmdb,
        IEnumerable<TmdbEpisodeGroup>? groups = null)
    {
        var result = new ShowResult { Show = show, TmdbId = tmdb.Id, TmdbName = tmdb.Name };

        var cands = tmdb.Episodes.Select(e => new Candidate { Ep = e, Norm = TitleMatcher.Normalize(e.Name) }).ToList();
        var byId = tmdb.Episodes.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var byTvdb = tmdb.Episodes.Where(e => e.TvdbId is not null).GroupBy(e => e.TvdbId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var bySlot = tmdb.Episodes.GroupBy(e => (e.Season, e.Episode)).ToDictionary(g => g.Key, g => g.First());

        // Current air dates shared by more than one file: an air date alone can't tell those apart.
        var currentDateCounts = current.Where(c => c.AirDate != null).GroupBy(c => c.AirDate!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = current.Select(c => new EpisodeResult
        {
            Current = c,
            SlotEpisode = bySlot.GetValueOrDefault((c.Season, c.Episode)),
        }).ToList();

        // Pass 1: identify every file.
        foreach (var r in rows)
            Identify(r, cands, byId, byTvdb, currentDateCounts, exclude: null);

        ResolveConflicts(rows, cands, byId, byTvdb, currentDateCounts);
        DetectSplits(rows, tmdb);
        Classify(rows);
        Summarize(result, rows, tmdb);

        if (groups != null) PickEpisodeGroup(result, rows, groups);
        return result;
    }

    // ---- identification -------------------------------------------------------------------

    private static void Identify(EpisodeResult r, List<Candidate> cands, Dictionary<int, TmdbEpisode> byId,
        Dictionary<int, TmdbEpisode> byTvdb, Dictionary<DateOnly, int> currentDateCounts, HashSet<int>? exclude)
    {
        var c = r.Current;
        r.Match = null;
        r.MatchMethod = "";
        r.MatchScore = 0;

        if (c.TmdbEpisodeId is { } tid && byId.TryGetValue(tid, out var linked) && exclude?.Contains(linked.Id) != true)
        {
            Set(r, linked, "TMDB link", 1.0);
            return;
        }
        if (c.TvdbEpisodeId is { } vid && byTvdb.TryGetValue(vid, out var viaTvdb) && exclude?.Contains(viaTvdb.Id) != true)
        {
            Set(r, viaTvdb, "TVDB link", 1.0);
            return;
        }

        var norm = TitleMatcher.Normalize(c.Title);
        Candidate? best = null;
        string bestMethod = "";
        double bestScore = 0, secondScore = 0;
        Candidate? second = null;

        foreach (var t in cands)
        {
            if (exclude?.Contains(t.Ep.Id) == true) continue;
            var sim = TitleMatcher.SimilarityNormalized(norm, t.Norm);
            var dateExact = c.AirDate != null && c.AirDate == t.Ep.AirDate;
            var dateNear = !dateExact && c.AirDate is { } ca && t.Ep.AirDate is { } ta &&
                           Math.Abs(ca.DayNumber - ta.DayNumber) <= 1;
            var sameSlot = t.Ep.Season == c.Season && t.Ep.Episode == c.Episode;
            var datesUnknown = c.AirDate is null || t.Ep.AirDate is null;

            string? method = null;
            if (sim >= 0.85) method = dateExact || dateNear ? "Title + air date" : "Title";
            else if (sim >= 0.4 && (dateExact || dateNear)) method = "Title + air date";
            else if (sameSlot && sim >= 0.5) method = "Number + title";
            else if (sameSlot && dateExact) method = "Number + air date";
            else if (sameSlot && sim is null && (dateNear || datesUnknown)) method = "Number (placeholder title)";
            if (method is null) continue;

            var score = (sim ?? 0) * 0.6 + (dateExact ? 0.35 : dateNear ? 0.25 : 0) + (sameSlot ? 0.1 : 0);
            if (score > bestScore)
            {
                second = best; secondScore = bestScore;
                best = t; bestScore = score; bestMethod = method;
            }
            else if (score > secondScore)
            {
                second = t; secondScore = score;
            }
        }

        if (best != null)
        {
            // Two near-identical candidates (e.g. two episodes both called "Pilot") – don't guess.
            if (second != null && bestScore - secondScore < 0.03 && second.Ep.Id != best.Ep.Id)
            {
                // Unless the file holds both ("A / B"): take the first one, and split detection finds the rest.
                if (TitleMatcher.ContainsTitle(c.Title, best.Ep.Name) && TitleMatcher.ContainsTitle(c.Title, second.Ep.Name))
                {
                    var firstEp = (best.Ep.Season, best.Ep.Episode).CompareTo((second.Ep.Season, second.Ep.Episode)) <= 0 ? best : second;
                    Set(r, firstEp.Ep, bestMethod, bestScore);
                    return;
                }
                r.Note = $"Could be {best.Ep.Code} or {second.Ep.Code} on TMDB.";
                return;
            }
            Set(r, best.Ep, bestMethod, bestScore);
            return;
        }

        // Last resort: one TMDB episode aired that day and only one of your files did too.
        if (c.AirDate is { } d && currentDateCounts.GetValueOrDefault(d) == 1)
        {
            var sameDay = cands.Where(t => t.Ep.AirDate == d && exclude?.Contains(t.Ep.Id) != true).ToList();
            if (sameDay.Count == 1)
            {
                Set(r, sameDay[0].Ep, "Air date only", 0.3);
                r.Note = "Matched only by air date; the titles don't agree.";
            }
        }
    }

    private static void Set(EpisodeResult r, TmdbEpisode t, string method, double score)
    {
        r.Match = t;
        r.MatchMethod = method;
        r.MatchScore = score;
    }

    /// <summary>
    /// When several files point at the same TMDB episode, either TMDB really combines them (a merge),
    /// or the fuzzy match was wrong and the weaker ones need another look.
    /// </summary>
    private static void ResolveConflicts(List<EpisodeResult> rows, List<Candidate> cands, Dictionary<int, TmdbEpisode> byId,
        Dictionary<int, TmdbEpisode> byTvdb, Dictionary<DateOnly, int> currentDateCounts)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            var claimed = rows.Where(r => r.Match != null).GroupBy(r => r.Match!.Id).Where(g => g.Count() > 1).ToList();
            foreach (var g in claimed)
            {
                var list = g.OrderBy(r => r.Current.Season).ThenBy(r => r.Current.Episode).ToList();
                if (IsGenuineMerge(list)) continue;

                var keep = list.OrderByDescending(r => r.MatchScore)
                    .ThenBy(r => r.Match!.Season == r.Current.Season && r.Match.Episode == r.Current.Episode ? 0 : 1)
                    .First();
                var taken = rows.Where(r => r.Match != null && r != keep).Select(r => r.Match!.Id).ToHashSet();
                taken.Add(keep.Match!.Id);
                foreach (var other in list.Where(r => r != keep))
                {
                    Identify(other, cands, byId, byTvdb, currentDateCounts, taken);
                    if (other.Match != null) taken.Add(other.Match.Id);
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private static bool IsGenuineMerge(List<EpisodeResult> list)
    {
        // TMDB's own links say so.
        if (list.All(r => r.MatchMethod is "TMDB link" or "TVDB link")) return true;
        // Several slots of one multi-episode file.
        var file = list[0].Current.FilePath;
        if (file != null && list.All(r => r.Current.FilePath == file)) return true;
        // Consecutive episodes, same season, that each look like part of the TMDB episode.
        for (var i = 1; i < list.Count; i++)
            if (list[i].Current.Season != list[0].Current.Season || list[i].Current.Episode != list[i - 1].Current.Episode + 1)
                return false;
        var t = list[0].Match!;
        return list.All(r => (r.Current.AirDate != null && r.Current.AirDate == t.AirDate) ||
                             TitleMatcher.Similarity(r.Current.Title, t.Name) >= 0.5 ||
                             TitleMatcher.ContainsTitle(t.Name, r.Current.Title));
    }

    /// <summary>One file ("A / B") that TMDB lists as two or more episodes.</summary>
    private static void DetectSplits(List<EpisodeResult> rows, TmdbShow tmdb)
    {
        var used = rows.Where(r => r.Match != null).Select(r => r.Match!.Id).ToHashSet();
        foreach (var r in rows.Where(r => r.Match != null))
        {
            var t = r.Match!;
            var neighbours = tmdb.Episodes
                .Where(x => x.Season == t.Season && x.Id != t.Id && Math.Abs(x.Episode - t.Episode) <= 3 && !used.Contains(x.Id))
                .Where(x => x.AirDate == t.AirDate || t.AirDate is null || x.AirDate is null)
                .Where(x => TitleMatcher.ContainsTitle(r.Current.Title, x.Name))
                .OrderBy(x => x.Episode)
                .ToList();
            if (neighbours.Count == 0) continue;
            r.ExtraMatches.AddRange(neighbours);
            foreach (var n in neighbours) used.Add(n.Id);
        }
    }

    // ---- classification -------------------------------------------------------------------

    private static void Classify(List<EpisodeResult> rows)
    {
        var claimCounts = rows.Where(r => r.Match != null).GroupBy(r => r.Match!.Id).ToDictionary(g => g.Key, g => g.Count());
        var identityOfSlot = rows.Where(r => r.Match != null)
            .GroupBy(r => r.Match!.Id)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var r in rows)
        {
            var c = r.Current;
            var t = r.Match;
            if (t is null)
            {
                if (r.SlotEpisode is { } slot)
                {
                    if (identityOfSlot.TryGetValue(slot.Id, out var owner) && owner != r)
                    {
                        r.Change = EpisodeChange.NotOnTmdb;
                        r.Note = Join(r.Note, $"TMDB's {slot.Code} \"{slot.Name}\" is your {owner.Current.Code}.");
                    }
                    else
                    {
                        r.Change = EpisodeChange.Uncertain;
                        r.Note = Join(r.Note, $"TMDB has \"{slot.Name}\" at this number. It may be the same episode under another title.");
                    }
                }
                else
                {
                    r.Change = EpisodeChange.NotOnTmdb;
                    r.Note = Join(r.Note, "TMDB has no episode at this number and no matching title or date.");
                }
                continue;
            }

            if (claimCounts[t.Id] > 1)
            {
                r.Change = EpisodeChange.MergedOnTmdb;
                r.Note = Join(r.Note, $"TMDB lists this together with other files as {t.Code} \"{t.Name}\".");
                continue;
            }
            if (r.ExtraMatches.Count > 0)
            {
                r.Change = EpisodeChange.SplitOnTmdb;
                r.Note = Join(r.Note, $"TMDB lists this as {r.ExtraMatches.Count + 1} episodes: {r.NewCode}.");
                continue;
            }

            if (t.Season == c.Season && t.Episode == c.Episode)
            {
                r.Change = TitlesAgree(c.Title, t.Name) ? EpisodeChange.None : EpisodeChange.TitleOnly;
                continue;
            }

            r.Change = t.Season == 0 && c.Season != 0 ? EpisodeChange.MovedToSpecials
                : c.Season == 0 && t.Season != 0 ? EpisodeChange.MovedFromSpecials
                : EpisodeChange.Renumbered;
            r.Note = Join(r.Note, r.SlotEpisode is { } s
                ? $"TMDB calls this {t.Code}. Its {c.Code} is \"{s.Name}\"."
                : $"TMDB calls this {t.Code}. TMDB has nothing at {c.Code}.");
        }
    }

    private static bool TitlesAgree(string a, string b)
    {
        var na = TitleMatcher.Normalize(a);
        var nb = TitleMatcher.Normalize(b);
        if (na == nb) return true;
        if (TitleMatcher.IsGeneric(na) && TitleMatcher.IsGeneric(nb)) return true;
        return TitleMatcher.SimilarityNormalized(na, nb) >= 0.9;
    }

    private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;

    private static void Summarize(ShowResult result, List<EpisodeResult> rows, TmdbShow tmdb)
    {
        result.Episodes.AddRange(rows);
        if (rows.Count == 0)
        {
            result.Status = ShowStatus.NoChanges;
            result.Summary = "No numbered episodes in the library.";
            return;
        }

        var changed = rows.Where(r => r.IsChanged).ToList();
        var titleOnly = rows.Count(r => r.Change == EpisodeChange.TitleOnly);
        if (changed.Count > 0)
            result.Status = changed.All(r => r.Change == EpisodeChange.Uncertain) ? ShowStatus.NeedsReview : ShowStatus.Changes;
        else
            result.Status = titleOnly > 0 ? ShowStatus.TitlesOnly : ShowStatus.NoChanges;

        var parts = new List<string>();
        void Add(EpisodeChange kind, string one, string many)
        {
            var n = rows.Count(r => r.Change == kind);
            if (n > 0) parts.Add(n == 1 ? one : string.Format(System.Globalization.CultureInfo.InvariantCulture, many, n));
        }
        Add(EpisodeChange.Renumbered, "1 episode gets a different number", "{0} episodes get different numbers");
        Add(EpisodeChange.MovedToSpecials, "1 episode becomes a special", "{0} episodes become specials");
        Add(EpisodeChange.MovedFromSpecials, "1 special becomes a regular episode", "{0} specials become regular episodes");
        Add(EpisodeChange.MergedOnTmdb, "1 file is combined with another", "{0} files are combined with others");
        Add(EpisodeChange.SplitOnTmdb, "1 file is split into several episodes", "{0} files are split into several episodes");
        Add(EpisodeChange.NotOnTmdb, "1 episode isn't on TMDB", "{0} episodes aren't on TMDB");
        Add(EpisodeChange.Uncertain, "1 episode needs checking by hand", "{0} episodes need checking by hand");
        Add(EpisodeChange.TitleOnly, "1 title differs", "{0} titles differ");
        result.Summary = parts.Count == 0 ? $"All {rows.Count} episodes match." : string.Join(", ", parts) + ".";

        // Season layout differences are often the real story (e.g. TVDB splits a season TMDB keeps whole).
        var mine = rows.Where(r => r.Current.Season > 0).Select(r => r.Current.Season).Distinct().OrderBy(x => x).ToList();
        var theirs = rows.Where(r => r.Match is { Season: > 0 }).Select(r => r.Match!.Season).Distinct().OrderBy(x => x).ToList();
        if (mine.Count > 0 && theirs.Count > 0 && !mine.SequenceEqual(theirs))
            result.Notes.Add($"Your episodes are in {Seasons(mine)}; on TMDB the same episodes are in {Seasons(theirs)}.");

        var tmdbOnly = tmdb.Episodes.Count(e => e.Season > 0) - rows.Count(r => r.Match is { Season: > 0 });
        if (tmdbOnly > 0)
            result.Notes.Add($"TMDB lists {tmdbOnly} regular episode{(tmdbOnly == 1 ? "" : "s")} you don't have (this isn't a change).");
    }

    private static string Seasons(List<int> s)
    {
        if (s.Count == 1) return $"season {s[0]}";
        var contiguous = s.Last() - s.First() + 1 == s.Count;
        return contiguous ? $"seasons {s.First()}–{s.Last()}" : "seasons " + string.Join(", ", s);
    }

    // ---- episode groups -------------------------------------------------------------------

    private static void PickEpisodeGroup(ShowResult result, List<EpisodeResult> rows, IEnumerable<TmdbEpisodeGroup> groups)
    {
        var identified = rows.Where(r => r.Match != null && r.ExtraMatches.Count == 0 && r.Change != EpisodeChange.MergedOnTmdb).ToList();
        if (identified.Count == 0) return;

        var defaultScore = (double)identified.Count(r => r.Change is EpisodeChange.None or EpisodeChange.TitleOnly) / identified.Count;
        TmdbEpisodeGroup? best = null;
        double bestScore = 0;
        foreach (var g in groups)
        {
            var hits = identified.Count(r => g.Positions.TryGetValue(r.Match!.Id, out var p) &&
                                             p.Season == r.Current.Season && p.Episode == r.Current.Episode);
            var score = (double)hits / identified.Count;
            if (score > bestScore) { best = g; bestScore = score; }
        }

        if (best != null && bestScore >= 0.9 && bestScore > defaultScore + 0.01)
        {
            result.MatchingGroup = best.Info;
            result.MatchingGroupScore = bestScore;
            result.Notes.Add($"The TMDB episode group \"{best.Info.Name}\" ({best.Info.TypeName}) keeps {bestScore:P0} of your " +
                             $"current numbering, against {defaultScore:P0} for TMDB's default order. If your server lets you " +
                             "choose an episode group for this show, picking it would avoid most of the changes.");
        }
    }
}
