using System;
using System.Collections.Generic;

namespace SVN.Core
{
    /// <summary>
    /// Jedno źródło prawdy o nazwie projektu w tabeli:
    ///   "UrlName (folderName)" gdy nazwy się różnią,
    ///   "UrlName"              gdy identyczne lub folder pusty,
    ///   "folderName"           gdy URL pusty/nieparsowalny.
    ///
    /// Używane przez SVNLoad (auto-nazwa przy tworzeniu wpisu)
    /// oraz ProjectSelectionPanel (Reset do auto-nazwy w dialogu Rename).
    /// </summary>
    public static class SVNProjectNaming
    {
        private static readonly HashSet<string> SvnKeywords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "trunk", "branches", "tags" };

        /// <summary>
        /// Nazwa z URL-a. Pomija trunk/branches/tags, ucina .git/.svn i port.
        /// Przykłady:
        ///   svn://srv/MyRepo/trunk          → MyRepo
        ///   svn://srv/MyRepo/branches/x     → MyRepo
        ///   https://host:8443/svn/Repo.git → Repo
        /// </summary>
        public static string FromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";

            try
            {
                string cleaned = url.Trim().TrimEnd('/', '\\');
                var segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) return "";

                string last = segments[segments.Length - 1];

                if (SvnKeywords.Contains(last) && segments.Length > 1)
                    last = segments[segments.Length - 2];

                if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
                    last.EndsWith(".svn", StringComparison.OrdinalIgnoreCase))
                    last = last.Substring(0, last.Length - 4);

                // Bezpiecznik: host:port → ucina port jeśli trafił do segmentu
                int colon = last.IndexOf(':');
                if (colon >= 0) last = last.Substring(colon + 1);

                return last;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Ostatni segment ścieżki (folder roboczy).</summary>
        public static string FolderName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string p = path.Replace('\\', '/').TrimEnd('/');
            int idx = p.LastIndexOf('/');
            return idx >= 0 ? p.Substring(idx + 1) : p;
        }

        /// <summary>
        /// Buduje nazwę wyświetlaną w tabeli projektów.
        /// Reguły:
        ///   - oba puste     → ""
        ///   - tylko folder  → "folder"
        ///   - tylko URL     → "UrlName"
        ///   - identyczne    → "folder"
        ///   - różne         → "UrlName (folder)"
        /// </summary>
        public static string BuildDisplayName(string url, string workingDir)
        {
            string folder = FolderName(workingDir);
            string urlName = FromUrl(url);

            if (string.IsNullOrWhiteSpace(urlName)) return folder;
            if (string.IsNullOrWhiteSpace(folder)) return urlName;
            if (string.Equals(urlName, folder, StringComparison.OrdinalIgnoreCase)) return folder;

            return $"{urlName} ({folder})";
        }

        /// <summary>
        /// Dołącza " (folderName)" do nazwy PODANEJ PRZEZ USERA. Jeśli nazwa już
        /// kończy się jakimś " (...)" — stary sufiks jest zdejmowany i wstawiany
        /// nowy (aktualny folder). Zapobiega dublowaniu "MyRepo (trunk) (trunk)"
        /// i "MyRepo (klientA) (trunk)" przy zmianie folderu.
        ///
        /// Przykłady (folder = "trunk"):
        ///   "MyRepo"                 → "MyRepo (trunk)"
        ///   "Mój klient A"           → "Mój klient A (trunk)"
        ///   "MyRepo (trunk)"         → "MyRepo (trunk)"          (bez zmian)
        ///   "MyRepo (klientA)"       → "MyRepo (trunk)"          (stary suffix zdjęty)
        ///   "trunk"                  → "trunk"                    (identyczne — bez nawiasu)
        ///   ""                       → "trunk"                    (fallback)
        /// </summary>
        public static string EnsureFolderSuffix(string baseName, string workingDir)
        {
            string folder = FolderName(workingDir);
            if (string.IsNullOrWhiteSpace(folder)) return baseName ?? "";
            if (string.IsNullOrWhiteSpace(baseName)) return folder;

            string trimmed = baseName.Trim();

            // Zdejmij ewentualny trailing " (...)" — user mógł wpisać naszą auto-nazwę
            // z poprzedniego otwarcia dialogu, albo skopiować nazwę z innego projektu.
            int lastParen = trimmed.LastIndexOf(')');
            if (lastParen == trimmed.Length - 1)
            {
                int lastOpen = trimmed.LastIndexOf('(', lastParen);
                if (lastOpen > 0 && trimmed[lastOpen - 1] == ' ')
                {
                    trimmed = trimmed.Substring(0, lastOpen - 1).TrimEnd();
                }
            }

            if (string.IsNullOrWhiteSpace(trimmed)) return folder;
            if (string.Equals(trimmed, folder, StringComparison.OrdinalIgnoreCase)) return folder;

            return $"{trimmed} ({folder})";
        }
    }
}