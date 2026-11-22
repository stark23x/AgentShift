// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentShift.Core.Comparison;

/// <summary>Fuzzy episode-title comparison that tolerates punctuation, "Part 1" vs "(1)", accents and so on.</summary>
public static partial class TitleMatcher
{
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        s = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        s = s.Replace("&", " and ", StringComparison.Ordinal);
        s = ParenNumber().Replace(s, " part $1 ");
        s = PartWord().Replace(s, m => " part " + WordToNumber(m.Groups[1].Value) + " ");
        s = Pt().Replace(s, " part ");
        s = NonAlnum().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();
        s = LeadingArticle().Replace(s, "");
        return s;
    }

    /// <summary>Placeholder titles ("Episode 5", "TBA") say nothing about which episode it is.</summary>
    public static bool IsGeneric(string normalized) =>
        normalized.Length == 0 || GenericTitle().IsMatch(normalized);

    /// <summary>
    /// 1 = same title, 0 = nothing in common, null = can't tell (one side is empty or a placeholder).
    /// </summary>
    public static double? Similarity(string? a, string? b) => SimilarityNormalized(Normalize(a), Normalize(b));

    public static double? SimilarityNormalized(string na, string nb)
    {
        if (IsGeneric(na) || IsGeneric(nb)) return null;
        if (na == nb) return 1;

        var shorter = na.Length <= nb.Length ? na : nb;
        var longer = ReferenceEquals(shorter, na) ? nb : na;
        if (shorter.Length >= 5 && longer.Contains(shorter, StringComparison.Ordinal)) return 0.9;

        var dice = Dice(na.Replace(" ", "", StringComparison.Ordinal), nb.Replace(" ", "", StringComparison.Ordinal));
        var tokens = TokenJaccard(na, nb);
        return Math.Max(dice, tokens);
    }

    /// <summary>True when <paramref name="part"/> appears inside <paramref name="whole"/> (e.g. one half of "A / B").</summary>
    public static bool ContainsTitle(string? whole, string? part)
    {
        var w = Normalize(whole);
        var p = Normalize(part);
        if (IsGeneric(p) || p.Length < 4 || w.Length <= p.Length) return false;
        return (" " + w + " ").Contains(" " + p + " ", StringComparison.Ordinal);
    }

    private static double Dice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var k = (a[i] << 16) | a[i + 1];
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        var hits = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var k = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(k, out var c) && c > 0)
            {
                hits++;
                counts[k] = c - 1;
            }
        }
        return 2.0 * hits / (a.Length - 1 + b.Length - 1);
    }

    private static double TokenJaccard(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Count(tb.Contains);
        return (double)inter / (ta.Count + tb.Count - inter);
    }

    private static string WordToNumber(string w) => w switch
    {
        "one" or "i" => "1",
        "two" or "ii" => "2",
        "three" or "iii" => "3",
        "four" or "iv" => "4",
        "five" or "v" => "5",
        _ => w,
    };

    [GeneratedRegex(@"\((\d+)\)")]
    private static partial Regex ParenNumber();

    [GeneratedRegex(@"\bpart\s+(one|two|three|four|five|i{1,3}|iv|v)\b")]
    private static partial Regex PartWord();

    [GeneratedRegex(@"\bpt\.?\s")]
    private static partial Regex Pt();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(the|a|an) ")]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"^((episode|ep|episodio|episodio|folge|chapter|capitulo|aflevering|odcinek)( \d+)+|tba|tbd|untitled|unknown|\d+( \d+)*)$")]
    private static partial Regex GenericTitle();
}
