using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FModel.Services;

/// <summary>Shares the prepared search terms with visible result rows.</summary>
public sealed class SearchHighlightPattern
{
    private readonly string[] _terms;
    private readonly StringComparison _comparison;
    private readonly Regex _regex;

    public SearchHighlightPattern(string[] terms, bool matchCase, Regex regex = null)
    {
        _terms = terms;
        _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _regex = regex;
    }

    public IReadOnlyList<(int Start, int Length)> GetRanges(string text)
    {
        var ranges = new List<(int Start, int Length)>();
        if (_regex != null)
        {
            foreach (Match match in _regex.Matches(text))
            {
                if (match.Length > 0)
                    ranges.Add((match.Index, match.Length));
            }
        }
        else
        {
            foreach (var term in _terms)
            {
                if (term.Length == 0)
                    continue;

                var start = 0;
                while (start <= text.Length - term.Length)
                {
                    var index = text.IndexOf(term, start, _comparison);
                    if (index < 0)
                        break;

                    ranges.Add((index, term.Length));
                    start = index + 1;
                }
            }
        }

        // Merge overlapping terms so every character is displayed exactly once.
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(int Start, int Length)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].Start + merged[^1].Length)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            var end = Math.Max(previous.Start + previous.Length, range.Start + range.Length);
            merged[^1] = (previous.Start, end - previous.Start);
        }

        return merged;
    }
}
