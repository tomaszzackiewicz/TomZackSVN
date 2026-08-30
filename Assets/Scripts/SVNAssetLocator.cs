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

        // UWAGA semantyczna: sprawdza, czy ścieżka jest KORZENIEM working copy
        // (katalog .svn bezpośrednio w niej). Podkatalog checkoutu (bez własnego
        // .svn) zwraca false — svn operuje tam poprawnie, ale projekt powinien
        // wskazywać korzeń (tak rejestrują go Checkout/Load).
        public static bool IsWorkingCopy(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Directory.Exists(Path.Combine(path, ".svn"));
        }

        // === NOWE: czy ścieżka NALEŻY do working copy (.svn tu lub u dowolnego
        // przodka) — dla pytań typu "czy można tu wykonać svn" (zamiast "czy to
        // korzeń"). Wzorzec z SVNClean.ValidatePath.
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

        // === FIX K1: najpłytsze wystąpienie markera struktury (z granicą
        // segmentu). Wcześniej EndsWith("/trunk") + LastIndexOf("/branches/")
        // liczyły korzeń OD DOŁU: repo/branches/foo/branches/bar → "korzeń"
        // repo/branches/foo (zamiast repo); repo/branches/foo/trunk →
        // repo/branches/foo. Wszystkie URL-e budowane z repoRoot (BranchTag,
        // Merge, Revision, Repair) wskazywały wtedy złe miejsce.
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

            // Brak markerów → URL bez zmian (jak dotychczas).
            if (cut == int.MaxValue) return url;

            return url.Substring(0, cut);
        }

        // Pierwsze wystąpienie markera będące PEŁNYM segmentem ścieżki
        // (zakończonym końcem URL-a albo '/'). Chroni przed fałszywymi
        // trafieniami typu "/tagsArchive" czy "/trunking".
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

                searchFrom = idx + 1;   // marker w środku słowa — szukaj dalej
            }
        }

        // === FIX K2: tolerancja separatora — "Revision: 123" (dwukropek) też
        // parsowane; wcześniej '\s+' wymagało wyłącznie białych znaków.
        public static string ParseRevision(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var match = Regex.Match(input, @"revision\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase);
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