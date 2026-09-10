using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace SVN.Core
{
    public static class SVNAssetLocator
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Replace("\\", "/").Trim();
        }

        public static bool IsWorkingCopy(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Directory.Exists(Path.Combine(path, ".svn"));
        }

        public static bool IsInsideWorkingCopy(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string cur = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                while (!string.IsNullOrEmpty(cur))
                {
                    if (Directory.Exists(Path.Combine(cur, ".svn")))
                        return true;

                    var parent = Directory.GetParent(cur);
                    if (parent == null) break;
                    cur = parent.FullName;
                }
            }
            catch { }
            return false;
        }

        public static string GetRepoRoot(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            url = url.TrimEnd('/');

            int cut = int.MaxValue;

            int trunkIdx = IndexOfStructureMarker(url, "/trunk");
            if (trunkIdx >= 0 && trunkIdx < cut) cut = trunkIdx;

            int branchesIdx = IndexOfStructureMarker(url, "/branches");
            if (branchesIdx >= 0 && branchesIdx < cut) cut = branchesIdx;

            int tagsIdx = IndexOfStructureMarker(url, "/tags");
            if (tagsIdx >= 0 && tagsIdx < cut) cut = tagsIdx;

            if (cut == int.MaxValue) return url;

            return url.Substring(0, cut);
        }

        private static int IndexOfStructureMarker(string url, string marker)
        {
            int searchFrom = 0;
            while (true)
            {
                int idx = url.IndexOf(marker, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return -1;

                int after = idx + marker.Length;
                if (after >= url.Length || url[after] == '/')
                    return idx;

                searchFrom = idx + 1;
            }
        }

        /// <summary>
        /// === FIX (cichy pusty string): wcześniej regex wymagał prefiksu
        /// "revision". Jeśli caller wrzucał sam numer ("123"), dostawał "" bez
        /// ostrzeżenia. Teraz akceptujemy oba warianty. Priorytet ma jawny
        /// prefiks — "abc revision 5 2024" → "5", nie "2024".
        /// </summary>
        public static string ParseRevision(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // 1. Preferuj "revision 5" / "revision: 5" / "revision=5".
            var prefixed = Regex.Match(input, @"revision\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase);
            if (prefixed.Success) return prefixed.Groups[1].Value;

            // 2. Fallback: sam numer (np. wpisany w pole input).
            var plain = Regex.Match(input, @"^\s*(\d+)\s*$");
            if (plain.Success) return plain.Groups[1].Value;

            return string.Empty;
        }

        public static string ExtractUserFromUrl(string xmlOutput)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);
                XmlNode urlNode = doc.SelectSingleNode("//url");

                if (urlNode != null)
                {
                    string fullUrl = urlNode.InnerText;
                    var match = Regex.Match(fullUrl, @"://([^@/]+)@");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
            }
            catch (Exception e)
            {
                SVNLogBridge.LogError($"[AssetLocator] XML Parse error: {e.Message}");
            }
            return null;
        }
    }
}