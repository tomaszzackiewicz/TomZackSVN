using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNSnapshot : SVNBase, IDisposable
    {
        private const string SnapshotName = "CurrentSnapshot";

        private readonly string _rootFolder;
        private CancellationTokenSource _cts;
        private int _processingFlag;
        private int _disposed;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNSnapshot(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            // === FIX K2: katalog GŁÓWNY — snapshoty trzymają się w podfolderach
            // per-projekt (patrz GetProjectFolder), żeby restore projektu A nie
            // mógł trafić na working copy projektu B.
            _rootFolder = Path.Combine(SVNPrefs.PersistentDataPath, "SVN_Snapshots");
            Directory.CreateDirectory(_rootFolder);
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

        // === FIX K2: podfolder per-projekt. Kombinacja czytelnej nazwy projektu
        // i krótkiego hasha pełnej ścieżki (ścieżki bywają zbyt długie na nazwę
        // folderu, a same nazwy projektów mogą się powtarzać).
        private string GetProjectFolder()
        {
            string wd = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(wd))
                return _rootFolder; // fallback (przed załadowaniem projektu) — zachowanie legacy

            string normalized = wd.Replace('\\', '/').Trim().TrimEnd('/');

            // === FIX kompilacji: Convert.ToHexString wymaga .NET 5+ (Unity go nie ma)
            // — klasyczny BitConverter.ToString + Replace("-", "").
            string hash;
            using (var md5 = MD5.Create())
            {
                byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()));
                hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 10);
            }

            string projectName = Path.GetFileName(normalized);
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(projectName.Length);
            foreach (char c in projectName)
                sb.Append(invalid.Contains(c) ? '_' : c);
            string safeName = sb.ToString();
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "Project";
            if (safeName.Length > 40) safeName = safeName.Substring(0, 40);

            return Path.Combine(_rootFolder, $"{safeName}_{hash}");
        }

        public void ExecuteCreateSnapshot()
        {
            // === FIX K3: odczyt toggle'a NA MAIN THREAD (tu jesteśmy — przycisk),
            // PRZED fire-and-forget. Wcześniej czytano go przez PostUI (asynchroniczne
            // Post!) i wartość była prawie zawsze fałszywa — tryb unversioned-only
            // praktycznie nie działał.
            bool unversionedOnly = svnUI?.SnapshotUnversionedOnlyToggle?.isOn ?? false;

            SafeFireAndForget(async () =>
            {
                await CreateSnapshotAsync("Manual", unversionedOnly).ConfigureAwait(false);
            });
        }

        public void ExecuteRestoreSnapshot()
        {
            SafeFireAndForget(async () =>
            {
                await RestoreSnapshotAsync().ConfigureAwait(false);
            });
        }

        // === FIX Ś2: guard — delete w trakcie tworzenia/odtwarzania snapshotu
        // mógł skasować pliki pod nogami operacji.
        public void ExecuteDeleteSnapshot()
        {
            if (!TryEnterProcessing())
            {
                SVNLogBridge.LogLine("<color=#FFAA00>[Snapshot] Cannot delete — another snapshot operation is running.</color>");
                return;
            }

            try
            {
                string folder = GetProjectFolder();
                string patchPath = GetSnapshotFilePath();
                string addedFilesPath = GetAddedFilesFolder();
                string metaPath = GetMetadataPath();

                if (File.Exists(patchPath)) File.Delete(patchPath);
                if (Directory.Exists(addedFilesPath)) Directory.Delete(addedFilesPath, true);
                if (File.Exists(metaPath)) File.Delete(metaPath);

                SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Snapshot deleted.\nPath: {folder}</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] Delete failed:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task<bool> CreateSnapshotAsync(string reason = null, bool unversionedOnly = false)
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

                string projectFolder = GetProjectFolder();
                Directory.CreateDirectory(projectFolder);

                string patchPath = Path.Combine(projectFolder, SnapshotName + ".patch");
                string addedFilesPath = Path.Combine(projectFolder, SnapshotName + "_Files");

                PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Creating snapshot ({reason ?? "Manual"})...\nDestination: {projectFolder}"));

                string statusOutput = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(statusOutput))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Working copy is clean. Creating empty snapshot.\nPath: {projectFolder}</color>"));
                }
                else
                {
                    // === FIX: RemoveEmptyEntries (liczyło pustą końcówkę — +1).
                    int statusLineCount = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Detected {statusLineCount} changed/untracked items."));
                }

                List<string> unversionedFiles = ParseUnversionedFiles(statusOutput, root);

                string diff = string.Empty;
                bool hasTrackedChanges = false;
                List<string> binaryModified = new List<string>();

                if (!unversionedOnly)
                {
                    PostUI(() => SVNLogBridge.LogLine("[Snapshot] Generating patch..."));
                    diff = await SvnRunner.RunAsync("diff", root, false, token).ConfigureAwait(false);
                    hasTrackedChanges = !string.IsNullOrWhiteSpace(diff);

                    // === FIX K1: 'svn diff' NIE zawiera treści plików binarnych
                    // ("Cannot display: file marked as binary") — bez tego snapshot
                    // udawał kompletny backup, a sceny/prefaby/tekstury nie były
                    // zapisywane i restore ich NIE przywracał. Teraz binaria idą
                    // do _Files, a restore (CopyDirectory) zwraca je automatycznie.
                    binaryModified = ParseBinaryModifiedFromDiff(diff, root);
                    if (binaryModified.Count > 0)
                        PostUI(() => SVNLogBridge.LogLine(
                            $"<color=yellow>[Snapshot] {binaryModified.Count} binary file(s) detected (not patchable) — backing up directly.</color>"));
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

                // === FIX K1: preserve-set = unversioned + zmodyfikowane binaria.
                var copySet = unversionedFiles
                    .Concat(binaryModified)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (copySet.Count > 0)
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Snapshot] Backing up {copySet.Count} file(s)/folder(s)..."));
                    foreach (string sourcePath in copySet)
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

                PostUI(() => SVNLogBridge.LogLine($"<color=#55FF55>[Snapshot] Successfully created ({reason ?? "Manual"})\nSaved to: {projectFolder}</color>"));
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Snapshot] Cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FF9900>[Snapshot] FAILED:\n{ex}</color>"));
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
                string projectFolder = GetProjectFolder();
                string patchPath = Path.Combine(projectFolder, SnapshotName + ".patch");
                string addedFilesPath = Path.Combine(projectFolder, SnapshotName + "_Files");
                string metaPath = Path.Combine(projectFolder, SnapshotName + ".meta");

                if (!File.Exists(patchPath))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] No snapshot found to restore.\nExpected at: {projectFolder}</color>"));
                    return false;
                }

                // === FIX K2: snapshot w fallbackowym katalogu głównym (legacy /
                // brak WorkingDir) — odmawiamy restore, bo nie możemy zweryfikować
                // przynależności do projektu.
                if (string.IsNullOrWhiteSpace(root) || projectFolder == _rootFolder)
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=#FF9900>[Snapshot] Cannot verify project ownership — load a project first.</color>"));
                    return false;
                }

                bool isUnversionedOnly = false;
                string snapshotWorkingDir = null;
                if (File.Exists(metaPath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(metaPath);
                        foreach (string l in lines)
                        {
                            if (l.StartsWith("UnversionedOnly="))
                                isUnversionedOnly = l.Substring(16) == "1";
                            else if (l.StartsWith("WorkingDir="))
                                snapshotWorkingDir = l.Substring(11).Trim();
                        }
                    }
                    catch { }
                }

                // === FIX K2 (weryfikacja): snapshot z INNEGO projektu = hard stop.
                if (!string.IsNullOrWhiteSpace(snapshotWorkingDir))
                {
                    string currentNorm = root.Replace('\\', '/').Trim().TrimEnd('/');
                    string snapNorm = snapshotWorkingDir.Replace('\\', '/').Trim().TrimEnd('/');
                    if (!string.Equals(currentNorm, snapNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        PostUI(() => SVNLogBridge.LogLine(
                            $"<color=#FF4444><b>[Snapshot] MISMATCH — this snapshot belongs to another project!</b></color>\n" +
                            $"Snapshot: {snapshotWorkingDir}\nCurrent:  {root}"));
                        return false;
                    }
                }

                if (isUnversionedOnly)
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Snapshot] Unversioned-only mode detected. Restoring files directly from:\n{projectFolder}</color>"));

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
                    PostUI(() => SVNLogBridge.LogLine("[Snapshot] Full snapshot detected. Pre-flight check: Verifying working copy..."));

                    // === FIX Ś1: blokujemy TYLKO wersjonowane zmiany. Pełny status
                    // blokował też na '?' (nieversioned — w Unity wszędzie) i 'X'
                    // (externals — nieodwracalne), przez co restore był praktycznie
                    // zawsze zablokowany. '--ignore-externals' + filtr kolumn.
                    string currentStatus = await SvnRunner.RunAsync("status --ignore-externals", root, false, token).ConfigureAwait(false);
                    bool hasVersionedChanges = false;

                    foreach (string rawLine in (currentStatus ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (rawLine.Length < 1) continue;
                        char col0 = rawLine[0];
                        char col1 = rawLine.Length > 1 ? rawLine[1] : ' ';
                        if (col0 == '?' || col0 == 'I' || col0 == 'X') continue;      // nieversioned/ignored/externals — nie przeszkadzają patchowi
                        if (col0 != ' ' || col1 != ' ') { hasVersionedChanges = true; break; }
                    }

                    if (hasVersionedChanges)
                    {
                        PostUI(() => SVNLogBridge.LogLine(
                            "<color=#FF4444><b>[Snapshot] RESTORE BLOCKED</b></color>\n" +
                            "Your working copy has uncommitted VERSIONED changes. Restoring now could corrupt your current files.\n\n" +
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

                        PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] SVN Patch failed (working copy may be partially patched — inspect status!). Details:\n{errorMsg}</color>"));
                        return false;
                    }

                    PostUI(() => SVNLogBridge.LogLine("<color=green>[Snapshot] Patch applied successfully.</color>"));

                    if (Directory.Exists(addedFilesPath))
                    {
                        PostUI(() => SVNLogBridge.LogLine("[Snapshot] Restoring unversioned/binary files..."));
                        // === FIX K1: binaria wracają tutaj (CopyDirectory nadpisuje
                        // wersjonowane pliki zmodyfikowanymi kopiami ze snapshotu).
                        CopyDirectory(addedFilesPath, root);
                    }
                }

                PostUI(() => SVNLogBridge.LogLine("[Snapshot] Refreshing workspace..."));

#if UNITY_EDITOR
                PostUI(() => UnityEditor.AssetDatabase.Refresh());
#endif

                await svnManager.RefreshStatus().ConfigureAwait(false);

                PostUI(() => SVNLogBridge.LogLine($"<color=#55FF55>[Snapshot] Restore process completed successfully (from: {projectFolder}).</color>"));
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Snapshot] Restore cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Snapshot] Restore failed: {ex.Message}</color>"));
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

            if (!File.Exists(patchPath))
                return null;

            var info = new SnapshotInfo
            {
                Exists = true,
                FolderPath = GetProjectFolder(),
                Date = File.GetLastWriteTime(patchPath),
                SizeBytes = new FileInfo(patchPath).Length
            };

            string filesFolder = GetAddedFilesFolder();
            if (Directory.Exists(filesFolder))
            {
                try
                {
                    // === FIX: jeden przebieg (dwa GetFiles AllDirectories = podwójny
                    // pełny skan folderu).
                    var files = Directory.GetFiles(filesFolder, "*", SearchOption.AllDirectories);
                    info.UnversionedFileCount = files.Length;
                    foreach (string f in files)
                    {
                        try { info.SizeBytes += new FileInfo(f).Length; } catch { }
                    }
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

            string metaPath = GetMetadataPath();
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

        // === FIX K1: te same reguły co w SVNShelve — ścieżka z "Index: ", treść
        // "Cannot display: file marked as binary" → plik do backupu bezpośredniego.
        private static List<string> ParseBinaryModifiedFromDiff(string diff, string root)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(diff)) return result;

            string pendingIndex = null;
            foreach (string rawLine in diff.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith("Index: ", StringComparison.Ordinal))
                {
                    pendingIndex = line.Substring("Index: ".Length).Trim();
                    continue;
                }

                if (pendingIndex != null &&
                    line.IndexOf("Cannot display: file marked as binary", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string fullPath = Path.GetFullPath(Path.Combine(root, pendingIndex.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(fullPath))
                        result.Add(fullPath);
                    pendingIndex = null;
                    continue;
                }

                if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                    pendingIndex = null;
            }

            return result;
        }

        // === FIX K2: metadane zapisują WorkingDir — weryfikacja przy restorze.
        private void WriteMetadata(string reason, bool hasTracked, int unversionedCount, bool unversionedOnly = false)
        {
            try
            {
                string metaPath = GetMetadataPath();
                var sb = new StringBuilder();
                sb.AppendLine($"Reason={reason ?? "Manual"}");
                sb.AppendLine($"Date={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"WorkingDir={svnManager?.WorkingDir ?? ""}");
                sb.AppendLine($"Tracked={(hasTracked ? 1 : 0)}");
                sb.AppendLine($"Unversioned={unversionedCount}");
                sb.AppendLine($"UnversionedOnly={(unversionedOnly ? 1 : 0)}");
                File.WriteAllText(metaPath, sb.ToString());
            }
            catch { }
        }

        private string GetSnapshotFilePath()
        {
            string folder = GetProjectFolder();
            return Path.Combine(folder, SnapshotName + ".patch");
        }

        private string GetAddedFilesFolder()
        {
            string folder = GetProjectFolder();
            return Path.Combine(folder, SnapshotName + "_Files");
        }

        private string GetMetadataPath()
        {
            string folder = GetProjectFolder();
            return Path.Combine(folder, SnapshotName + ".meta");
        }

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