using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBackupManager
    {
        private readonly SVNManager _svnManager;
        private readonly Action<string> _log;

        public SVNBackupManager(SVNManager manager, Action<string> log)
        {
            _svnManager = manager;
            _log = log;
        }

        // === FIX 9: metody faktycznie asynchroniczne (Task.Run) — wcześniej
        // 'async' bez await (ostrzeżenie CS1998) i blokujące I/O na wątku
        // wywołującym. Wywołania "await ..." po stronie callerów bez zmian.
        public Task<string> BackupAsync(string path, CancellationToken token = default)
        {
            return Task.Run(() => BackupCore(path, token), token);
        }

        private string BackupCore(string path, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return null;

                token.ThrowIfCancellationRequested();

                string backupRoot = GetBackupRoot();
                if (string.IsNullOrEmpty(backupRoot))
                {
                    _log("<color=#FFAA00>[Backup]</color> Failed to create backup folder.");
                    return null;
                }

                string relative = GetRelativeToWorkingDir(path);
                string destPath = Path.Combine(backupRoot, relative);
                destPath = MakeUniquePath(destPath);

                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(path, destPath, true);
                }
                else if (Directory.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    CopyDirectory(path, destPath, token);
                }

                _log($"<color=#00FF88><b>[Backup]</b></color> Backup created:");
                _log($"<color=yellow>  Source :</color> {path}");
                _log($"<color=yellow>  Backup :</color> <color=yellow>{destPath}</color>");
                _log($"<color=yellow>  Backup folder: {backupRoot}</color>");

                return destPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"<color=#FFAA00>[Backup] Failed to create backup: {ex.Message}</color>");
                return null;
            }
        }

        public Task SafeDeleteAsync(string path, CancellationToken token = default)
        {
            return Task.Run(() => SafeDeleteCore(path, token), token);
        }

        private void SafeDeleteCore(string path, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return;

                token.ThrowIfCancellationRequested();

                string backupRoot = GetBackupRoot();
                if (string.IsNullOrEmpty(backupRoot))
                {
                    // === FIX P0: backup fail → STOP (nie kasuj!)
                    // Wcześniej: PermanentDelete = utrata danych bez backupu.
                    _log("<color=#FF4444>[Backup] Failed to create backup folder — deletion ABORTED (file preserved).</color>");
                    _log($"<color=#FF4444>  Path preserved: {path}</color>");
                    _log("<color=#FFAA00>  Resolve manually (check disk space / permissions).</color>");
                    return;
                }

                string relative = GetRelativeToWorkingDir(path);
                string destPath = Path.Combine(backupRoot, relative);
                destPath = MakeUniquePath(destPath);

                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Move(path, destPath);
                }
                else if (Directory.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    Directory.Move(path, destPath);
                }

                _log($"<color=#00FF88><b>[Backup]</b></color> File moved to backup:");
                _log($"<color=yellow>  Source :</color> {path}");
                _log($"<color=yellow>  Backup :</color> <color=yellow>{destPath}</color>");
                _log($"<color=yellow>  Backup folder: {backupRoot}</color>");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // === FIX P0: backup fail → STOP (nie kasuj!)
                _log("<color=#FF4444><b>[Backup] Failed to move file — deletion ABORTED (file preserved).</b></color>");
                _log($"<color=#FF4444>  Path preserved: {path}</color>");
                _log($"<color=#FF4444>  Reason: {ex.Message}</color>");
                _log("<color=#FFAA00>  Resolve manually (file is still on disk).</color>");
            }
        }

        private string GetBackupRoot()
        {
            try
            {
                string projectName = Application.productName;
                if (string.IsNullOrWhiteSpace(projectName))
                    projectName = "SVN_Project";

                foreach (char c in Path.GetInvalidFileNameChars())
                    projectName = projectName.Replace(c, '_');

                string backupRoot = Path.Combine(SVNPrefs.PersistentDataPath, $"{projectName}_Backup");

                if (!Directory.Exists(backupRoot))
                    Directory.CreateDirectory(backupRoot);

                return backupRoot;
            }
            catch
            {
                return null;
            }
        }

        private string GetRelativeToWorkingDir(string fullPath)
        {
            try
            {
                string root = Path.GetFullPath(_svnManager.WorkingDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(fullPath);

                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length)
                               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return Path.GetFileName(fullPath);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        // === FIX 12: pętla antykolizyjna — dwa backupy tej samej ścieżki w tej
        // samej sekundzie nadpisywały się (timestamp ma rozdzielczość 1s).
        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string candidate = Path.Combine(dir, $"{name}_{timestamp}{ext}");
            int counter = 1;
            while (File.Exists(candidate) || Directory.Exists(candidate))
                candidate = Path.Combine(dir, $"{name}_{timestamp}_{counter++}{ext}");

            return candidate;
        }

        private static void CopyDirectory(string sourceDir, string destDir, CancellationToken token)
        {
            if (!Directory.Exists(sourceDir))
                return;

            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                token.ThrowIfCancellationRequested();
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                token.ThrowIfCancellationRequested();
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir, token);
            }
        }

        // === FIX 11: czyszczenie atrybutów także na katalogach — bez tego
        // Directory.Delete(recursive) po cichu nie usuwał struktury read-only
        // (spójnie z wersją PermanentDelete w SVNConflictCore).
        private static void PermanentDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(dir, FileAttributes.Normal);
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }
    }
}