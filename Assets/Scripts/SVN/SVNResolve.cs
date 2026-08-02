using System;
using System.Collections.Concurrent;
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
    public class SVNResolve : SVNBase, IDisposable
    {
        private readonly SemaphoreSlim _resolveLock = new(1, 1);
        private int _processingFlag;
        private int _refreshingFlag;
        private int _uiRefreshingFlag;
        private bool _obstructionsJustDeleted;

        public enum SVNConflictType { Text, Manual, Tree }
        public enum SVNConflictState { Pending, ManualEditing, Resolving, Resolved }

        public class SVNConflictData
        {
            public string Path;
            public SVNConflictType Type;
            public SVNConflictState State;
        }

        private readonly ConcurrentDictionary<string, SVNConflictData> _conflictCache = new(StringComparer.OrdinalIgnoreCase);

        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                SVNLogBridge.LogLine(msg);
                if (svnUI?.ResolveLogConsole != null)
                    SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
            });
        }

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

        public void AutoRefreshConflictList() => SafeFireAndForget(AutoRefreshConflictListAsync);
        public void MarkAsResolved() => SafeFireAndForget(MarkAsResolvedAsync);
        public void DeleteAllObstructions() => SafeFireAndForget(DeleteAllObstructionsAsync);
        public void ResolveTheirs() => SafeFireAndForget(() => ResolveBatchAsync("theirs-full"));
        public void ResolveMine() => SafeFireAndForget(() => ResolveBatchAsync("mine-full"));
        public void OpenInEditor() => SafeFireAndForget(OpenInEditorAsync);
        public void ResolveAllMine() => SafeFireAndForget(() => ResolveAllAsync("mine-full"));
        public void ResolveAllTheirs() => SafeFireAndForget(() => ResolveAllAsync("theirs-full"));

        public async Task ResolveSingleMine(string path)
        {
            await ResolveSingleSilentAsync(path, "mine-full").ConfigureAwait(false);
            await RefreshConflictUIAsync().ConfigureAwait(false);
            await RefreshMainUIAfterResolve().ConfigureAwait(false);
        }

        public async Task ResolveSingleTheirs(string path)
        {
            await ResolveSingleSilentAsync(path, "theirs-full").ConfigureAwait(false);
            await RefreshConflictUIAsync().ConfigureAwait(false);
            await RefreshMainUIAfterResolve().ConfigureAwait(false);
        }

        public async Task RefreshConflictUI() => await RefreshConflictUIAsync().ConfigureAwait(false);

        private static void SafeFireAndForget(Func<Task> operation) => RunWithExceptionShield(operation);
        private static async void RunWithExceptionShield(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { SVNLogBridge.LogException(ex); }
        }

        private string GetResolveToolPath()
        {
            string path = svnManager?.ResolveToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = svnUI?.SettingsResolveToolPathInput?.text;
            if (string.IsNullOrWhiteSpace(path))
                path = PlayerPrefs.GetString(SVNManager.KEY_RESOLVE_TOOL, "");

            return path?.Trim().Replace("\"", "");
        }

        private bool TryLaunchExternalResolveTool(string conflictedFullPath, string relativePath)
        {
            string toolPath = GetResolveToolPath();

            if (string.IsNullOrEmpty(toolPath) || !File.Exists(toolPath))
                return false;

            try
            {
                string dir = Path.GetDirectoryName(conflictedFullPath);
                string fileName = Path.GetFileName(conflictedFullPath);

                string mineFile = conflictedFullPath + ".mine";

                var revFiles = Directory.GetFiles(dir, $"{fileName}.r*")
                                        .Where(f => !f.EndsWith(".mine"))
                                        .OrderBy(f => f).ToList();

                if (revFiles.Count < 2 || !File.Exists(mineFile))
                {
                    LogBoth($"<color=#FFAA00>Resolve Tool:</color> Missing .mine or .r* files for 3-way merge. Falling back.");
                    return false;
                }

                string baseFile = revFiles.First();  // (Base)
                string theirsFile = revFiles.Last(); // (Theirs)

                string processArgs;

                if (toolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    processArgs = $"/base:\"{baseFile}\" /mine:\"{mineFile}\" /theirs:\"{theirsFile}\" /merged:\"{conflictedFullPath}\"";
                }
                else
                {
                    processArgs = $"\"{baseFile}\" \"{mineFile}\" \"{theirsFile}\" \"{conflictedFullPath}\"";
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = toolPath,
                    Arguments = processArgs,
                    UseShellExecute = true
                });

                LogBoth($"<color=cyan>Launched 3-way resolve tool for:</color> {relativePath}");
                return true;
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>External resolve tool failed:</color> {ex.Message}. Falling back.");
                return false;
            }
        }

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

                await RefreshConflictUIAsync();
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Refresh conflict list failed:</color> {ex.Message}"); }
            finally { ExitRefreshing(); }
        }

        private async Task ResolveSingleSilentAsync(string path, string strategy)
        {
            path = NormalizePath(path);
            await _resolveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                LogBoth($"[Resolve] {strategy} -> {path}");

                if (_conflictCache.TryGetValue(path, out var data))
                    data.State = SVNConflictState.Resolving;

                if (path.Contains("\""))
                {
                    LogBoth($"<color=#FFAA00>Invalid path (contains quotes):</color> {path}");
                    return;
                }

                bool resolved = false;
                string actualResolutionMethod = strategy;

                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                    resolved = true;
                }
                catch (Exception ex) when (ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
                {
                    string fallbackStrategy = strategy.Replace("-full", "-conflict");
                    LogBoth($"<color=#FFFF00>[Fallback 1]</color> Standard failed. Trying: {fallbackStrategy}");

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                        resolved = true;
                        actualResolutionMethod = fallbackStrategy;
                    }
                    catch (Exception ex2) when (ex2.Message.Contains("W195024") || ex2.Message.Contains("E155027"))
                    {
                        if (strategy.Equals("working", StringComparison.OrdinalIgnoreCase))
                        {
                            LogBoth($"<color=#FF0000>[Blocked]</color> Cannot force 'working' resolution via revert. Manual intervention required for: {path}");
                            throw;
                        }

                        LogBoth($"<color=#FFFF00>[Fallback 2]</color> {fallbackStrategy} failed. Forcing state reset via svn revert...");

                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                            resolved = true;

                            actualResolutionMethod = "state reset via revert (BASE)";
                        }
                        catch (Exception ex3)
                        {
                            LogBoth($"<color=#FF0000>[Failed]</color> Revert also failed: {ex3.Message}");
                        }
                    }
                }

                if (resolved)
                {
                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                    _conflictCache.TryRemove(path, out _);
                    svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);
                    LogBoth($"<color=green>Resolved ({actualResolutionMethod}):</color> {path}");
                }
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Error resolving {path}:</color> {ex.Message}"); }
            finally { _resolveLock.Release(); }
        }

        private async Task ResolveManyAsync(IEnumerable<SVNConflictData> conflicts, string strategy)
        {
            if (conflicts == null) return;
            var snapshot = conflicts.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                                    .Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var path in snapshot)
                await ResolveSingleSilentAsync(path, strategy).ConfigureAwait(false);

            await Task.Delay(150).ConfigureAwait(false);
            _conflictCache.Clear();
            var latest = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
            foreach (var c in latest) _conflictCache[c.Path] = c;

            await RefreshConflictUIAsync();
            await RefreshMainUIAfterResolve().ConfigureAwait(false);

            await svnManager.RefreshStatus().ConfigureAwait(false);
        }

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
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Error:</color> {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        private async Task ResolveAllAsync(string strategy)
        {
            if (!TryEnterProcessing()) return;
            try
            {
                var paths = GetActiveConflictPaths().ToList();
                int initialCount = paths.Count;

                await ResolveManyAsync(paths.Select(p => new SVNConflictData { Path = p }), strategy).ConfigureAwait(false);

                int remainingCount = GetActiveConflictPaths().Count;

                if (remainingCount == 0)
                    LogBoth($"<color=green>All conflicts resolved ({strategy}).</color>");
                else
                    LogBoth($"<color=#FFAA00>Resolved {initialCount - remainingCount} of {initialCount} conflicts ({strategy}). {remainingCount} could not be resolved automatically.</color>");
            }
            finally { ExitProcessing(); }
        }

        private async Task MarkAsResolvedAsync()
        {
            if (!TryEnterProcessing()) return;
            try
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                if (conflicts.Count == 0) { LogBoth("<color=yellow>No conflicts found.</color>"); return; }

                var clean = new List<SVNConflictData>();
                var blocked = new List<SVNConflictData>();
                foreach (var c in conflicts)
                {
                    string full = Path.Combine(svnManager.WorkingDir, c.Path);
                    if (File.Exists(full) && await HasConflictMarkersAsync(full).ConfigureAwait(false))
                        blocked.Add(c);
                    else clean.Add(c);
                }

                if (clean.Count > 0)
                {
                    await ResolveManyAsync(clean, "working").ConfigureAwait(false);
                    LogBoth("<color=green>Marked as resolved.</color>");
                }
                else
                {
                    LogBoth("<color=yellow>No files were marked. All conflicts still contain markers or are tree conflicts.</color>");
                }

                if (blocked.Count > 0)
                {
                    LogBoth($"<color=#FFAA00>{blocked.Count} file(s) still contain conflict markers – not marked.</color>");
                    foreach (var c in blocked) LogBoth($"<color=#FFAA00>  • {c.Path}</color>");
                }
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Error:</color> {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        private async Task DeleteAllObstructionsAsync()
        {
            if (!TryEnterProcessing()) return;
            try
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                if (conflicts == null || conflicts.Count == 0) { LogBoth("<color=yellow>No conflicts found.</color>"); return; }

                var treeConflicts = conflicts.Where(x => x.Type == SVNConflictType.Tree).ToList();

                if (treeConflicts.Count == 0)
                {
                    LogBoth("<color=yellow>No tree obstructions found to delete.</color>");
                    return;
                }

                LogBoth("<color=#FF4444><b>====================================</b></color>");
                LogBoth("<color=#FF4444><b>DELETING OBSTRUCTIONS (TREE CONFLICTS)</b></color>");
                LogBoth("<color=#FFAA00>This will force the removal of conflict metadata from SVN.</color>");
                LogBoth("<color=#FFAA00>WARNING: This does NOT cancel the merge or restore files from the server!</color>");
                LogBoth("<color=#FFAA00>Doing this after a failed 'Cancel Local Merge' creates a DANGEROUS state (Dirty State).</color>");
                LogBoth("<color=#FF4444><b>====================================</b></color>");

                await Task.Delay(1500).ConfigureAwait(false);

                foreach (var c in treeConflicts)
                    await DeleteObstructionAsync(c.Path, refreshUi: false).ConfigureAwait(false);

                await RefreshConflictUIAsync();
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                LogBoth("<color=green>All obstructions removed from SVN metadata.</color>");
                _obstructionsJustDeleted = true;

                LogBoth("<color=#FF4444><b>====================================</b></color>");
                LogBoth("<color=#FF4444><b>IMMEDIATE ACTION REQUIRED</b></color>");
                LogBoth("<color=white>Your working copy is now out of sync with SVN history.</color>");
                LogBoth("<color=white>You MUST perform one of the following actions to fix the project:</color>");
                LogBoth(" ");
                LogBoth("<color=#00FF00><b>1. REVERT TO HEAD (Recommended):</b></color> <color=white>If you simply want to safely cancel the broken merge and return to a clean state.</color>");
                LogBoth(" ");
                LogBoth("<color=#00FF00><b>2. COMMIT:</b></color> <color=white>Only if you manually reviewed the files and are sure this state is correct. It will record this 'half-merge' in history.</color>");
                LogBoth(" ");
                LogBoth("<color=#FF0000><b>DO NOT:</b> Click 'Cancel Local Merge' or 'Update' again without understanding the current state!</color>");
                LogBoth("<color=#FF4444><b>====================================</b></color>");
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>DeleteAllObstructions error:</color> {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        private async Task OpenInEditorAsync()
        {
            if (!TryEnterProcessing()) return;
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string root = svnManager.WorkingDir;

                string targetFile = null;
                if (svnUI?.ResolveTargetFileInput != null && !string.IsNullOrWhiteSpace(svnUI.ResolveTargetFileInput.text))
                    targetFile = NormalizePath(svnUI.ResolveTargetFileInput.text.Trim());
                else
                {
                    var first = _conflictCache.Values.OrderBy(x => x.Path).FirstOrDefault(x => x.State != SVNConflictState.Resolved);
                    if (first != null) targetFile = first.Path;
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);
                    foreach (var c in conflicts) _conflictCache.AddOrUpdate(c.Path, c, (_, __) => c);
                    targetFile = conflicts.FirstOrDefault()?.Path;
                }

                if (string.IsNullOrEmpty(targetFile)) { LogBoth("<color=yellow>No conflicted file found to open.</color>"); return; }

                string fullPath = Path.Combine(root, targetFile);
                if (!File.Exists(fullPath)) { LogBoth($"<color=#FFAA00>File not found:</color> {targetFile}"); return; }

                if (!TryLaunchExternalResolveTool(fullPath, targetFile))
                {
                    string editorPath = svnManager.MergeToolPath;
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        editorPath = PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
                        if (string.IsNullOrEmpty(editorPath)) { LogBoth("<color=#FFAA00>Error:</color> Merge tool path is not set!"); return; }
                    }

                    LogBoth($"Opening editor: <color=green>{targetFile}</color>");

                    var startInfo = new ProcessStartInfo(editorPath, $"\"{fullPath}\"")
                    {
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null) LogBoth("<color=#FFAA00>Failed to start merge tool.</color>");
                    }
                }

                var data = _conflictCache.TryGetValue(targetFile, out var existing) ? existing : new SVNConflictData { Path = targetFile };
                data.Type = SVNConflictType.Manual; data.State = SVNConflictState.ManualEditing;
                _conflictCache[targetFile] = data;

                await RefreshConflictUIAsync();
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Exception:</color> {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        public async Task RefreshConflictUIAsync()
        {
            if (svnUI?.ResolveConsoleContent == null || svnUI.ConflictPrefab == null) return;

            var root = svnManager.WorkingDir;
            var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);

            var infos = new List<(string path, SVNConflictItem.ConflictType type, bool markers)>();
            foreach (var c in conflicts)
            {
                bool markers = await HasConflictMarkersAsync(Path.Combine(root, c.Path)).ConfigureAwait(false);
                infos.Add((c.Path, ConvertConflictType(c.Type), markers));
            }

            if (!TryEnterUiRefresh()) return;

            try
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        if (svnUI == null || svnUI.ResolveConsoleContent == null || svnUI.ConflictPrefab == null)
                        {
                            tcs.TrySetResult(false);
                            return;
                        }

                        var parent = svnUI.ResolveConsoleContent.transform;
                        for (int i = parent.childCount - 1; i >= 0; i--)
                            GameObject.Destroy(parent.GetChild(i).gameObject);

                        foreach (var info in infos)
                        {
                            var obj = GameObject.Instantiate(svnUI.ConflictPrefab, parent);
                            obj.SetActive(true);
                            var item = obj.GetComponent<SVNConflictItem>();
                            if (item != null) item.Setup(info.path, info.type, info.markers);
                        }
                        Canvas.ForceUpdateCanvases();
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogError($"[Resolve UI] Render error: {ex.Message}");
                    }
                    finally
                    {
                        tcs.TrySetResult(true);
                    }
                });

                await tcs.Task.ConfigureAwait(false);

                LogBoth($"[Resolve UI] Rendered {conflicts.Count} conflict items.");
            }
            finally
            {
                ExitUiRefresh();
            }
        }

        public async Task OpenSingle(string path)
        {
            if (!TryEnterProcessing()) return;
            try
            {
                string full = Path.Combine(svnManager.WorkingDir, path);
                if (!File.Exists(full)) { LogBoth($"<color=#FFAA00>File not found:</color> {path}"); return; }

                if (!TryLaunchExternalResolveTool(full, path))
                {
                    string editorPath = svnManager.MergeToolPath;
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        editorPath = PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
                        if (string.IsNullOrEmpty(editorPath)) { LogBoth("<color=#FFAA00>Merge tool path missing!</color>"); return; }
                    }

                    LogBoth($"Opening editor for: {path}");

                    using (var process = Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\"")
                    {
                        UseShellExecute = true,
                        CreateNoWindow = false
                    }))
                    {
                        if (process == null) LogBoth("<color=#FFAA00>Failed to start merge tool.</color>");
                    }
                }

                if (_conflictCache.TryGetValue(path, out var conflict)) { conflict.Type = SVNConflictType.Manual; conflict.State = SVNConflictState.ManualEditing; }
                await RefreshConflictUIAsync();
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>{ex.Message}</color>"); }
            finally { ExitProcessing(); }
        }

        public async Task MarkSingleResolved(string path)
        {
            if (!TryEnterProcessing()) return;
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string fullPath = Path.Combine(svnManager.WorkingDir, path);
                if (File.Exists(fullPath) && await HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
                { LogBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}"); return; }

                LogBoth($"[Resolve] Finalizing: {path}");
                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup --remove-unversioned", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                _conflictCache.TryRemove(path, out _);
                await Task.Delay(150).ConfigureAwait(false);

                await RefreshConflictUIAsync();
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                LogBoth($"<color=green>Resolved manually:</color> {path}");
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Error finalizing {path}:</color> {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        public async Task DeleteObstruction(string path, bool refreshUi = true) => await DeleteObstructionAsync(path, refreshUi).ConfigureAwait(false);

        public async Task DeleteObstructionAsync(string path, bool refreshUi = true)
        {
            await _resolveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                path = NormalizePath(path);
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                if (path.Contains("\"")) { LogBoth($"<color=#FFAA00>Invalid path (contains quotes):</color> {path}"); return; }

                string fullPath = Path.Combine(svnManager.WorkingDir, path);
                bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);
                LogBoth($"[TREE RESOLVE] {path} (exists: {fileExists})");

                if (fileExists)
                {
                    LogBoth("[TREE RESOLVE] File exists locally - removing physical file...");
                    try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false); } catch { }

                    bool removed = false;
                    try
                    {
                        if (File.Exists(fullPath)) { File.SetAttributes(fullPath, FileAttributes.Normal); File.Delete(fullPath); removed = true; }
                        else if (Directory.Exists(fullPath))
                        {
                            var di = new DirectoryInfo(fullPath); di.Attributes = FileAttributes.Normal;
                            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
                            Directory.Delete(fullPath, true); removed = true;
                        }
                    }
                    catch (Exception ex) { LogBoth($"<color=#yellow>Physical delete failed:</color> {ex.Message} - using svn delete --force"); }

                    if (!removed)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"delete \"{path}\" --force --keep-local", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                            if (File.Exists(fullPath)) File.Delete(fullPath);
                            else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                            LogBoth("[TREE RESOLVE] Removed via svn delete --force.");
                        }
                        catch (Exception ex2) { LogBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex2.Message}"); }
                    }
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    LogBoth("[TREE RESOLVE] File missing locally - restoring from server...");
                    try { await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false); LogBoth("[TREE RESOLVE] SVN metadata cleared via revert."); }
                    catch (Exception ex) { LogBoth($"<color=#FFFF00>Revert failed:</color> {ex.Message} - trying theirs-full..."); try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex2) { LogBoth($"<color=#FFAA00>theirs-full failed:</color> {ex2.Message}"); } }
                    try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false); } catch { }
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);
                _conflictCache.TryRemove(path, out _);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);

                if (refreshUi)
                {
                    await RefreshConflictUIAsync();
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                }

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);
                LogBoth($"<color=green>Tree conflict resolved:</color> {path}");
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>Tree resolve error:</color> {ex.Message}"); }
            finally { _resolveLock.Release(); }
        }

        private List<string> GetActiveConflictPaths() =>
            _conflictCache.Values.Where(x => x != null).Select(x => x.Path).ToList();

        private static string NormalizePath(string path) =>
            string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim();

        private SVNConflictItem.ConflictType ConvertConflictType(SVNConflictType type) => type switch
        {
            SVNConflictType.Manual => SVNConflictItem.ConflictType.Manual,
            SVNConflictType.Tree => SVNConflictItem.ConflictType.Tree,
            _ => SVNConflictItem.ConflictType.Text,
        };

        private async Task<List<SVNConflictData>> GetConflictsAsync(string root)
        {
            try
            {
                string xml = await SvnRunner.RunAsync("status --xml", root, false, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml)) return new List<SVNConflictData>();

                var result = new List<SVNConflictData>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var stringReader = new StringReader(xml))
                using (var reader = XmlReader.Create(stringReader, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit }))
                {
                    string currentPath = null, item = null, props = null, tree = null;
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "entry": currentPath = reader.GetAttribute("path"); item = props = tree = null; break;
                                case "wc-status": item = reader.GetAttribute("item"); props = reader.GetAttribute("props"); tree = reader.GetAttribute("tree-conflicted"); break;
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
                                    if (_conflictCache.TryGetValue(path, out var cached) && cached.State == SVNConflictState.ManualEditing) type = SVNConflictType.Manual;

                                    var data = new SVNConflictData
                                    {
                                        Path = path,
                                        Type = type,
                                        State = _conflictCache.TryGetValue(path, out var old) ? old.State : SVNConflictState.Pending
                                    };
                                    _conflictCache[path] = data; result.Add(data);
                                }
                            }
                        }
                    }
                }

                var valid = new HashSet<string>(result.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var key in _conflictCache.Keys.ToList()) { if (!valid.Contains(key)) _conflictCache.TryRemove(key, out _); }
                return result.OrderBy(x => x.Path).ToList();
            }
            catch (Exception ex) { LogBoth($"<color=#FFAA00>GetConflicts error:</color> {ex.Message}"); return new List<SVNConflictData>(); }
        }

        private async Task<bool> HasConflictMarkersAsync(string fullPath)
        {
            if (!File.Exists(fullPath)) return false;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 5 * 1024 * 1024) // Ignore files bigger than 5 MB
                return false;

            try
            {
                return await Task.Run(() =>
                {
                    using var stream = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192);
                    string line;
                    while ((line = stream.ReadLine()) != null)
                    {
                        ReadOnlySpan<char> span = line.AsSpan().TrimStart();

                        if (span.StartsWith("<<<<<<<") ||
                            span.StartsWith(">>>>>>>") ||
                            span.StartsWith("======="))
                        {
                            return true;
                        }
                    }
                    return false;
                }).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshMainUIAfterResolve()
        {
            try
            {
                var statusModule = svnManager?.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    SVNStatus.ClearLockCache();

                    await statusModule.RefreshAfterAction();
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>[Warning] Main UI refresh failed:</color> {ex.Message}");
            }
        }

        public void Dispose()
        {
            _resolveLock?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}