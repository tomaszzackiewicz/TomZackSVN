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

        public static string GetRepoRoot(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            url = url.TrimEnd('/');

            // 1. Jeśli URL kończy się dokładnie na "/trunk" (np. .../Test/trunk), korzeniem jest to co przed nim.
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase))
            {
                return url.Substring(0, url.Length - 6); // 6 to długość "/trunk"
            }

            // 2. Szukamy ostatniego wystąpienia pełnego folderu "/branches/"
            int branchesIdx = url.LastIndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
            if (branchesIdx >= 0)
            {
                return url.Substring(0, branchesIdx);
            }

            // 3. Szukamy ostatniego wystąpienia pełnego folderu "/tags/"
            int tagsIdx = url.LastIndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
            if (tagsIdx >= 0)
            {
                return url.Substring(0, tagsIdx);
            }

            // 4. Jeśli nie ma żadnego ze standardowych folderów, zwracamy URL bez zmian
            return url;
        }

        public static string ParseRevision(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var match = Regex.Match(input, @"revision\s+(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
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