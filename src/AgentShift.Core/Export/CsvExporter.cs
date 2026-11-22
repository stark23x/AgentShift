// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using AgentShift.Core.Comparison;

namespace AgentShift.Core.Export;

public static class CsvExporter
{
    /// <summary>One row per episode, including shows with no changes. Opens cleanly in Excel (UTF-8 with BOM).</summary>
    public static void WriteEpisodes(string path, IEnumerable<ShowResult> results)
    {
        var sb = new StringBuilder();
        Row(sb, "Show", "Library", "Current ordering", "Show status", "TMDB id",
            "Current number", "Current title", "TMDB title at this number",
            "TMDB number", "TMDB title", "Change", "Matched by", "Note", "File");
        foreach (var s in results)
        {
            if (s.Episodes.Count == 0)
            {
                Row(sb, s.Title, s.Library, s.Ordering, s.StatusText, s.TmdbId?.ToString() ?? "",
                    "", "", "", "", "", "", "", s.Summary, "");
                continue;
            }
            foreach (var e in s.Episodes)
                Row(sb, s.Title, s.Library, s.Ordering, s.StatusText, s.TmdbId?.ToString() ?? "",
                    e.CurrentCode, e.CurrentTitle, e.SlotEpisode?.Name ?? "",
                    e.Match is null ? "" : e.NewCode, e.NewTitle, e.ChangeText, e.MatchMethod, e.Note,
                    e.Current.FilePath ?? "");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>One row per show.</summary>
    public static void WriteShows(string path, IEnumerable<ShowResult> results)
    {
        var sb = new StringBuilder();
        Row(sb, "Show", "Library", "Current ordering", "Status", "Episodes", "Changed", "Titles differ",
            "Summary", "Suggestion", "Notes", "TMDB link");
        foreach (var s in results)
            Row(sb, s.Title, s.Library, s.Ordering, s.StatusText, s.EpisodeCount.ToString(), s.ChangedCount.ToString(),
                s.TitleOnlyCount.ToString(), s.Summary, s.Suggestion, string.Join(" ", s.Notes), s.TmdbUrl);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void Row(StringBuilder sb, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(fields[i]));
        }
        sb.Append("\r\n");
    }

    internal static string Escape(string? v)
    {
        v ??= "";
        // Stop spreadsheet apps from treating a title like "=Cool" as a formula.
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
