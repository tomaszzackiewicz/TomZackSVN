using System;

namespace SVN.Core
{
    public static class SvnMergeUrlResolver
    {
        public static bool ValidateSourceInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (input.Contains("://")) return false;
            if (input.StartsWith("/")) return false;
            if (input.Contains("..") || input.Contains("//") ||
                input.Contains("\\") || input.Contains("\0"))
                return false;

            foreach (char c in input)
            {
                if (c == ';' || c == '|' || c == '&' || c == '>' ||
                    c == '<' || c == '$' || c == '`' || c == '(' || c == ')')
                    return false;
            }
            return true;
        }

        public static string ResolveSourceUrl(string input, string repoRoot)
        {
            string trimmed = input.Trim().TrimStart('/');

            if (trimmed.StartsWith("branches/", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/{trimmed}";
            if (trimmed.StartsWith("tags/", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/{trimmed}";
            if (trimmed.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/trunk";

            return $"{repoRoot}/branches/{trimmed}";
        }

        public static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            if (arg.Contains(' ') || arg.Contains('"'))
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            return arg.Replace("\"", "\\\"");
        }
    }
}