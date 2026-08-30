using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNShelve : SVNBase, IDisposable
    {
        private readonly string _shelfFolder;
        private CancellationTokenSource _cts;
        private int _processingFlag;
        private int _disposed;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _shelfFolder = Path.Combine(SVNPrefs.PersistentDataPath, "SVN_Shelves");
            Directory.CreateDirectory(_shelfFolder);
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
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Unhandled:</color> {ex.Message}"));
            }
        }

        public void ExecuteShelve()
        {
            SafeFireAndForget(async () =>
            {
                string name = svnUI?.ShelfNameInput?.text;
                if (string.IsNullOrWhiteSpace(name))
                    name = "Stash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                bool success = await Shelve(name).ConfigureAwait(false);
                if (!success) return;

                PostUI(() =>
                {
                    RefreshShelvesUI();
                    if (svnUI?.ShelfNameInput != null)
                        svnUI.ShelfNameInput.text = "";
                });
            });
        }

        // === FIX S2: usuwamy też folder _Files shelfa (wcześniej zostawał orphanem).
        public void ExecuteDeleteShelf(string shelfName)
        {
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;
                PostUI(() => RemoveShelfUI(shelfName));

                try
                {
                    string filePath = GetShelfFilePath(shelfName);
                    string filesFolder = GetAddedFilesFolder(shelfName);
                    bool removedAny = false;

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        removedAny = true;
                    }

                    if (Directory.Exists(filesFolder))
                    {
                        Directory.Delete(filesFolder, true);
                        removedAny = true;
                    }

                    PostUI(() => SVNLogBridge.LogLine(removedAny
                        ? $"<color=green>[Stash]</color> Deleted: {shelfName}"
                        : $"<color=yellow>[Stash]</color> Shelf '{shelfName}' not found."));
                }
                catch (Exception ex)
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>Delete failed:</color> {ex.Message}"));
                }
                finally
                {
                    ExitProcessing();
                    PostUI(() => RefreshShelvesUI());
                }
            });
        }

        public void Button_RefreshShelvesUI() => RefreshShelvesUI();

        public async Task<bool> Shelve(string shelfName, bool requireCleanWorkingCopy = true)
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Stash] Another operation is already running.</color>"));
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
                    PostUI(() => SVNLogBridge.LogLine("<color=#FF9900>[Stash] Invalid working directory.</color>"));
                    return false;
                }

                shelfName = SanitizeShelfName(shelfName);
                string patchPath = GetShelfFilePath(shelfName);
                string addedFilesPath = GetAddedFilesFolder(shelfName);

                PostUI(() => SVNLogBridge.LogLine($"[Stash] Working directory: {root}"));
                PostUI(() => SVNLogBridge.LogLine($"[Stash] Shelf name: {shelfName}"));

                PostUI(() => SVNLogBridge.LogLine("[Stash] Reading SVN status..."));
                string statusOutput = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(statusOutput))
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=yellow>[Stash] No changes detected.</color>"));
                    return true;
                }

                // === FIX (licznik): RemoveEmptyEntries — bez tego liczyło pustą końcówkę.
                int statusLineCount = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                PostUI(() => SVNLogBridge.LogLine($"[Stash] Detected {statusLineCount} changed/untracked items."));

                // === FIX K2: rozbity parsing statusu — osobno '?' (unversioned),
                // osobno 'A' (scheduled adds; revert zdejmie scheduling i zostawi
                // je jako '?' — musimy je zapisać i posprzątać sami).
                var unversionedFiles = new List<string>();
                var addedFiles = new List<string>();
                var addedDirs = new List<string>();
                ParseShelveSets(statusOutput, root, unversionedFiles, addedFiles, addedDirs);

                PostUI(() => SVNLogBridge.LogLine("[Stash] Creating patch..."));
                string diff = await SvnRunner.RunAsync("diff", root, false, token).ConfigureAwait(false);
                bool hasTrackedChanges = !string.IsNullOrWhiteSpace(diff);

                // === FIX K1 (krytyczny): 'svn diff' NIE zawiera treści plików
                // binarnych ("Cannot display: file marked as binary"). Bez tego
                // revert -R niszczył zmiany binarne (sceny/prefaby/tekstury)
                // BEZPOWROTNIE — patch ich nie przywracał. Teraz binaria idą do
                // folderu _Files shelfa, a Unshelve (CopyDirectory _Files → root)
                // przywraca je automatycznie.
                List<string> binaryModified = ParseBinaryModifiedFromDiff(diff, root);
                if (binaryModified.Count > 0)
                {
                    PostUI(() => SVNLogBridge.LogLine(
                        $"<color=yellow>[Stash] {binaryModified.Count} binary file(s) detected (not patchable) — copying directly to shelf.</color>"));
                }

                if (File.Exists(patchPath)) File.Delete(patchPath);
                if (Directory.Exists(addedFilesPath)) Directory.Delete(addedFilesPath, true);
                Directory.CreateDirectory(addedFilesPath);

                if (hasTrackedChanges)
                {
                    PostUI(() => SVNLogBridge.LogLine("[Stash] Saving tracked changes..."));
                    string normalizedDiff = diff.Replace("\r\n", "\n");
                    await File.WriteAllTextAsync(patchPath, normalizedDiff, new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(patchPath, string.Empty, token).ConfigureAwait(false);
                }

                // === FIX K1+K2: preserve-set = unversioned + scheduled-adds + binaria.
                var copySet = unversionedFiles
                    .Concat(addedFiles)
                    .Concat(binaryModified)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (copySet.Count > 0)
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Stash] Saving {copySet.Count} file(s)/folder(s) directly to shelf..."));
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

                PostUI(() => SVNLogBridge.LogLine("[Stash] Reverting tracked changes..."));
                await SvnRunner.RunAsync("revert -R .", root, true, token).ConfigureAwait(false);
                PostUI(() => SVNLogBridge.LogLine("[Stash] Revert completed."));

                // === FIX K2: po revercie pliki 'A' stają się '?' na dysku — usuwamy
                // je RAZEM z unversioned (zawartość bezpiecznie w _Files / w patchu).
                var deleteFromDisk = unversionedFiles
                    .Concat(addedFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (deleteFromDisk.Count > 0)
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Stash] Removing {deleteFromDisk.Count} item(s) from workspace..."));

                    var deleteTcs = new TaskCompletionSource<bool>();

                    PostUI(() =>
                    {
                        try
                        {
                            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                            int failedCount = 0;

                            foreach (string path in deleteFromDisk)
                            {
                                if (!File.Exists(path) && !Directory.Exists(path)) continue;

                                string relToProj = Path.GetRelativePath(projectRoot, path).Replace("\\", "/");
                                bool deleted = false;

#if UNITY_EDITOR
                                if (!string.IsNullOrEmpty(relToProj) && !relToProj.StartsWith(".."))
                                {
                                    deleted = UnityEditor.AssetDatabase.DeleteAsset(relToProj);
                                }
#endif

                                if (!deleted)
                                {
                                    try
                                    {
                                        if (File.Exists(path))
                                        {
                                            File.SetAttributes(path, FileAttributes.Normal);
                                            File.Delete(path);
                                            deleted = true;
                                        }
                                        else if (Directory.Exists(path))
                                        {
                                            ForceClearReadOnly(new DirectoryInfo(path));
                                            Directory.Delete(path, true);
                                            deleted = true;
                                        }

                                        if (deleted)
                                        {
                                            string metaPath = path + ".meta";
                                            if (File.Exists(metaPath))
                                            {
                                                File.SetAttributes(metaPath, FileAttributes.Normal);
                                                File.Delete(metaPath);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        SVNLogBridge.LogWarning($"[Stash] Fallback delete failed for {Path.GetFileName(path)}: {ex.Message}");
                                        failedCount++;
                                    }
                                }
                            }

#if UNITY_EDITOR
                            UnityEditor.AssetDatabase.Refresh();
#endif

                            deleteTcs.SetResult(failedCount == 0);
                        }
                        catch (Exception ex)
                        {
                            deleteTcs.SetException(ex);
                        }
                    });

                    bool allDeleted = await deleteTcs.Task.ConfigureAwait(false);

                    if (!allDeleted)
                    {
                        PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Stash] Warning: Some untracked files could not be deleted (may be locked by IDE).</color>"));
                    }
                }

                // === FIX K2: sprzątanie pustych katalogów po plikach 'A'
                // (inaczej '?'-katalogi psułyby finalny check czystości).
                foreach (string dir in addedDirs.OrderByDescending(d => d.Length))
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { }
                }

                // === FIX S1: finalny check z --ignore-externals (linie 'X' są
                // nieodwracalne przez revert — wcześniej przy projekcie z
                // externals KAŻDY shelve z requireCleanWorkingCopy kończył się
                // "porażką" po wykonaniu destrukcyjnej części).
                string finalStatus = await SvnRunner.RunAsync("status --ignore-externals", root, false, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(finalStatus))
                {
                    int dirtyLines = finalStatus.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (requireCleanWorkingCopy)
                    {
                        // === FIX (komunikat): dane SĄ już zashelfowane i zrevertowane —
                        // stary tekst sugerował totalną porażkę operacji.
                        PostUI(() => SVNLogBridge.LogLine(
                            $"<color=#FF9900>[Stash] Shelf saved, but {dirtyLines} item(s) still remain in working copy — check for leftovers (e.g. locked/ignored files).</color>"));
                        return false;
                    }
                    else
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Working copy still has items (non‑critical).</color>"));
                    }
                }

                PostUI(() => SVNLogBridge.LogLine($"<color=55FF55>[Stash] Successfully saved: {shelfName}</color>"));
                CleanupOldPatchFiles();

                await svnManager.RefreshStatus(force: true).ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Stash] Cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FF9900>[Stash] FAILED:\n{ex}</color>"));
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                try { cts.Dispose(); } catch { }
                ExitProcessing();
            }
        }

        // === FIX K2: jeden przebieg po statusie -> trzy zestawy ścieżek.
        private static void ParseShelveSets(
            string statusOutput,
            string rootPath,
            List<string> unversionedFiles,
            List<string> addedFiles,
            List<string> addedDirs)
        {
            if (string.IsNullOrWhiteSpace(statusOutput)) return;

            foreach (string rawLine in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine.TrimEnd();
                if (line.Length < 8) continue;

                char status = line[0];
                if (status != '?' && status != 'A') continue;

                string relativePath = line.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                string fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));

                if (status == '?')
                {
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;
                    unversionedFiles.Add(fullPath);
                }
                else // 'A'
                {
                    if (Directory.Exists(fullPath))
                        addedDirs.Add(fullPath);
                    else if (File.Exists(fullPath))
                        addedFiles.Add(fullPath);
                    // nieistniejące 'A' (np. po częściowym revert) ignorujemy
                }
            }
        }

        // === FIX K1: wyciąga ścieżki plików binarnych z diffu. Format svn:
        //   Index: Assets/path/file.png
        //   ===================================================================
        //   Cannot display: file marked as binary ...
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
                    pendingIndex = null; // treść tekstowa → to nie binarium
            }

            return result;
        }

        public void ExecuteUnshelve(string selectedShelf)
        {
            SafeFireAndForget(async () =>
            {
                bool success = await Unshelve(selectedShelf).ConfigureAwait(false);
                PostUI(() => RefreshShelvesUI());
            });
        }

        public async Task<bool> Unshelve(string shelfName)
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Stash] Another operation is already running.</color>"));
                return false;
            }

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string root = svnManager?.WorkingDir;
                string patchPath = GetShelfFilePath(shelfName);
                string addedFilesPath = GetAddedFilesFolder(shelfName);

                if (!File.Exists(patchPath))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Shelf not found: {shelfName}</color>"));
                    return false;
                }

                // === FIX S3: ostrzeżenie przy brudnej kopii — patch na zmienionych
                // plikach prawie na pewno da rejecty; user powinien wiedzieć DLACZEGO.
                try
                {
                    string preStatus = await SvnRunner.RunAsync("status --ignore-externals", root, false, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(preStatus))
                        PostUI(() => SVNLogBridge.LogLine(
                            "<color=yellow>[Stash] Warning: working copy has local changes — restore may produce conflicts/rejects.</color>"));
                }
                catch { }

                PostUI(() => SVNLogBridge.LogLine($"[Stash] Restoring tracked changes from '{shelfName}'..."));

                string output = await SvnRunner.RunAsync($"patch --ignore-whitespace \"{patchPath}\" \"{root}\"", root, true, token).ConfigureAwait(false);
                string safeOutput = output ?? string.Empty;

                bool patchFailed = safeOutput.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                                   safeOutput.Contains("failed to apply", StringComparison.OrdinalIgnoreCase) ||
                                   safeOutput.Contains("Can't open file", StringComparison.OrdinalIgnoreCase);

                if (patchFailed)
                {
                    string errorMsg = safeOutput.Length > 500 ? safeOutput.Substring(0, 500) + "\n... (truncated)" : safeOutput;
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] SVN Patch failed. Details:\n{errorMsg}</color>"));
                    return false;
                }

                if (Directory.Exists(addedFilesPath))
                {
                    PostUI(() => SVNLogBridge.LogLine("[Stash] Restoring unversioned/binary files..."));
                    // === FIX K1: binaria wracają tutaj tym samym CopyDirectory.
                    CopyDirectory(addedFilesPath, root);
                }

                try
                {
                    File.Delete(patchPath);
                    if (Directory.Exists(addedFilesPath))
                        Directory.Delete(addedFilesPath, true);
                }
                catch { }

                PostUI(() => SVNLogBridge.LogLine("[Stash] Refreshing workspace..."));
                await svnManager.RefreshStatus().ConfigureAwait(false);

                PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash] Successfully restored: {shelfName}</color>"));
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Stash] Restore cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Restore failed: {ex.Message}</color>"));
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                try { cts.Dispose(); } catch { }
                ExitProcessing();
            }
        }

        private void RemoveShelfUI(string shelfName)
        {
            if (svnUI?.ShelfListContainer == null) return;
            Transform container = svnUI.ShelfListContainer.content;

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                var ui = child.GetComponent<ShelfItemUI>();
                if (ui != null && ui.NameText.text == shelfName)
                {
                    GameObject.Destroy(child.gameObject);
                    return;
                }
            }
        }

        public async Task<List<ShelfInfo>> GetShelvesList()
        {
            return await Task.Run(() =>
            {
                var result = new List<ShelfInfo>();
                try
                {
                    var dirInfo = new DirectoryInfo(_shelfFolder);
                    if (!dirInfo.Exists) return result;

                    foreach (var fileInfo in dirInfo.GetFiles("*.patch"))
                    {
                        var info = new ShelfInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(fileInfo.Name),
                            Date = fileInfo.LastWriteTime,
                            SizeBytes = fileInfo.Length
                        };

                        try
                        {
                            int fileCount = 0;
                            using var reader = new StreamReader(fileInfo.FullName);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.StartsWith("Index: ")) fileCount++;
                            }
                            info.FileCount = fileCount;
                        }
                        catch { info.FileCount = 0; }

                        result.Add(info);
                    }
                }
                catch { }

                return result.OrderByDescending(i => i.Date).ToList();
            }).ConfigureAwait(false);
        }

        public async void RefreshShelvesUI()
        {
            List<ShelfInfo> shelfInfos = await GetShelvesList().ConfigureAwait(false);
            PostUI(() => RefreshShelvesUIInternal(shelfInfos));
        }

        private void RefreshShelvesUIInternal(List<ShelfInfo> shelfInfos)
        {
            if (svnUI?.ShelfListContainer == null) return;
            Transform container = svnUI.ShelfListContainer.content;

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child != null) GameObject.Destroy(child.gameObject);
            }

            if (shelfInfos.Count == 0)
            {
                if (svnUI.ShelfItemPrefab != null)
                {
                    GameObject emptyItem = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);
                    var ui = emptyItem.GetComponent<ShelfItemUI>();
                    if (ui != null)
                    {
                        ui.NameText.text = "<color=yellow>No shelves found.</color>";
                        if (ui.DateText != null) ui.DateText.text = "";
                        if (ui.FilesLabel != null) ui.FilesLabel.text = "";
                        if (ui.SizeLabel != null) ui.SizeLabel.text = "";
                        ui.RestoreButton.gameObject.SetActive(false);
                        ui.DeleteButton.gameObject.SetActive(false);
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>No shelves found.</color>");
                }
            }
            else
            {
                foreach (var info in shelfInfos)
                {
                    if (svnUI.ShelfItemPrefab == null) break;
                    string filePath = GetShelfFilePath(info.Name);
                    if (!File.Exists(filePath))
                    {
                        SVNLogBridge.LogLine($"<color=yellow>[Stash]</color> Stale entry '{info.Name}' ignored.");
                        continue;
                    }

                    GameObject item = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);
                    var ui = item.GetComponent<ShelfItemUI>();
                    if (ui != null)
                    {
                        ui.RestoreButton.onClick.RemoveAllListeners();
                        ui.DeleteButton.onClick.RemoveAllListeners();

                        ui.NameText.text = info.Name;
                        ui.DateText.text = info.Date.ToString("yyyy-MM-dd HH:mm");

                        if (ui.FilesLabel != null)
                            ui.FilesLabel.text = $"Files: {info.FileCount}";
                        if (ui.SizeLabel != null)
                            ui.SizeLabel.text = $"Size: {FormatSize(info.SizeBytes)}";

                        string currentName = info.Name;
                        ui.RestoreButton.onClick.AddListener(() => ExecuteUnshelve(currentName));
                        ui.DeleteButton.onClick.AddListener(() => ExecuteDeleteShelf(currentName));
                    }
                }
            }

            if (container is RectTransform rect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        public void CleanupOldPatchFiles()
        {
            try
            {
                if (!Directory.Exists(_shelfFolder)) return;

                foreach (var file in Directory.EnumerateFiles(_shelfFolder, "*.patch"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                            info.Delete();
                    }
                    catch { }
                }

                foreach (var dir in Directory.EnumerateDirectories(_shelfFolder, "*_Files"))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                            info.Delete(true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private string GetAddedFilesFolder(string shelfName)
        {
            string safeName = SanitizeShelfName(shelfName);
            return Path.Combine(_shelfFolder, safeName + "_Files");
        }

        private string GetShelfFilePath(string name)
        {
            string safeName = SanitizeShelfName(name);
            return Path.Combine(_shelfFolder, safeName + ".patch");
        }

        private static string SanitizeShelfName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return $"Shelf_{DateTime.Now:yyyyMMdd_HHmmss}";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
                builder.Append(invalidChars.Contains(c) ? '_' : c);

            string result = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(result)
                ? $"Shelf_{DateTime.Now:yyyyMMdd_HHmmss}"
                : result;
        }

        private void ForceClearReadOnly(DirectoryInfo directory)
        {
            if (!directory.Exists) return;

            foreach (FileInfo file in directory.GetFiles())
            {
                if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    file.Attributes = FileAttributes.Normal;
                }
            }

            foreach (DirectoryInfo subDir in directory.GetDirectories())
            {
                ForceClearReadOnly(subDir);
            }
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

        public class ShelfInfo
        {
            public string Name;
            public DateTime Date;
            public int FileCount;
            public long SizeBytes;
        }
    }
}