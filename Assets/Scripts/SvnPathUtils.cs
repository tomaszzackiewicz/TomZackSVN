using System.IO;

namespace SVN.Core
{
    /// <summary>
    /// Wspólne helpery ścieżek/formatowania — wcześniej logika ".svn"
    /// i formatowanie bajtów było zduplikowane w SVNBar i SVNStatus.
    /// </summary>
    public static class SvnPathUtils
    {
        /// <summary>
        /// Czy ścieżka leży wewnątrz katalogu administracyjnego SVN (.svn).
        /// Bezpieczniejsze niż Contains(".svn") — nie łapie plików typu "foo.svn".
        /// </summary>
        public static bool IsInsideSvnAdminDir(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;

            string n = fullPath.Replace('\\', '/');
            int idx = 0;
            while ((idx = n.IndexOf("/.svn", idx, System.StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = idx + 5;
                if (end == n.Length || n[end] == '/') return true;
                idx = end;
            }
            return false;
        }

        /// <summary>
        /// Idzie w górę drzewa katalogów szukając .svn.
        /// Zwraca korzeń working copy albo null gdy ścieżka nie leży w żadnym WC.
        /// SVN 1.7+ trzyma .svn TYLKO w korzeniu — podfolder checkoutu też tu trafi.
        /// </summary>
        public static string FindWorkingCopyRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(path));
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".svn")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
                return null;
            }
            catch { return null; }
        }

        public static bool IsInsideWorkingCopy(string path) =>
            FindWorkingCopyRoot(path) != null;

        public static string FormatBytes(long bytes)
        {
            double gigabytes = (double)bytes / (1024 * 1024 * 1024);
            if (gigabytes >= 1.0) return $"{gigabytes:F2} GB";
            return $"{(double)bytes / (1024 * 1024):F2} MB";
        }
    }
}