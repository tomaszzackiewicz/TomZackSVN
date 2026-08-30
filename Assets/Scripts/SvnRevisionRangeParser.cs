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
        // === FIX K2: sanity-limit — literówka '1:999999999' iterowała miliard razy.
        // 100k rewizji to i tak absurd dla cherry-pick/revert; nadmiar odrzucany z warningiem.
        private const long MaxRangeSpan = 100_000;

        public static List<SvnRevisionItem> Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<SvnRevisionItem>();

            var tokens = input
                .Split(new[] { ',', ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().TrimStart('r', 'R'))
                .Where(t => !string.IsNullOrWhiteSpace(t));

            // === FIX K1: dedupe do SortedSet — a NIE rozbijanie do wyniku.
            // Ciągłe bieguny zostaną scalone w ZAKRESY na końcu.
            var revisions = new SortedSet<long>();

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

                        // === FIX K3: rewizje >= 1.
                        if (start < 1) start = 1;

                        if (end - start > MaxRangeSpan)
                        {
                            SVNLogBridge.LogWarning(
                                $"[RevisionParser] Range r{start}:r{end} exceeds limit ({MaxRangeSpan}) — token ignored.");
                            continue;
                        }

                        for (long rev = start; rev <= end; rev++)
                            revisions.Add(rev);
                    }
                }
                else if (long.TryParse(token, out long rev))
                {
                    // === FIX K3.
                    if (rev >= 1)
                        revisions.Add(rev);
                }
            }

            // === FIX K1 (rdzeń): scal ciągłe bieguny w pojedyncze ZAKRESY.
            // "140:150" → 1×(140,150); "150,148:150" → 1×(148,150);
            // "150,151,152" → 1×(150,152); "10,20" → 2×single.
            var result = new List<SvnRevisionItem>();
            long? runStart = null;
            long prev = 0;

            foreach (var rev in revisions)
            {
                if (runStart == null)
                {
                    runStart = rev;
                }
                else if (rev != prev + 1)
                {
                    result.Add(new SvnRevisionItem(runStart.Value, prev));
                    runStart = rev;
                }
                prev = rev;
            }

            if (runStart != null)
                result.Add(new SvnRevisionItem(runStart.Value, prev));

            return result;
        }
    }
}