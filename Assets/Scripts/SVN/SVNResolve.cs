using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public class SVNResolve : SVNBase
    {
        private readonly SemaphoreSlim _resolveLock = new(1, 1);
        private int _processingFlag;
        private int _refreshingFlag;
        private int _uiRefreshingFlag;

        public enum SVNConflictType { Text, Manual, Tree }
        public enum SVNConflictState { Pending, ManualEditing, Resolving, Resolved }

        public class SVNConflictData
        {
            public string Path;
            public SVNConflictType Type;
            public SVNConflictState State;
        }

        private readonly Dictionary<string, SVNConflictData> _conflictCache = new();

        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        #region Logging

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI?.ResolveLogConsole != null)
                SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
        }

        #endregion

        #region Thread-Safe Flags

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

        private bool TryEnterRefreshing() => Interlocked.Exchange(ref _refreshingFlag, 1) == 0;
        private void ExitRefreshing() => Interlocked.Exchange(ref _refreshingFlag, 0);

        private bool TryEnterUiRefresh() => Interlocked.Exchange(ref _uiRefreshingFlag, 1) == 0;
        private void ExitUiRefresh() => Interlocked.Exchange(ref _uiRefreshingFlag, 0);

        #endregion

        #region Public API (Unity-safe wrappers)

        public void AutoRefreshConflictList() => SafeFireAndForget(AutoRefreshConflictListAsync);
        public void MarkAsResolved() => SafeFireAndForget(MarkAsResolvedAsync);
        public void DeleteAllObstructions() => SafeFireAndForget(DeleteAllObstructionsAsync);
        public void ResolveTheirs() => SafeFireAndForget(() => ResolveBatchAsync("theirs-full"));
        public void ResolveMine() => SafeFireAndForget(() => ResolveBatchAsync("mine-full"));
        public void OpenInEditor() => SafeFireAndForget(OpenInEditorAsync);
        public void ResolveAllMine() => SafeFireAndForget(() => ResolveAllAsync("mine-full"));
        public void ResolveAllTheirs() => SafeFireAndForget(() => ResolveAllAsync("theirs-full"));

        private async void SafeFireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        #endregion

        #region Core Operations

        private async Task AutoRefreshConflictListAsync()
        {
            if (!TryEnterRefreshing()) return;

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    LogBoth("<color=#FFAA00>No working directory.</color>");
                    return;
                }

                await Task.Delay(120).ConfigureAwait(false);

                var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);

                LogBoth(conflicts.Count > 0
                    ? $"<b>[Resolve]</b> Detected conflicts: {conflicts.Count}"
                    : "<b>[Resolve]</b> No conflicts");

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Refresh conflict list failed:</color> {ex.Message}");
            }
            finally
            {
                ExitRefreshing();
            }
        }

        public async Task ResolveSingleMine(string path) => await ResolveSingleAsync(path, "mine-full").ConfigureAwait(false);
        public async Task ResolveSingleTheirs(string path) => await ResolveSingleAsync(path, "theirs-full").ConfigureAwait(false);

        private async Task ResolveBatchAsync(string strategy)
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                await ResolveManyAsync(conflicts, strategy).ConfigureAwait(false);
                LogBoth($"<color=green>{strategy} batch resolved.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task ResolveAllAsync(string strategy)
        {
            if (!TryEnterProcessing()) return;

            try
            {
                var paths = GetActiveConflictPaths();
                await ResolveManyAsync(paths.Select(p => new SVNConflictData { Path = p }), strategy).ConfigureAwait(false);
                LogBoth($"<color=green>All conflicts resolved ({strategy}).</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task MarkAsResolvedAsync()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);

                if (conflicts.Count == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                foreach (var c in conflicts)
                {
                    string full = Path.Combine(svnManager.WorkingDir, c.Path);
                    if (File.Exists(full) && await HasConflictMarkersAsync(full).ConfigureAwait(false))
                    {
                        LogBoth($"<color=#FFAA00>Abort:</color> {c.Path} still has conflict markers");
                        return;
                    }
                }

                await ResolveManyAsync(conflicts, "working").ConfigureAwait(false);
                LogBoth("<color=green>Marked as resolved.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task DeleteAllObstructionsAsync()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);

                if (conflicts == null || conflicts.Count == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                LogBoth($"[Batch Resolve] {conflicts.Count} items");

                foreach (var c in conflicts.Where(x => x.Type == SVNConflictType.Tree))
                {
                    await DeleteObstructionAsync(c.Path, refreshUi: false).ConfigureAwait(false);
                }

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }

                LogBoth("<color=green>All obstructions removed.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>DeleteAllObstructions error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task OpenInEditorAsync()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string root = svnManager.WorkingDir;
                string editorPath = svnManager.MergeToolPath;

                if (string.IsNullOrEmpty(editorPath))
                {
                    editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        LogBoth("<color=#FFAA00>Error:</color> Merge tool path is not set!");
                        return;
                    }
                }

                string targetFile = null;

                if (svnUI?.ResolveTargetFileInput != null &&
                    !string.IsNullOrWhiteSpace(svnUI.ResolveTargetFileInput.text))
                {
                    targetFile = NormalizePath(svnUI.ResolveTargetFileInput.text.Trim());
                }
                else
                {
                    var first = _conflictCache.Values
                        .OrderBy(x => x.Path)
                        .FirstOrDefault(x => x.State != SVNConflictState.Resolved);

                    if (first != null) targetFile = first.Path;
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);
                    foreach (var c in conflicts)
                    {
                        if (_conflictCache.TryGetValue(c.Path, out var cachedEntry))
                            cachedEntry.Type = c.Type;
                        else
                            _conflictCache[c.Path] = c;
                    }
                    targetFile = conflicts.FirstOrDefault()?.Path;
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    LogBoth("<color=yellow>No conflicted file found to open.</color>");
                    return;
                }

                string fullPath = Path.Combine(root, targetFile);
                if (!File.Exists(fullPath))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {targetFile}");
                    return;
                }

                LogBoth($"Opening editor: <color=green>{targetFile}</color>");

                var startInfo = new ProcessStartInfo(editorPath, $"\"{fullPath}\"");
                using (Process.Start(startInfo)) { }

                var data = _conflictCache.TryGetValue(targetFile, out var conflictData) ? conflictData : new SVNConflictData { Path = targetFile };
                data.Type = SVNConflictType.Manual;
                data.State = SVNConflictState.ManualEditing;
                _conflictCache[targetFile] = data;

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Exception:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        #endregion

        #region Data Layer

        private async Task<List<SVNConflictData>> GetConflictsAsync(string root)
        {
            try
            {
                string xml = await SvnRunner.RunAsync("status --xml", root, false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(xml))
                {
                    LogBoth("<color=#FFAA00>[Resolve] Empty SVN XML</color>");
                    return new List<SVNConflictData>();
                }

                var result = new List<SVNConflictData>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var stringReader = new StringReader(xml))
                using (var reader = XmlReader.Create(stringReader, new XmlReaderSettings { Async = true }))
                {
                    string currentPath = null;
                    string item = null;
                    string props = null;
                    string tree = null;

                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "entry":
                                    currentPath = reader.GetAttribute("path");
                                    item = props = tree = null;
                                    break;
                                case "wc-status":
                                    item = reader.GetAttribute("item");
                                    props = reader.GetAttribute("props");
                                    tree = reader.GetAttribute("tree-conflicted");
                                    break;
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "entry")
                        {
                            bool isConflict = item == "conflicted" || props == "conflicted" || tree == "true";
                            if (isConflict && !string.IsNullOrWhiteSpace(currentPath))
                            {
                                string path = NormalizePath(currentPath);
                                if (seen.Add(path))
                                {
                                    var type = tree == "true" ? SVNConflictType.Tree : SVNConflictType.Text;
                                    if (_conflictCache.TryGetValue(path, out var cached) && cached.State == SVNConflictState.ManualEditing)
                                        type = SVNConflictType.Manual;

                                    var data = new SVNConflictData
                                    {
                                        Path = path,
                                        Type = type,
                                        State = _conflictCache.TryGetValue(path, out var old) ? old.State : SVNConflictState.Pending
                                    };
                                    _conflictCache[path] = data;
                                    result.Add(data);
                                }
                            }
                        }
                    }
                }

                var valid = new HashSet<string>(result.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var key in _conflictCache.Keys.ToList())
                {
                    if (!valid.Contains(key)) _conflictCache.Remove(key);
                }

                return result.OrderBy(x => x.Path).ToList();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>GetConflicts error:</color> {ex.Message}");
                return new List<SVNConflictData>();
            }
        }

        private async Task<bool> HasConflictMarkersAsync(string fullPath)
        {
            if (!File.Exists(fullPath)) return false;

            using var stream = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
            string line;
            while ((line = await stream.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Contains("<<<<<<<") || line.Contains("=======") || line.Contains(">>>>>>>"))
                    return true;
            }
            return false;
        }

        private async Task ResolveManyAsync(IEnumerable<SVNConflictData> conflicts, string strategy)
        {
            if (conflicts == null) return;

            var snapshot = conflicts
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                .Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in snapshot)
            {
                await ResolveSingleAsync(path, strategy).ConfigureAwait(false);
            }

            await Task.Delay(150).ConfigureAwait(false);

            var latest = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
            _conflictCache.Clear();
            foreach (var c in latest) _conflictCache[c.Path] = c;

            if (TryEnterUiRefresh())
            {
                try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                finally { ExitUiRefresh(); }
            }
        }

        private async Task ResolveSingleAsync(string path, string strategy)
        {
            path = NormalizePath(path);
            await _resolveLock.WaitAsync().ConfigureAwait(false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                LogBoth($"[Resolve] {strategy} -> {path}");

                if (_conflictCache.TryGetValue(path, out var data))
                    data.State = SVNConflictState.Resolving;

                string full = Path.Combine(svnManager.WorkingDir, path);

                if (strategy == "theirs-full")
                {
                    try
                    {
                        if (File.Exists(full)) File.Delete(full);
                        else if (Directory.Exists(full)) Directory.Delete(full, true);
                    }
                    catch { /* best effort */ }
                }

                await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup --remove-unversioned", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);

                _conflictCache.Remove(path);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await Task.Delay(120).ConfigureAwait(false);

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);

                LogBoth($"<color=green>Resolved ({strategy}):</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Error resolving {path}:</color> {ex.Message}");
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        public async Task OpenSingle(string path)
        {
            if (Interlocked.CompareExchange(ref _processingFlag, 1, 0) == 1) return;

            string editorPath = svnManager.MergeToolPath;
            if (string.IsNullOrEmpty(editorPath))
            {
                editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                if (string.IsNullOrEmpty(editorPath))
                {
                    LogBoth("<color=#FFAA00>Merge tool path missing!</color>");
                    return;
                }
            }

            try
            {
                string full = Path.Combine(svnManager.WorkingDir, path);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {path}");
                    return;
                }

                LogBoth($"Opening editor for: {path}");
                using (Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\""))) { }

                if (_conflictCache.TryGetValue(path, out var conflict))
                {
                    conflict.Type = SVNConflictType.Manual;
                    conflict.State = SVNConflictState.ManualEditing;
                }

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>{ex.Message}</color>");
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        public async Task MarkSingleResolved(string path)
        {
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string fullPath = Path.Combine(svnManager.WorkingDir, path);
                if (File.Exists(fullPath) && await HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
                {
                    LogBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}");
                    return;
                }

                LogBoth($"[Resolve] Finalizing: {path}");
                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup --remove-unversioned", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                _conflictCache.Remove(path);
                await Task.Delay(150).ConfigureAwait(false);

                if (TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }

                await svnManager.RefreshStatus().ConfigureAwait(false);
                LogBoth($"<color=green>Resolved manually:</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Error finalizing {path}:</color> {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        /// <summary>
        /// Backward-compatible wrapper dla istniejących skryptów (SVNConflictItem, SVNMerge, SVNUpdate, SVNManager).
        /// </summary>
        public async Task DeleteObstruction(string path, bool refreshUi = true)
        {
            await DeleteObstructionAsync(path, refreshUi).ConfigureAwait(false);
        }

        public async Task DeleteObstructionAsync(string path, bool refreshUi = true)
        {
            await _resolveLock.WaitAsync().ConfigureAwait(false);

            try
            {
                path = NormalizePath(path);
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string fullPath = Path.Combine(svnManager.WorkingDir, path);
                bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

                LogBoth($"[TREE RESOLVE] {path} (exists: {fileExists})");

                if (fileExists)
                {
                    LogBoth("[TREE RESOLVE] File exists locally – removing physical file...");

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }

                    bool removed = false;
                    try
                    {
                        if (File.Exists(fullPath))
                        {
                            File.SetAttributes(fullPath, FileAttributes.Normal);
                            File.Delete(fullPath);
                            removed = true;
                        }
                        else if (Directory.Exists(fullPath))
                        {
                            var di = new DirectoryInfo(fullPath);
                            di.Attributes = FileAttributes.Normal;
                            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
                                file.Attributes = FileAttributes.Normal;
                            Directory.Delete(fullPath, true);
                            removed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=yellow>Physical delete failed:</color> {ex.Message} – using svn delete --force");
                    }

                    if (!removed)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"delete \"{path}\" --force --keep-local", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                            if (File.Exists(fullPath)) File.Delete(fullPath);
                            else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                            LogBoth("[TREE RESOLVE] Removed via svn delete --force.");
                        }
                        catch (Exception ex2)
                        {
                            LogBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex2.Message}");
                        }
                    }

                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    LogBoth("[TREE RESOLVE] File missing locally – restoring from server...");

                    try
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                        LogBoth("[TREE RESOLVE] File restored via svn revert.");
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=yellow>Revert failed:</color> {ex.Message} – trying theirs-full...");
                        try
                        {
                            await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex2)
                        {
                            LogBoth($"<color=#FFAA00>theirs-full failed:</color> {ex2.Message}");
                        }
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                _conflictCache.Remove(path);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);

                if (refreshUi && TryEnterUiRefresh())
                {
                    try { await RefreshConflictUIAsync().ConfigureAwait(false); }
                    finally { ExitUiRefresh(); }
                }

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);
                LogBoth($"<color=green>Tree conflict resolved:</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Tree resolve error:</color> {ex.Message}");
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        #endregion

        #region UI Layer

        /// <summary>
        /// Backward-compatible wrapper dla istniejących skryptów (SVNMerge, SVNUpdate, SVNManager).
        /// </summary>
        public async Task RefreshConflictUI()
        {
            await RefreshConflictUIAsync().ConfigureAwait(false);
        }

        public async Task RefreshConflictUIAsync()
        {
            if (svnUI?.ResolveConsoleContent == null || svnUI.ConflictPrefab == null) return;

            var root = svnManager.WorkingDir;
            var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);

            await Task.Yield();

            var parent = svnUI.ResolveConsoleContent.transform;

            for (int i = parent.childCount - 1; i >= 0; i--)
                GameObject.Destroy(parent.GetChild(i).gameObject);

            await Task.Yield();

            foreach (var c in conflicts)
            {
                bool markers = await HasConflictMarkersAsync(Path.Combine(root, c.Path)).ConfigureAwait(false);
                await Task.Yield();

                var obj = GameObject.Instantiate(svnUI.ConflictPrefab, parent);
                obj.SetActive(true);

                var item = obj.GetComponent<SVNConflictItem>();
                if (item == null)
                {
                    LogBoth("<color=#FFAA00>SVNConflictItem missing!</color>");
                    continue;
                }

                item.Setup(c.Path, ConvertConflictType(c.Type), markers);
            }

            Canvas.ForceUpdateCanvases();
            LogBoth($"[Resolve UI] Generated {conflicts.Count} conflict items.");
        }

        private SVNConflictItem.ConflictType ConvertConflictType(SVNConflictType type) => type switch
        {
            SVNConflictType.Manual => SVNConflictItem.ConflictType.Manual,
            SVNConflictType.Tree => SVNConflictItem.ConflictType.Tree,
            _ => SVNConflictItem.ConflictType.Text,
        };

        #endregion

        #region Helpers

        private List<string> GetActiveConflictPaths()
        {
            return _conflictCache.Values
                .Where(x => x != null)
                .Select(x => x.Path)
                .ToList();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim();
        }

        #endregion
    }
}