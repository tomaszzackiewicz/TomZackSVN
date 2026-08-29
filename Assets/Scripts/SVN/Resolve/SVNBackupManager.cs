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

        public async Task<string> BackupAsync(string path, CancellationToken token = default)
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

        public async Task SafeDeleteAsync(string path, CancellationToken token = default)
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
                    _log("<color=#FFAA00>[Backup]</color> Failed to create backup folder – deleting permanently.");
                    PermanentDelete(path);
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
                _log($"<color=#FFAA00>[Backup] Failed to move file – deleting permanently.</color>");
                _log($"<color=#FFAA00>  Reason: {ex.Message}</color>");
                PermanentDelete(path);
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

                string backupRoot = Path.Combine(Application.persistentDataPath, $"{projectName}_Backup");

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

        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            return Path.Combine(dir, $"{name}_{timestamp}{ext}");
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
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }
    }
}