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
        private readonly SynchronizationContext _mainThreadContext;

        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _shelfFolder = Path.Combine(Application.persistentDataPath, "SVN_Shelves");
            Directory.CreateDirectory(_shelfFolder);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void Cancel() => _cts?.Cancel();

        private bool TryEnterProcessing()
        {
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

        public void ExecuteDeleteShelf(string shelfName)
        {
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;
                PostUI(() => RemoveShelfUI(shelfName));

                try
                {
                    string filePath = GetShelfFilePath(shelfName);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash]</color> Deleted: {shelfName}"));
                    }
                    else
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Stash]</color> Shelf '{shelfName}' not found."));
                    }
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

            using var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string root = svnManager?.WorkingDir;

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=#FF5555>[Stash] Invalid working directory.</color>"));
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

                PostUI(() => SVNLogBridge.LogLine($"[Stash] SVN status:\n{statusOutput}"));
                List<string> unversionedFiles = ParseUnversionedFiles(statusOutput, root);

                PostUI(() => SVNLogBridge.LogLine("[Stash] Creating patch..."));
                string diff = await SvnRunner.RunAsync("diff --git", root, false, token).ConfigureAwait(false);
                bool hasTrackedChanges = !string.IsNullOrWhiteSpace(diff);
                bool hasUnversionedFiles = unversionedFiles.Count > 0;

                if (File.Exists(patchPath)) File.Delete(patchPath);
                if (Directory.Exists(addedFilesPath)) Directory.Delete(addedFilesPath, true);
                Directory.CreateDirectory(addedFilesPath);

                if (hasTrackedChanges)
                {
                    PostUI(() => SVNLogBridge.LogLine("[Stash] Saving tracked changes..."));
                    await File.WriteAllTextAsync(patchPath, diff, token).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(patchPath, string.Empty, token).ConfigureAwait(false);
                }

                if (hasUnversionedFiles)
                {
                    PostUI(() => SVNLogBridge.LogLine($"[Stash] Saving {unversionedFiles.Count} unversioned files..."));
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

                PostUI(() => SVNLogBridge.LogLine("[Stash] Reverting tracked changes..."));
                string revertOutput = await SvnRunner.RunAsync("revert -R .", root, true, token).ConfigureAwait(false);
                PostUI(() => SVNLogBridge.LogLine($"[Stash] Revert result:\n{revertOutput}"));

                foreach (string path in unversionedFiles)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            string metaPath = path + ".meta";
                            if (File.Exists(metaPath)) File.Delete(metaPath);
                        }
                        else if (Directory.Exists(path))
                        {
                            Directory.Delete(path, true);
                            string metaPath = path + ".meta";
                            if (File.Exists(metaPath)) File.Delete(metaPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Could not remove {path}: {ex.Message}</color>"));
                    }
                }

                string finalStatus = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(finalStatus))
                {
                    if (requireCleanWorkingCopy)
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=#FF5555>[Stash] Working copy is still dirty:\n{finalStatus}</color>"));
                        return false;
                    }
                    else
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Working copy still has unversioned items (non‑critical).</color>"));
                    }
                }

                PostUI(() => SVNLogBridge.LogLine($"<color=#55FF55>[Stash] Successfully saved: {shelfName}</color>"));
                CleanupOldPatchFiles();
                return true;
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Stash] Cancelled.</color>"));
                return false;
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FF5555>[Stash] FAILED:\n{ex}</color>"));
                return false;
            }
            finally
            {
                _cts = null;
                ExitProcessing();
            }
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

            using var cts = new CancellationTokenSource();
            _cts = cts;
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

                string output = await SvnRunner.RunAsync($"patch \"{patchPath}\" \"{root}\"", root, true, token).ConfigureAwait(false);
                string safeOutput = output ?? string.Empty;
                bool patchFailed = safeOutput.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
                                   safeOutput.Contains("failed", StringComparison.OrdinalIgnoreCase);

                if (patchFailed)
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=#FFAA00>[Stash] Patch could not be fully restored.</color>"));
                    return false;
                }

                if (Directory.Exists(addedFilesPath))
                    CopyDirectory(addedFilesPath, root);

                try
                {
                    File.Delete(patchPath);
                    if (Directory.Exists(addedFilesPath))
                        Directory.Delete(addedFilesPath, true);
                }
                catch { }

                await svnManager.RefreshStatus().ConfigureAwait(false);
                PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash] Restored: {shelfName}</color>"));
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
                _cts = null;
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
                        ui.NameText.text = "<color=#888888>No shelves found.</color>";
                        if (ui.DateText != null) ui.DateText.text = "";
                        if (ui.FilesLabel != null) ui.FilesLabel.text = "";
                        if (ui.SizeLabel != null) ui.SizeLabel.text = "";
                        ui.RestoreButton.gameObject.SetActive(false);
                        ui.DeleteButton.gameObject.SetActive(false);
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=#888888>No shelves found.</color>");
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
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            int digit = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
            double value = bytes / Math.Pow(1024, digit);
            return value.ToString("F1") + " " + units[digit];
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory)) return;
            Directory.CreateDirectory(destinationDirectory);

            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                File.Copy(file, destinationFile, true);
            }

            foreach (string directory in Directory.GetDirectories(sourceDirectory))
            {
                string destinationSubDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
                CopyDirectory(directory, destinationSubDirectory);
            }
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

        public class ShelfInfo
        {
            public string Name;
            public DateTime Date;
            public int FileCount;
            public long SizeBytes;
        }
    }
}