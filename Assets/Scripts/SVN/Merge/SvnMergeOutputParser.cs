using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static SVN.Core.SVNMerge;

namespace SVN.Core
{
    public static class SvnMergeOutputParser
    {
        private static readonly HashSet<char> ValidMergeStates = new("UADGRCM");
        private static readonly Regex MergeLineRegex = new(@"^([AUGDRCME ])\s{2,}(\S.+)$", RegexOptions.Compiled);
        private static readonly Regex SkippedLineRegex = new(@"^Skipped\s+['""]?(.+?)['""]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static MergeFileResult Parse(string output)
        {
            var result = new MergeFileResult();

            if (string.IsNullOrWhiteSpace(output) ||
                output.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return result;
            }

            foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimStart();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("Recording mergeinfo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("recorded mergeinfo", StringComparison.OrdinalIgnoreCase))
                {
                    result.MergeInfoUpdated = true;
                    continue;
                }

                var skippedMatch = SkippedLineRegex.Match(line);
                if (skippedMatch.Success)
                {
                    result.Skipped++;
                    result.SkippedPaths.Add(skippedMatch.Groups[1].Value.Trim());
                    continue;
                }

                if (line.Contains("tree conflict", StringComparison.OrdinalIgnoreCase))
                {
                    result.Conflicts++;
                    result.HasTreeConflicts = true;
                    string conflictPath = ExtractPathFromConflictLine(line);
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = conflictPath });
                    continue;
                }

                var match = MergeLineRegex.Match(line);
                if (!match.Success) continue;

                char state = match.Groups[1].Value[0];
                string path = match.Groups[2].Value.Trim();

                if (state == 'C')
                {
                    result.Conflicts++;
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = path });
                    continue;
                }

                // === FIX P4: usunięta heurystyka 'path.Contains("conflict")' —
                // plik o nazwie zawierającej "conflict" był liczony jako konflikt.
                // Konflikt rozpoznaje wyłącznie kolumna statusu ('C') powyżej.

                switch (state)
                {
                    case 'A': result.Added++; break;
                    case 'U':
                    case 'G': result.Updated++; break;
                    case 'D': result.Deleted++; break;
                }

                if (ValidMergeStates.Contains(state))
                {
                    bool isMergeInfoOnly = path == "." || (path.Length <= 2 && path.EndsWith("."));
                    if (!isMergeInfoOnly && !string.IsNullOrWhiteSpace(path))
                    {
                        result.RealChanges++;
                        result.Files.Add(new MergeFileInfo { State = state, Path = path });
                    }
                }
            }

            return result;
        }

        private static string ExtractPathFromConflictLine(string line)
        {
            int quoteStart = line.IndexOf('\'');
            int quoteEnd = line.LastIndexOf('\'');
            if (quoteStart >= 0 && quoteEnd > quoteStart + 1)
                return line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

            if (line.Length > 2) return line.Substring(2).Trim();
            return line;
        }
    }
}