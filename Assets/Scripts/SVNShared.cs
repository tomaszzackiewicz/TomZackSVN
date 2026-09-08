using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    /// Wspólne helpery niskiego poziomu — stateless, zero zależności od
    /// UnityEngine, testowalne bez sceny. Celowo NIE w SVNManager (MonoBehaviour
    /// singleton = cykl życia + sprzężenie God-obiektu z każdym modułem).
    public static class SVNShared
    {
        private static readonly string[] SvnUrlPrefixes =
        {
            "http://", "https://", "svn://", "svn+ssh://", "file://"
        };

        /// StartsWith("svn")/("http") łapało lokalne pliki ("svnReadme.txt",
        /// "httpd.conf"). Jawnie rozpoznajemy URL-e; "/" = ścieżka repozyjna.
        public static bool IsServerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path[0] == '/') return true;
            foreach (string prefix in SvnUrlPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// Walidacja + containment względem roota. Odrzuca "..", ścieżki
        /// zakorzenione ("C:\...", "/etc/...") i nielegalne znaki (GetFullPath
        /// rzuca). Zwraca bezpieczną ścieżkę względną.
        public static bool TryGetRelativePath(string root, string input, out string safeRelative)
        {
            safeRelative = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(input)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
                string fullInput = Path.GetFullPath(Path.Combine(fullRoot, input)).Replace('\\', '/');

                if (fullInput.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    safeRelative = "";
                    return true;
                }

                if (fullInput.StartsWith(fullRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    safeRelative = fullInput.Substring(fullRoot.Length + 1);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            return arg.Replace("\"", "\\\"");
        }

        /// Bezpieczny start procesu — Process.Start w lambdach dispatcherowych
        /// wykonuje się na main threadzie PO zakończeniu try/catch na puli:
        /// Win32Exception (brak skojarzenia/pliku) uciekał poza wszystkie catche.
        public static bool TryStartProcess(ProcessStartInfo psi, Action<Exception> onError = null)
        {
            try
            {
                using var process = Process.Start(psi);
                return process != null;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return false;
            }
        }

        /// Cancel + opóźniony Dispose CTS (natychmiastowy Cancel+Dispose rzucał
        /// ObjectDisposedException w umierającej operacji). Wzorzec był wklejany
        /// 7× w czterech klasach.
        public static void CancelAndDisposeLater(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
        }

        /// Sprzątanie starych plików cache — czwarta kopia tego samego patternu
        /// (manager: r*_, blame: Blame_*, diff: svn_base_*/svn_diff_preview*).
        /// Wiek wg LastWriteTime (CreationTime utrzymuje się przez truncate —
        /// świeżo zapisany plik był kasowany przez własny cleanup).
        public static void CleanupOldFiles(string folder, string searchPattern, TimeSpan maxAge)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                DateTime threshold = DateTime.UtcNow - maxAge;
                foreach (string file in Directory.EnumerateFiles(folder, searchPattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < threshold)
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}