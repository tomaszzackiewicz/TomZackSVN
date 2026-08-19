using System;
using System.Collections.Generic;
using System.Linq;

namespace SVN.Core
{
    public readonly struct SvnRevisionItem
    {
        public readonly long Start;
        public readonly long End;

        public bool IsRange => Start != End;

        public SvnRevisionItem(long start, long end)
        {
            Start = start;
            End = end;
        }
    }

    public static class SvnRevisionRangeParser
    {
        public static List<SvnRevisionItem> Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<SvnRevisionItem>();

            var tokens = input
                .Split(new[] { ',', ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().TrimStart('r', 'R'))
                .Where(t => !string.IsNullOrWhiteSpace(t));

            var result = new List<SvnRevisionItem>();
            var seen = new HashSet<long>();

            foreach (var token in tokens)
            {
                if (token.Contains(':'))
                {
                    var parts = token.Split(':');
                    if (parts.Length == 2 &&
                        long.TryParse(parts[0], out long start) &&
                        long.TryParse(parts[1], out long end))
                    {
                        if (start > end)
                            (start, end) = (end, start);

                        for (long rev = start; rev <= end; rev++)
                        {
                            if (seen.Add(rev))
                                result.Add(new SvnRevisionItem(rev, rev));
                        }
                    }
                }
                else
                {
                    if (long.TryParse(token, out long rev) && seen.Add(rev))
                        result.Add(new SvnRevisionItem(rev, rev));
                }
            }

            return result
                .OrderBy(x => x.Start)
                .ThenBy(x => x.End)
                .ToList();
        }
    }
}