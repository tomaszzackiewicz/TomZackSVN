using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SVN.Core
{
    public static class SVNPathUtilities
    {
        public static string NormalizePath(string path) =>
            string.IsNullOrWhiteSpace(path)
                ? ""
                : path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim().TrimEnd('/');

        public static bool TryGetRelativePath(string root, string path, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

            string normRoot = Path.GetFullPath(root.Trim()).Replace('\\', '/').TrimEnd('/');
            string normInput = path.Replace('\\', '/').Trim();

            try
            {
                string absolutePath = Path.IsPathRooted(normInput)
                    ? Path.GetFullPath(normInput)
                    : Path.GetFullPath(Path.Combine(normRoot, normInput));

                absolutePath = absolutePath.Replace('\\', '/').TrimEnd('/');

                if (absolutePath.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = ".";
                    return true;
                }

                string prefix = normRoot + "/";

                if (!absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                relativePath = absolutePath.Substring(prefix.Length).Replace('\\', '/').Trim('/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch
            {
                return false;
            }
        }

        public static List<SVNConflictData> SortConflictsDeepestFirst(List<SVNConflictData> conflicts)
        {
            return conflicts.OrderByDescending(c => NormalizePath(c.Path).Count(ch => ch == '/'))
                           .ThenByDescending(c => c.Path.Length)
                           .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }

        public static bool HasUnresolvedParentConflict(string path, List<SVNConflictData> conflicts)
        {
            if (string.IsNullOrWhiteSpace(path) || conflicts == null || conflicts.Count == 0) return false;

            string normalized = NormalizePath(path);
            string parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/').Trim().TrimEnd('/');

            while (!string.IsNullOrWhiteSpace(parent))
            {
                if (conflicts.Any(c => NormalizePath(c.Path).Equals(parent, StringComparison.OrdinalIgnoreCase)))
                    return true;
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/').Trim().TrimEnd('/');
            }
            return false;
        }

        public static string ForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace('\\', '/');
        }

        public static bool IsBinaryFile(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return false;

            try
            {
                using var fs = File.OpenRead(fullPath);
                byte[] buffer = new byte[8000];
                int read = fs.Read(buffer, 0, buffer.Length);
                if (read == 0) return false;

                if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return false; // UTF-16 LE / UTF-32 LE (starts FF FE)
                if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return false; // UTF-16 BE
                if (read >= 4 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xFE && buffer[3] == 0xFF) return false; // UTF-32 BE

                for (int i = 0; i < read; i++)
                    if (buffer[i] == 0) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}