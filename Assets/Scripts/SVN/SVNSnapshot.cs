using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNSnapshot : SVNBase, IDisposable
    {
        private const string SnapshotName = "CurrentSnapshot";

        private readonly string _snapshotFolder;
        private CancellationTokenSource _cts;
        private int _processingFlag;
        private int _disposed;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNSnapshot(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _snapshotFolder = Path.Combine(Application.persistentDataPath, "SVN_Snapshots");
            Directory.CreateDirectory(_snapshotFolder);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        public void Cancel()
        {
            try
            {
                var cts = Volatile.Read(ref _cts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = FireAndForget(operation);
        }

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] Unhandled:</color> {ex.Message}"));
            }
        }

        public void ExecuteCreateSnapshot()
        {
            SafeFireAndForget(async () =>
            {
                await CreateSnapshotAsync("Manual").ConfigureAwait(false);
            });
        }

        public void ExecuteRestoreSnapshot()
        {
            SafeFireAndForget(async () =>
            {
                await RestoreSnapshotAsync().ConfigureAwait(false);
            });
        }

        public void ExecuteDeleteSnapshot()
        {
            try
            {
                string patchPath = GetSnapshotFilePath();
                string addedFilesPath = GetAddedFilesFolder();
                string metaPath = GetMetadataPath();

                if (File.Exists(patchPath)) File.Delete(patchPath);
                if (Directory.Exists(addedFilesPath)) Directory.Delete(addedFilesPath, true);
                if (File.Exists(metaPath)) File.Delete(metaPath);

                SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Snapshot deleted.\nPath: {_snapshotFolder}</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] Delete failed:</color> {ex.Message}");
            }
        }

        private async Task<bool> CreateSnapshotAsync(string reason = null)
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Snapshot] Another operation is already running.</color>"));
                return false;
            }

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=#FF9900>[Snapshot] Invalid working directory.</color>"));
                    return false;
                }

                string patchPath = GetSnapshotFilePath();
                string addedFilesPath = GetAddedFilesFolder();

                PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Creating snapshot ({reason ?? "Manual"})...\nDestination: {_snapshotFolder}"));

                string statusOutput = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(statusOutput))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Working copy is clean. Creating empty snapshot.\nPath: {_snapshotFolder}</color>"));
                }
                else
                {
                    int statusLineCount = statusOutput.Split('\n').Length;
                    PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Detected {statusLineCount} changed/untracked items."));
                }

                List<string> unversionedFiles = ParseUnversionedFiles(statusOutput, root);

                bool unversionedOnly = false;
                PostUI(() => unversionedOnly = svnUI?.SnapshotUnversionedOnlyToggle?.isOn ?? false);

                string diff = string.Empty;
                bool hasTrackedChanges = false;

                if (!unversionedOnly)
                {
                    PostUI(() => SVNLogBridge.LogLine("[Snapshot] Generating patch..."));
                    diff = await SvnRunner.RunAsync("diff", root, false, token).ConfigureAwait(false);
                    hasTrackedChanges = !string.IsNullOrWhiteSpace(diff);
                }
                else
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=yellow>[Snapshot] Unversioned-only mode. Skipping diff generation.</color>"));
                }

                bool hasUnversionedFiles = unversionedFiles.Count > 0;

                if (File.Exists(patchPath)) File.Delete(patchPath);
                if (Directory.Exists(addedFilesPath)) Directory.Delete(addedFilesPath, true);
                Directory.CreateDirectory(addedFilesPath);

                string normalizedDiff = hasTrackedChanges ? diff.Replace("\r\n", "\n") : string.Empty;
                await File.WriteAllTextAsync(patchPath, normalizedDiff, new UTF8Encoding(false), token).ConfigureAwait(false);

                if (hasUnversionedFiles)
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Backing up {unversionedFiles.Count} unversioned files..."));
                    foreach (string sourcePath in unversionedFiles)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) continue;

                        string relativePath = Path.GetRelativePath(root, sourcePath);
                        string destinationPath = Path.Combine(addedFilesPath, relativePath);
                        string destinationDirectory = Path.GetDirectoryName(destinationPath);

                        if (!string.IsNullOrWhiteSpace(destinationDirectory))
                            Directory.CreateDirectory(destinationDirectory);

                        if (File.Exists(sourcePath))
                            File.Copy(sourcePath, destinationPath, true);
                        else if (Directory.Exists(sourcePath))
                            CopyDirectory(sourcePath, destinationPath);
                    }
                }

                WriteMetadata(reason, hasTrackedChanges, unversionedFiles.Count, unversionedOnly);

                PostUI(() => SVNLogBridge.LogLine($"<color=#55FF55>[Snapshot] Successfully created ({reason ?? "Manual"})\nSaved to: {_snapshotFolder}</color>"));
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Snapshot] Cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FF9900>[Snapshot] FAILED (Path: {_snapshotFolder}):\n{ex}</color>"));
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                try { cts.Dispose(); } catch { }
                ExitProcessing();
            }
        }

        private async Task<bool> RestoreSnapshotAsync()
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Snapshot] Another operation is already running.</color>"));
                return false;
            }

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string root = svnManager?.WorkingDir;
                string patchPath = GetSnapshotFilePath();
                string addedFilesPath = GetAddedFilesFolder();
                string metaPath = GetMetadataPath();

                if (!File.Exists(patchPath))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] No snapshot found to restore.\nExpected at: {_snapshotFolder}</color>"));
                    return false;
                }

                bool isUnversionedOnly = false;
                if (File.Exists(metaPath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(metaPath);
                        foreach (string l in lines)
                        {
                            if (l.StartsWith("UnversionedOnly="))
                                isUnversionedOnly = l.Substring(16) == "1";
                        }
                    }
                    catch { }
                }

                if (isUnversionedOnly)
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Unversioned-only mode detected. Restoring files directly from:\n{_snapshotFolder}</color>"));

                    if (Directory.Exists(addedFilesPath))
                    {
                        CopyDirectory(addedFilesPath, root);
                        PostUI(() => SVNLogBridge.LogLine("<color=green>[Snapshot] Unversioned files restored successfully.</color>"));
                    }
                    else
                    {
                        PostUI(() => SVNLogBridge.LogLine("<color=yellow>[Snapshot] No unversioned files backup found.</color>"));
                    }
                }
                else
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Full snapshot detected (Source: {_snapshotFolder}). Pre-flight check: Verifying working copy..."));
                    string currentStatus = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(currentStatus))
                    {
                        PostUI(() => SVNLogBridge.LogLine(
                            "<color=#FF4444><b>[Snapshot] RESTORE BLOCKED</b></color>\n" +
                            "Your working copy is not clean. Restoring now could corrupt your current files.\n\n" +
                            "<b>What to do:</b>\n" +
                            "1. If you want to keep current changes -> Use <b>Shelve</b> first, then restore snapshot.\n" +
                            "2. If you want to discard current changes -> Use <b>Revert</b> first, then restore snapshot."));
                        return false;
                    }

                    PostUI(() => SVNLogBridge.LogLine("<color=green>[Snapshot] Working copy is clean. Applying patch...</color>"));

                    string output = await SvnRunner.RunAsync(
                        $"patch --ignore-whitespace \"{patchPath}\" \"{root}\"",
                        root, true, token).ConfigureAwait(false);

                    string safeOutput = output ?? string.Empty;
                    bool patchFailed = safeOutput.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                                       safeOutput.Contains("failed to apply", StringComparison.OrdinalIgnoreCase) ||
                                       safeOutput.Contains("Can't open file", StringComparison.OrdinalIgnoreCase);

                    if (patchFailed)
                    {
                        string errorMsg = safeOutput.Length > 500
                            ? safeOutput.Substring(0, 500) + "\n... (truncated)"
                            : safeOutput;

                        PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] SVN Patch failed. Details:\n{errorMsg}</color>"));
                        return false;
                    }

                    PostUI(() => SVNLogBridge.LogLine("<color=green>[Snapshot] Patch applied successfully.</color>"));

                    if (Directory.Exists(addedFilesPath))
                    {
                        PostUI(() => SVNLogBridge.LogLine("[Snapshot] Restoring unversioned files..."));
                        CopyDirectory(addedFilesPath, root);
                    }
                }

                PostUI(() => SVNLogBridge.LogLine("[Snapshot] Refreshing workspace..."));

#if UNITY_EDITOR
                PostUI(() => UnityEditor.AssetDatabase.Refresh());
#endif

                await svnManager.RefreshStatus().ConfigureAwait(false);

                PostUI(() => SVNLogBridge.LogLine($"<color=#55FF55>[Snapshot] Restore process completed successfully (from: {_snapshotFolder}).</color>"));
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Snapshot] Restore cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] Restore failed (Source: {_snapshotFolder}): {ex.Message}</color>"));
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                try { cts.Dispose(); } catch { }
                ExitProcessing();
            }
        }

        public SnapshotInfo GetCurrentSnapshotInfo()
        {
            string patchPath = GetSnapshotFilePath();
            string metaPath = GetMetadataPath();

            if (!File.Exists(patchPath))
                return null;

            var info = new SnapshotInfo
            {
                Exists = true,
                FolderPath = _snapshotFolder,
                Date = File.GetLastWriteTime(patchPath),
                SizeBytes = new FileInfo(patchPath).Length
            };

            string filesFolder = GetAddedFilesFolder();
            if (Directory.Exists(filesFolder))
            {
                try
                {
                    info.UnversionedFileCount = Directory.GetFiles(filesFolder, "*", SearchOption.AllDirectories).Length;
                    info.SizeBytes += Directory.GetFiles(filesFolder, "*", SearchOption.AllDirectories)
                                               .Sum(f => new FileInfo(f).Length);
                }
                catch { }
            }

            try
            {
                int fileCount = 0;
                using var reader = new StreamReader(patchPath);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("Index: ")) fileCount++;
                }
                info.TrackedFileCount = fileCount;
            }
            catch { }

            if (File.Exists(metaPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(metaPath);
                    foreach (string l in lines)
                    {
                        if (l.StartsWith("Reason=")) info.Reason = l.Substring(7);
                        else if (l.StartsWith("Tracked=")) info.HasTrackedChanges = l.Substring(8) == "1";
                        else if (l.StartsWith("UnversionedOnly=")) info.IsUnversionedOnly = l.Substring(16) == "1";
                    }
                }
                catch { }
            }

            return info;
        }

        private List<string> ParseUnversionedFiles(string statusOutput, string rootPath)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(statusOutput)) return result;

            foreach (string rawLine in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine.TrimEnd();
                if (line.Length < 8 || line[0] != '?') continue;

                string relativePath = line.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                string fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;

                result.Add(fullPath);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void WriteMetadata(string reason, bool hasTracked, int unversionedCount, bool unversionedOnly = false)
        {
            try
            {
                string metaPath = GetMetadataPath();
                var sb = new StringBuilder();
                sb.AppendLine($"Reason={reason ?? "Manual"}");
                sb.AppendLine($"Date={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Tracked={(hasTracked ? 1 : 0)}");
                sb.AppendLine($"Unversioned={unversionedCount}");
                sb.AppendLine($"UnversionedOnly={(unversionedOnly ? 1 : 0)}");
                File.WriteAllText(metaPath, sb.ToString());
            }
            catch { }
        }

        private string GetSnapshotFilePath() => Path.Combine(_snapshotFolder, SnapshotName + ".patch");
        private string GetAddedFilesFolder() => Path.Combine(_snapshotFolder, SnapshotName + "_Files");
        private string GetMetadataPath() => Path.Combine(_snapshotFolder, SnapshotName + ".meta");

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        public class SnapshotInfo
        {
            public bool Exists;
            public DateTime Date;
            public string Reason;
            public int TrackedFileCount;
            public int UnversionedFileCount;
            public long SizeBytes;
            public bool HasTrackedChanges;
            public bool IsUnversionedOnly;
            public string FolderPath;
        }
    }
}