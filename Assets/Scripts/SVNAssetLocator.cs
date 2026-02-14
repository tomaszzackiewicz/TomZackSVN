using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

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
            string[] markers = { "/trunk", "/branches", "/tags" };

            foreach (var marker in markers)
            {
                int index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    return url.Substring(0, index);
                }
            }
            return url;
        }

        public static string ParseRevision(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = Regex.Match(input, @"revision\s+(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
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
                Debug.LogWarning($"[AssetLocator] XML Parse error: {e.Message}");
            }
            return null;
        }
    }
}