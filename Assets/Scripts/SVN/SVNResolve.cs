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
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private CancellationTokenSource _activeCts;
        private Task _activeTask;
        private int _processingFlag;
        private int _uiRefreshingFlag;
        private int _disposed;

        public bool IsResolveBusy => Volatile.Read(ref _processingFlag) == 1;

        public enum SVNConflictType { Text, Manual, Tree }
        public enum SVNConflictState { Pending, ManualEditing, Resolving, Resolved }

        public class SVNConflictData
        {
            public string Path;
            public SVNConflictType Type;
            public SVNConflictState State;

            public string TreeConflictReason;
            public string TreeConflictAction;
            public string TreeConflictVictim;
            public string TreeConflictNodeKind;
        }

        private readonly ConcurrentDictionary<string, SVNConflictData> _conflictCache =
            new(StringComparer.OrdinalIgnoreCase);

        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        private void LogBoth(string msg)
        {
            PostToMainThread(() =>
            {
                SVNLogBridge.LogLine(msg);
                if (svnUI?.ResolveLogConsole != null)
                    SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
            });
        }

        private void LogOverwrite(string msg)
        {
            PostToMainThread(() =>
            {
                if (svnUI?.ResolveLogConsole != null)
                    SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", false);
            });
        }

        private bool TryEnterProcessing()
        {
            if (IsDisposed) return false;
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private bool TryEnterUiRefresh() => Interlocked.Exchange(ref _uiRefreshingFlag, 1) == 0;
        private void ExitUiRefresh() => Interlocked.Exchange(ref _uiRefreshingFlag, 0);

        private static void SafeFireAndForget(Func<Task> operation)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogException(ex);
                }
            });
        }

        #region Public API Wrappers

        public void AutoRefreshConflictList() => SafeFireAndForget(() => AutoRefreshConflictListAsync(CancellationToken.None));
        public void MarkAsResolved() => SafeFireAndForget(MarkAsResolvedAsync);
        public void DeleteAllObstructions() => SafeFireAndForget(DeleteAllObstructionsAsync);
        public void ResolveTheirs() => SafeFireAndForget(() => ResolveAllConflictsAsync("theirs-full"));
        public void ResolveMine() => SafeFireAndForget(() => ResolveAllConflictsAsync("mine-full"));
        public void OpenInEditor() => SafeFireAndForget(OpenInEditorAsync);
        public void ResolveAllMine() => SafeFireAndForget(() => ResolveAllConflictsAsync("mine-full"));
        public void ResolveAllTheirs() => SafeFireAndForget(() => ResolveAllConflictsAsync("theirs-full"));
        public void ResolveAllTreeMine() => SafeFireAndForget(() => ResolveAllTreeAsync("mine-full"));
        public void ResolveAllTreeTheirs() => SafeFireAndForget(() => ResolveAllTreeAsync("theirs-full"));
        public void ResolveAllTreeBase() => SafeFireAndForget(() => ResolveAllTreeAsync("base"));

        public void ResolveAllTreeTheirsForce() => SafeFireAndForget(() => ResolveAllTreeForceAsync("theirs-full"));
        public void ResolveAllTreeMineForce() => SafeFireAndForget(() => ResolveAllTreeForceAsync("mine-full"));
        public void ResolveAllTreeBaseForce() => SafeFireAndForget(() => ResolveAllTreeForceAsync("base"));

        public async Task ResolveTreeTheirsForce(string path) => await ResolveTreeForceAsync(path, "theirs-full").ConfigureAwait(false);
        public async Task ResolveTreeMineForce(string path) => await ResolveTreeForceAsync(path, "mine-full").ConfigureAwait(false);
        public async Task ResolveTreeBaseForce(string path) => await ResolveTreeForceAsync(path, "base").ConfigureAwait(false);

        public void CancelResolve()
        {
            try
            {
                var cts = Volatile.Read(ref _activeCts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
                LogBoth("<color=orange><b>[Resolve]</b> Cancel requested...</color>");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Error during cancel:</color> {ex.Message}");
            }
        }

        public async Task<bool> ResolveSingleMine(string path) =>
            await RunWithLockAsync(token => ResolveSingleCoreAsync(path, "mine-full", token)).ConfigureAwait(false);

        public async Task<bool> ResolveSingleTheirs(string path) =>
            await RunWithLockAsync(token => ResolveSingleCoreAsync(path, "theirs-full", token)).ConfigureAwait(false);

        public async Task RefreshConflictUI() =>
            await RefreshConflictUIAsync(CancellationToken.None).ConfigureAwait(false);

        public async Task OpenSingle(string path)
        {
            string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string full = Path.Combine(svnManager.WorkingDir, path);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {path}");
                    return;
                }

                if (!TryLaunchExternalResolveTool(full, path, resolveToolPath))
                {
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        LogBoth("<color=#FFAA00>Merge tool path missing!</color>");
                        return;
                    }

                    try
                    {
                        LogBoth($"Opening editor for: <color=green>{path}</color>");
                        Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>Failed to launch editor:</color> {ex.Message}");
                        return;
                    }
                }

                var existing = _conflictCache.TryGetValue(path, out var e) ? e : new SVNConflictData { Path = path };

                _conflictCache[path] = new SVNConflictData
                {
                    Path = existing.Path,
                    Type = existing.Type == SVNConflictType.Tree ? SVNConflictType.Tree : SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing,
                    TreeConflictReason = existing.TreeConflictReason,
                    TreeConflictAction = existing.TreeConflictAction,
                    TreeConflictVictim = existing.TreeConflictVictim,
                    TreeConflictNodeKind = existing.TreeConflictNodeKind
                };

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
            });
        }

        public async Task<bool> MarkSingleResolved(string path)
        {
            return await RunWithLockAsync(async token =>
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string fullPath = Path.Combine(svnManager.WorkingDir, path);

                if (_conflictCache.TryGetValue(path, out var data) && data.Type == SVNConflictType.Tree)
                {
                    LogBoth($"<color=#FFAA00>Tree conflict requires explicit strategy (Mine/Theirs/Base/Delete):</color> {path}");
                    return false;
                }

                if (File.Exists(fullPath) && await HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
                {
                    LogBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}");
                    return false;
                }

                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                _conflictCache.TryRemove(path, out _);

                await Task.Delay(150).ConfigureAwait(false);
                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);

                LogBoth($"<color=green>Resolved manually:</color> {path}");
                return true;
            }).ConfigureAwait(false);
        }

        public async Task<bool> ResolveTreeMine(string path) => await ResolveTreeStrategyAsync(path, "mine-full").ConfigureAwait(false);
        public async Task<bool> ResolveTreeTheirs(string path) => await ResolveTreeStrategyAsync(path, "theirs-full").ConfigureAwait(false);
        public async Task<bool> ResolveTreeBase(string path) => await ResolveTreeStrategyAsync(path, "base").ConfigureAwait(false);
        public async Task<bool> ResolveTreeWorking(string path) => await ResolveTreeStrategyAsync(path, "working").ConfigureAwait(false);

        public async Task<bool> DeleteObstruction(string path, bool refreshUi = true) =>
            await DeleteObstructionAsync(path, refreshUi).ConfigureAwait(false);

        public async Task<bool> DeleteObstructionAsync(string path, bool refreshUi = true)
        {
            return await RunWithLockAsync(async token =>
            {
                bool success = await DeleteObstructionCoreAsync(path, token).ConfigureAwait(false);
                if (refreshUi)
                {
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                }
                return success;
            }).ConfigureAwait(false);
        }

        #endregion

        #region Core Async Logic

        public async Task AutoRefreshConflictListAsync(CancellationToken externalToken)
        {
            await RunWithLockAsync(async internalToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalToken, externalToken);
                var combinedToken = linkedCts.Token;

                LogOverwrite("<color=yellow>Scanning for conflicts...</color>");
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, combinedToken).ConfigureAwait(false);
                if (combinedToken.IsCancellationRequested) return;

                await RefreshConflictUIAsync(combinedToken).ConfigureAwait(false);

                if (conflicts.Count == 0)
                    LogOverwrite("<color=green>No conflicts found.</color>");
                else
                    LogOverwrite($"<color=green>Conflicts refreshed: {conflicts.Count} found.</color>");
            });
        }

        private async Task OpenInEditorAsync()
        {
            string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
            string targetFromUI = svnUI?.ResolveTargetFileInput?.text;
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string root = svnManager.WorkingDir;
                string targetFile = !string.IsNullOrWhiteSpace(targetFromUI) ? NormalizePath(targetFromUI.Trim()) : null;

                if (string.IsNullOrEmpty(targetFile))
                {
                    targetFile = _conflictCache.Values
                        .OrderBy(x => x.Path)
                        .FirstOrDefault(x => x.State != SVNConflictState.Resolved)?.Path;

                    if (string.IsNullOrEmpty(targetFile))
                    {
                        var conflicts = await GetConflictsAsync(root, token).ConfigureAwait(false);
                        foreach (var c in conflicts)
                            _conflictCache.AddOrUpdate(c.Path, c, (_, __) => c);
                        targetFile = conflicts.FirstOrDefault()?.Path;
                    }
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    LogBoth("<color=yellow>No conflicted file found.</color>");
                    return;
                }

                string full = Path.Combine(root, targetFile);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {targetFile}");
                    return;
                }

                if (!TryLaunchExternalResolveTool(full, targetFile, resolveToolPath))
                {
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        LogBoth("<color=#FFAA00>Merge tool path missing!</color>");
                        return;
                    }

                    try
                    {
                        LogBoth($"Opening editor for: <color=green>{targetFile}</color>");
                        Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>Failed to launch editor:</color> {ex.Message}");
                        return;
                    }
                }

                var existing = _conflictCache.TryGetValue(targetFile, out var e) ? e : new SVNConflictData { Path = targetFile };

                _conflictCache[targetFile] = new SVNConflictData
                {
                    Path = existing.Path,
                    Type = existing.Type == SVNConflictType.Tree ? SVNConflictType.Tree : SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing,
                    TreeConflictReason = existing.TreeConflictReason,
                    TreeConflictAction = existing.TreeConflictAction,
                    TreeConflictVictim = existing.TreeConflictVictim,
                    TreeConflictNodeKind = existing.TreeConflictNodeKind
                };

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
            });
        }

        private async Task ResolveAllConflictsAsync(string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                conflicts = SortConflictsDeepestFirst(conflicts);
                var paths = conflicts.Where(x => x != null).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                int total = paths.Count;
                if (total == 0) { LogOverwrite("<color=yellow>No conflicts found.</color>"); return; }

                LogOverwrite($"<color=yellow>Starting {strategy} for {total} conflicts (deepest first)...</color>");
                int successCount = 0;
                var failedFiles = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] Resolving: {paths[i]}</color>");
                    var result = await ResolveSingleCoreSilentAsync(paths[i], strategy, token).ConfigureAwait(false);
                    if (result.success) successCount++;
                    else failedFiles.Add(result.path);
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                var latest = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                UpdateCacheFromLatest(latest);

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                if (failedFiles.Count == 0)
                    LogOverwrite($"<color=green>Successfully resolved all {successCount}/{total} conflicts ({strategy}).</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
            });
        }

        private async Task ResolveAllTreeAsync(string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                var treeConflicts = conflicts.Where(c => c.Type == SVNConflictType.Tree).ToList();

                if (treeConflicts.Count == 0) { LogOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

                treeConflicts = SortConflictsDeepestFirst(treeConflicts);
                int total = treeConflicts.Count;
                LogOverwrite($"<color=yellow>Resolving {total} tree conflicts with '{strategy}' (deepest first)...</color>");

                int successCount = 0;

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var c = treeConflicts[i];
                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] {c.Path}</color>");

                    try
                    {
                        if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                        {
                            await SvnRunner.RunAsync($"revert \"{c.Path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            await SvnRunner.RunAsync($"resolve --accept working \"{c.Path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            successCount++;
                        }
                        else
                        {
                            await SvnRunner.RunAsync($"resolve --accept {strategy} \"{c.Path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            successCount++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                            LogBoth($"<color=#FFAA00>Failed to restore base for {c.Path}: {ex.Message}</color>");

                        if (strategy.EndsWith("-full"))
                        {
                            try
                            {
                                string fb = strategy.Replace("-full", "-conflict");
                                await SvnRunner.RunAsync($"resolve --accept {fb} \"{c.Path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                                successCount++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { }
                        }
                    }
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                var latest = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                UpdateCacheFromLatest(latest);

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                int remaining = latest.Count(c => c.Type == SVNConflictType.Tree);
                if (remaining == 0)
                    LogOverwrite($"<color=green>All tree conflicts resolved with '{strategy}'.</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Remaining tree conflicts: {remaining}</color>");
            });
        }

        private async Task MarkAsResolvedAsync()
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                var clean = new List<SVNConflictData>();
                var blocked = new List<SVNConflictData>();

                foreach (var c in conflicts)
                {
                    if (c.Type == SVNConflictType.Tree) { blocked.Add(c); continue; }
                    string full = Path.Combine(svnManager.WorkingDir, c.Path);
                    if (File.Exists(full) && await HasConflictMarkersAsync(full).ConfigureAwait(false))
                        blocked.Add(c);
                    else
                        clean.Add(c);
                }

                if (clean.Count > 0)
                {
                    clean = SortConflictsDeepestFirst(clean);
                    LogOverwrite($"<color=yellow>Marking {clean.Count} files as resolved...</color>");
                    int successCount = 0;

                    for (int i = 0; i < clean.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        LogOverwrite($"<color=yellow>[{i + 1}/{clean.Count}] Marking: {clean[i].Path}</color>");
                        var result = await ResolveSingleCoreSilentAsync(clean[i].Path, "working", token).ConfigureAwait(false);
                        if (result.success) successCount++;
                    }

                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                    svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                    LogOverwrite($"<color=green>Successfully marked {successCount}/{clean.Count} files as resolved.</color>");
                }
                else
                {
                    LogOverwrite("<color=yellow>No files were marked as resolved.</color>");
                }

                if (blocked.Count > 0)
                {
                    LogBoth($"<color=#FFAA00>{blocked.Count} conflict(s) still need manual action:</color>");
                    foreach (var c in blocked)
                        LogBoth($"<color=#FFAA00> • {c.Path} ({c.Type}" + (string.IsNullOrEmpty(c.TreeConflictReason) ? "" : $": {c.TreeConflictReason}") + ")</color>");
                }
            });
        }

        private async Task DeleteAllObstructionsAsync()
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                var treeConflicts = conflicts.Where(x => x.Type == SVNConflictType.Tree).ToList();

                if (treeConflicts.Count == 0) { LogOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

                treeConflicts = SortConflictsDeepestFirst(treeConflicts);
                int total = treeConflicts.Count;
                LogOverwrite($"<color=#FF4444><b>RESOLVING {total} TREE CONFLICTS (deepest first)...</b></color>");

                int successCount = 0;
                var failedPaths = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var c = treeConflicts[i];
                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] Processing: {c.Path}</color>");
                    var result = await DeleteObstructionCoreSilentAsync(c.Path, token).ConfigureAwait(false);
                    if (result.success) successCount++;
                    else failedPaths.Add(result.path);
                }

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                if (failedPaths.Count == 0)
                    LogOverwrite($"<color=green>Successfully cleared {successCount} tree conflicts.</color>\n<color=#FFAA00>Important: Some items may now be scheduled for deletion. Use Revert to restore them or Commit to accept the deletion.</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Cleared {successCount}/{total}. Failed: {failedPaths.Count}</color>");
            });
        }

        private async Task<bool> ResolveTreeStrategyAsync(string rawPath, string strategy)
        {
            return await RunWithLockAsync(async token =>
            {
                if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
                {
                    LogBoth($"<color=#FFAA00>Invalid path:</color> {rawPath}");
                    return false;
                }

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                var allConflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                if (HasUnresolvedParentConflict(path, allConflicts))
                    LogBoth($"<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Consider resolving children first, then the parent.");

                _conflictCache.TryGetValue(path, out var info);
                string reason = info?.TreeConflictReason ?? "unknown";

                LogBoth($"[TREE RESOLVE] {path}");
                LogBoth($"   Strategy : <color=yellow>{strategy}</color>");
                LogBoth($"   Reason   : <color=#FFAA00>{reason}</color>");

                bool success = false;
                string error = null;

                try
                {
                    if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        success = true;
                    }
                    else
                    {
                        await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        success = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    error = ex.Message;
                    if (strategy.EndsWith("-full"))
                    {
                        string fallback = strategy.Replace("-full", "-conflict");
                        try
                        {
                            await SvnRunner.RunAsync($"resolve --accept {fallback} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            success = true;
                            error = null;
                            LogBoth($"<color=yellow>Fallback to {fallback} succeeded.</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception fbEx) { error = fbEx.Message; }
                    }
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                var remaining = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                string normalized = NormalizePath(path);
                bool stillExists = remaining.Any(c => NormalizePath(c.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));

                if (success && !stillExists)
                {
                    _conflictCache.TryRemove(normalized, out _);
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                    svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                    LogBoth($"<color=green>Tree conflict resolved with '{strategy}':</color> {path}");
                    return true;
                }
                else
                {
                    LogBoth($"<color=#FF4444>Failed to resolve tree conflict with '{strategy}':</color> {path}");
                    if (!string.IsNullOrEmpty(error)) LogBoth($"<color=#FFAA00>Error:</color> {error}");
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        private async Task<(bool success, string path, string error)> ResolveSingleCoreSilentAsync(string rawPath, string strategy, CancellationToken token)
        {
            if (IsDisposed) return (false, rawPath, "Module disposed");
            if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath, "Invalid path");

            if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    return (true, path, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (false, path, ex.Message); }
            }

            if (_conflictCache.TryGetValue(path, out var data))
            {
                _conflictCache[path] = new SVNConflictData
                {
                    Path = data.Path,
                    Type = data.Type,
                    State = SVNConflictState.Resolving,
                    TreeConflictReason = data.TreeConflictReason,
                    TreeConflictAction = data.TreeConflictAction,
                    TreeConflictVictim = data.TreeConflictVictim,
                    TreeConflictNodeKind = data.TreeConflictNodeKind
                };
            }

            bool resolved = false;
            string errorMsg = null;

            try
            {
                await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                resolved = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
            {
                string fallbackStrategy = strategy.Replace("-full", "-conflict");
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    resolved = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception fallbackEx) { errorMsg = fallbackEx.Message; }
            }
            catch (Exception ex) { errorMsg = ex.Message; }

            if (resolved)
            {
                try
                {
                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _conflictCache.TryRemove(path, out _);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            return (resolved, path, errorMsg);
        }

        private async Task<bool> ResolveSingleCoreAsync(string rawPath, string strategy, CancellationToken token)
        {
            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            LogBoth($"[Resolve] {strategy} → {rawPath}");
            var result = await ResolveSingleCoreSilentAsync(rawPath, strategy, token).ConfigureAwait(false);

            if (result.success)
            {
                LogBoth($"<color=green>Resolved:</color> {result.path}");
                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                return true;
            }
            else
            {
                LogBoth($"<color=#FF4444>Resolution failed for:</color> {result.path}" + (string.IsNullOrEmpty(result.error) ? "" : $" ({result.error})"));
                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return false;
            }
        }

        private async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
            {
                LogBoth($"<color=#FFAA00>Invalid path:</color> {rawPath}");
                return false;
            }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            var allConflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
            if (HasUnresolvedParentConflict(path, allConflicts))
                LogBoth($"<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Resolve children first, then the parent.");

            string fullPath = Path.Combine(svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            _conflictCache.TryGetValue(path, out var conflictInfo);
            string reason = conflictInfo?.TreeConflictReason ?? "unknown";
            string nodeKind = conflictInfo?.TreeConflictNodeKind ?? (fileExists ? "file/dir" : "missing");

            LogBoth($"[TREE RESOLVE] {path}");
            LogBoth($"   Reason : <color=#FFAA00>{reason}</color>");
            LogBoth($"   Kind   : {nodeKind} | Exists on disk: {fileExists}");

            if (fileExists)
            {
                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    LogBoth($"<color=yellow>Scheduled for deletion:</color> {path}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex.Message}");
                    return false;
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception revertEx)
                {
                    LogBoth($"<color=#FFFF00>Revert failed:</color> {revertEx.Message} → trying theirs-full...");
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception theirsEx) { LogBoth($"<color=#FFAA00>theirs-full failed:</color> {theirsEx.Message}"); }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception workingEx) { LogBoth($"<color=#FFAA00>resolve working failed:</color> {workingEx.Message}"); }
            }

            await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var remaining = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
            string normalizedPath = NormalizePath(path);
            bool stillExists = remaining.Any(c => NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists)
            {
                LogBoth($"<color=#FF4444>Tree conflict still exists:</color> {path}");
                return false;
            }
            else
            {
                _conflictCache.TryRemove(normalizedPath, out _);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);

                if (fileExists)
                    LogBoth($"<color=green>Tree conflict cleared (item deleted).</color>\n<color=#FFAA00>Next step: Revert (to restore) or Commit (to accept deletion).</color>");
                else
                    LogBoth($"<color=green>Tree conflict resolved:</color> {path}");
                return true;
            }
        }

        private async Task<(bool success, string path)> DeleteObstructionCoreSilentAsync(string rawPath, CancellationToken token)
        {
            if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath);

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string fullPath = Path.Combine(svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            if (fileExists)
            {
                try { await SvnRunner.RunAsync($"delete \"{path}\" --force", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { return (false, path); }
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            try { await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            var remaining = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
            string normalizedPath = NormalizePath(path);
            bool stillExists = remaining.Any(c => NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists) return (false, path);

            _conflictCache.TryRemove(normalizedPath, out _);
            return (true, path);
        }

        #endregion

        #region Data Fetching & UI

        private async Task<List<SVNConflictData>> GetConflictsAsync(string root, CancellationToken token = default)
        {
            try
            {
                string xml = await SvnRunner.RunAsync("status --xml", root, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml)) return new List<SVNConflictData>();

                var result = new List<SVNConflictData>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var stringReader = new StringReader(xml))
                using (var reader = XmlReader.Create(stringReader, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true }))
                {
                    string currentPath = null; string item = null; string props = null; string tree = null;
                    string tcReason = null; string tcAction = null; string tcVictim = null; string tcNodeKind = null;
                    bool insideTreeConflict = false;

                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();

                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "entry":
                                    currentPath = reader.GetAttribute("path");
                                    item = props = tree = null;
                                    tcReason = tcAction = tcVictim = tcNodeKind = null;
                                    insideTreeConflict = false;
                                    break;
                                case "wc-status":
                                    item = reader.GetAttribute("item");
                                    props = reader.GetAttribute("props");
                                    tree = reader.GetAttribute("tree-conflicted");
                                    break;
                                case "tree-conflict":
                                    insideTreeConflict = true;
                                    tcVictim = reader.GetAttribute("victim");
                                    tcNodeKind = reader.GetAttribute("kind");
                                    tcAction = reader.GetAttribute("operation") ?? reader.GetAttribute("action");
                                    break;
                                case "reason" when insideTreeConflict:
                                    tcReason = reader.GetAttribute("name") ?? reader.GetAttribute("value") ?? reader.GetAttribute("reason");
                                    if (string.IsNullOrEmpty(tcReason) && !reader.IsEmptyElement)
                                    {
                                        try { string content = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false); if (!string.IsNullOrWhiteSpace(content)) tcReason = content.Trim(); }
                                        catch { }
                                    }
                                    break;
                                case "action" when insideTreeConflict:
                                    tcAction = reader.GetAttribute("name") ?? reader.GetAttribute("value") ?? reader.GetAttribute("action") ?? tcAction;
                                    break;
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.Text && insideTreeConflict)
                        {
                            string text = reader.Value?.Trim();
                            if (!string.IsNullOrEmpty(text) && string.IsNullOrEmpty(tcReason)) tcReason = text;
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            if (reader.Name == "tree-conflict") insideTreeConflict = false;
                            else if (reader.Name == "entry")
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
                                            State = _conflictCache.TryGetValue(path, out var old) ? old.State : SVNConflictState.Pending,
                                            TreeConflictReason = tcReason,
                                            TreeConflictAction = tcAction,
                                            TreeConflictVictim = string.IsNullOrEmpty(tcVictim) ? path : NormalizePath(tcVictim),
                                            TreeConflictNodeKind = tcNodeKind
                                        };

                                        if (type == SVNConflictType.Tree && string.IsNullOrEmpty(data.TreeConflictReason))
                                            data.TreeConflictReason = BuildFallbackTreeReason(data);

                                        _conflictCache[path] = data;
                                        result.Add(data);
                                    }
                                }
                            }
                        }
                    }
                }

                var valid = new HashSet<string>(result.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var key in _conflictCache.Keys.ToList())
                {
                    if (!valid.Contains(key))
                        _conflictCache.TryRemove(key, out _);
                }

                return result.OrderBy(x => x.Path).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    LogBoth($"<color=#FFAA00>GetConflicts error:</color> {ex.Message}");
                return new List<SVNConflictData>();
            }
        }

        public async Task RefreshConflictUIAsync(CancellationToken token = default)
        {
            if (svnUI?.ResolveConsoleContent == null || svnUI.ConflictPrefab == null || IsDisposed) return;
            if (!TryEnterUiRefresh()) return;

            try
            {
                var root = svnManager.WorkingDir;
                var conflicts = await GetConflictsAsync(root, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                var infos = new List<(string path, SVNConflictItem.ConflictType type, bool markers, string treeReason)>();
                foreach (var c in conflicts)
                {
                    token.ThrowIfCancellationRequested();
                    bool markers = await HasConflictMarkersAsync(Path.Combine(root, c.Path)).ConfigureAwait(false);
                    infos.Add((c.Path, ConvertConflictType(c.Type), markers, c.TreeConflictReason));
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                PostToMainThread(() =>
                {
                    try
                    {
                        if (token.IsCancellationRequested || svnUI?.ResolveConsoleContent == null)
                        {
                            tcs.TrySetResult(false);
                            return;
                        }

                        var parent = svnUI.ResolveConsoleContent.transform;
                        for (int i = parent.childCount - 1; i >= 0; i--)
                        {
                            var child = parent.GetChild(i).gameObject;
                            UnityEngine.Object.Destroy(child);
                        }

                        foreach (var info in infos)
                        {
                            var obj = UnityEngine.Object.Instantiate(svnUI.ConflictPrefab, parent);
                            obj.SetActive(true);
                            var item = obj.GetComponent<SVNConflictItem>();
                            item?.Setup(info.path, info.type, info.markers, info.treeReason);
                        }

                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>[Resolve UI] Render error:</color> {ex.Message}");
                        tcs.TrySetResult(false);
                    }
                });

                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(2000, token)).ConfigureAwait(false);
                if (completed != tcs.Task)
                    LogBoth("<color=#FFAA00>UI Refresh timeout (main thread unresponsive).</color>");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>UI Refresh error:</color> {ex.Message}");
            }
            finally
            {
                ExitUiRefresh();
            }
        }

        #endregion

        #region Force Resolution Methods (final)

        private async Task ResolveAllTreeForceAsync(string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                var treeConflicts = conflicts.Where(c => c.Type == SVNConflictType.Tree).ToList();

                if (treeConflicts.Count == 0) { LogOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

                treeConflicts = SortConflictsDeepestFirst(treeConflicts);
                int total = treeConflicts.Count;
                LogOverwrite($"<color=yellow>Force Resolving {total} tree conflicts with '{strategy}' (deepest first)...</color>");

                int successCount = 0;
                var failedFiles = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var c = treeConflicts[i];
                    LogOverwrite($"<color=cyan>[TREE FORCE RESOLVE] {strategy} -> {c.Path}</color>");

                    var result = await ResolveTreeForceCoreAsync(c, strategy, token).ConfigureAwait(false);
                    if (result.success) successCount++;
                    else failedFiles.Add(c.Path);
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                var latest = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                UpdateCacheFromLatest(latest);

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                if (failedFiles.Count == 0)
                    LogOverwrite($"<color=green>Force resolved all {successCount}/{total} tree conflicts ({strategy}).</color>");
                else
                {
                    LogOverwrite($"<color=#FFAA00>Force resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
                    foreach (var f in failedFiles)
                        LogBoth($"<color=#FF4444>  -> Failed to force-resolve tree conflict ({strategy}): {f}</color>");
                }
            });
        }

        private async Task ResolveTreeForceAsync(string path, string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                if (!TryGetRelativePath(svnManager.WorkingDir, path, out string relativePath))
                {
                    LogBoth($"<color=#FFAA00>Invalid path:</color> {path}");
                    return;
                }

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                var allConflicts = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                var conflictData = allConflicts.FirstOrDefault(c => c.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (conflictData == null || conflictData.Type != SVNConflictType.Tree)
                {
                    LogBoth($"<color=#FFAA00>Not a valid tree conflict:</color> {relativePath}");
                    return;
                }

                LogOverwrite($"<color=cyan>[TREE FORCE RESOLVE] {strategy} -> {relativePath}</color>");
                var result = await ResolveTreeForceCoreAsync(conflictData, strategy, token).ConfigureAwait(false);

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                var remaining = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                bool stillExists = remaining.Any(c => c.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (result.success && !stillExists)
                {
                    _conflictCache.TryRemove(relativePath, out _);
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                    svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(relativePath);
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                    LogBoth($"<color=green>Force resolved tree conflict ({strategy}):</color> {relativePath}");
                }
                else
                {
                    LogBoth($"<color=#FF4444>Failed to force-resolve tree conflict ({strategy}):</color> {relativePath}");
                    if (!string.IsNullOrEmpty(result.error)) LogBoth($"<color=#FFAA00>Error:</color> {result.error}");
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                }
            });
        }

        private async Task<(bool success, string error)> ResolveTreeForceCoreAsync(SVNConflictData conflict, string strategy, CancellationToken token)
        {
            LogBoth($"<color=magenta>[FORCE CORE ENTER] strategy={strategy} path={conflict.Path}</color>");

            string path = conflict.Path;
            string fullPath = Path.Combine(svnManager.WorkingDir, path);

            bool isTheirs = strategy.Contains("theirs", StringComparison.OrdinalIgnoreCase);
            bool isMine = strategy.Contains("mine", StringComparison.OrdinalIgnoreCase);
            bool isBase = strategy.Equals("base", StringComparison.OrdinalIgnoreCase);

            try
            {
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"",
                        svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    return (true, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsInapplicableOrObstruction(ex))
                {
                    LogBoth($"<color=yellow>  -> Standard resolve not applicable ({GetShortError(ex)}). Forcing structural fix...</color>");
                }

                if (strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
                {
                    string conflictVariant = strategy.Replace("-full", "-conflict");
                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept {conflictVariant} \"{path}\"",
                            svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        return (true, null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* idziemy dalej */ }
                }

                bool itemExists = File.Exists(fullPath) || Directory.Exists(fullPath);

                if (isTheirs)
                {
                    LogBoth($"<color=yellow>  -> Removing local obstruction: {path}</color>");

                    try
                    {
                        await SvnRunner.RunAsync($"delete \"{path}\" --force",
                            svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception delEx)
                    {
                        LogBoth($"<color=#FFAA00>  -> svn delete failed: {GetShortError(delEx)} → SafeDelete</color>");
                        token.ThrowIfCancellationRequested();
                        SafeDelete(fullPath);
                    }

                    try { await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                            svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception resEx)
                    {
                        LogBoth($"<color=#FFAA00>  -> resolve working failed: {GetShortError(resEx)}</color>");
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"update \"{path}\"",
                            svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception upEx)
                    {
                        LogBoth($"<color=#FFAA00>  -> update after force: {GetShortError(upEx)}</color>");
                    }
                }
                else if (isMine)
                {
                    if (!itemExists)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"",
                                svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }

                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                        svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                else // base
                {
                    try
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"",
                            svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                        svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                var remaining = await GetConflictsAsync(svnManager.WorkingDir, token).ConfigureAwait(false);
                bool stillConflicted = remaining.Any(c =>
                    NormalizePath(c.Path).Equals(NormalizePath(path), StringComparison.OrdinalIgnoreCase));

                if (stillConflicted)
                    return (false, "Conflict still present after force structural fix");

                return (true, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static bool IsInapplicableOrObstruction(Exception ex)
        {
            string msg = GetFullExceptionMessage(ex);
            return msg.Contains("W195024") ||
                   msg.Contains("E155027") ||
                   msg.Contains("Inapplicable conflict resolution option", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("obstructed", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E155025") || msg.Contains("E155010") || msg.Contains("E155011") ||
                   msg.Contains("E155012") || msg.Contains("E155015") || msg.Contains("E155016") ||
                   msg.Contains("E155017") || msg.Contains("W195012");
        }

        private static string GetFullExceptionMessage(Exception ex)
        {
            if (ex == null) return "";
            var sb = new StringBuilder(ex.Message ?? "");
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append(" | ").Append(inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        private static string GetShortError(Exception ex)
        {
            string msg = GetFullExceptionMessage(ex);
            int idx = msg.IndexOf('\n');
            return idx > 0 ? msg.Substring(0, idx).Trim() : msg.Trim();
        }

        private async Task SafeDelete(string path, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return;

                string backupRoot = GetBackupRoot();
                if (string.IsNullOrEmpty(backupRoot))
                {
                    LogBoth("<color=#FFAA00>[Backup]</color> Failed to create backup folder – deleting permanently.");
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
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Move(path, destPath);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Move(path, destPath);
                }

                // Explicit user information
                LogBoth($"<color=#00FF88><b>[Backup]</b></color> File moved to backup:");
                LogBoth($"<color=#AAAAAA>  Source :</color> {path}");
                LogBoth($"<color=#AAAAAA>  Backup :</color> <color=yellow>{destPath}</color>");
                LogBoth($"<color=#888888>  Backup folder: {backupRoot}</color>");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>[Backup] Failed to move file – deleting permanently.</color>");
                LogBoth($"<color=#FFAA00>  Reason: {ex.Message}</color>");
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
                string root = Path.GetFullPath(svnManager.WorkingDir)
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

        #endregion

        #region Utilities

        private static List<SVNConflictData> SortConflictsDeepestFirst(List<SVNConflictData> conflicts)
        {
            return conflicts.OrderByDescending(c => c.Path.Count(ch => ch == '/')).ThenByDescending(c => c.Path.Length).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasUnresolvedParentConflict(string path, List<SVNConflictData> conflicts)
        {
            if (string.IsNullOrWhiteSpace(path) || conflicts == null || conflicts.Count == 0) return false;

            string normalized = NormalizePath(path);
            string parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/').Trim();

            while (!string.IsNullOrWhiteSpace(parent))
            {
                if (conflicts.Any(c => NormalizePath(c.Path).Equals(parent, StringComparison.OrdinalIgnoreCase)))
                    return true;
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/').Trim();
            }

            return false;
        }

        private static string BuildFallbackTreeReason(SVNConflictData data)
        {
            if (!string.IsNullOrEmpty(data.TreeConflictAction)) return $"operation: {data.TreeConflictAction}";
            if (!string.IsNullOrEmpty(data.TreeConflictNodeKind)) return $"tree conflict ({data.TreeConflictNodeKind})";
            return "tree conflict (details unavailable)";
        }

        private SVNConflictItem.ConflictType ConvertConflictType(SVNConflictType type)
        {
            return type switch
            {
                SVNConflictType.Tree => SVNConflictItem.ConflictType.Tree,
                SVNConflictType.Manual => SVNConflictItem.ConflictType.Manual,
                _ => SVNConflictItem.ConflictType.Text
            };
        }

        private async Task RefreshMainUIAfterResolve()
        {
            try
            {
                var statusModule = svnManager?.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    SVNStatus.ClearLockCache();
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>[Warning] Main UI refresh failed:</color> {ex.Message}");
            }
        }

        private async Task RunWithLockAsync(Func<CancellationToken, Task> action)
        {
            if (IsDisposed) return;

            if (!TryEnterProcessing())
            {
                LogBoth("<color=#FFAA00>Another resolve operation is already in progress.</color>");
                return;
            }

            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _activeCts, cts);
            previousCts?.Cancel();
            previousCts?.Dispose();

            _activeTask = action(cts.Token);

            try
            {
                await _activeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange><b>[Resolve]</b> Operation canceled.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FF4444>Unhandled exception in RunWithLock:</color> {ex.Message}");
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _activeCts, null, cts) == cts)
                    cts.Dispose();

                _activeTask = null;
                ExitProcessing();
            }
        }

        private async Task<T> RunWithLockAsync<T>(Func<CancellationToken, Task<T>> action)
        {
            if (IsDisposed) return default;

            if (!TryEnterProcessing())
            {
                LogBoth("<color=#FFAA00>Another resolve operation is already in progress.</color>");
                return default;
            }

            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _activeCts, cts);
            previousCts?.Cancel();
            previousCts?.Dispose();

            var task = action(cts.Token);
            _activeTask = task;

            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange><b>[Resolve]</b> Operation canceled.</color>");
                return default;
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FF4444>Unhandled exception in RunWithLock:</color> {ex.Message}");
                return default;
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _activeCts, null, cts) == cts)
                    cts.Dispose();

                _activeTask = null;
                ExitProcessing();
            }
        }

        private string GetResolveToolPath()
        {
            string path = svnManager?.ResolveToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = svnUI?.SettingsResolveToolPathInput?.text;
            if (string.IsNullOrWhiteSpace(path))
                path = PlayerPrefs.GetString(SVNManager.KEY_RESOLVE_TOOL, "");
            return path?.Trim().Trim('"');
        }

        private bool TryLaunchExternalResolveTool(string conflictedFullPath, string relativePath, string toolPath)
        {
            if (string.IsNullOrEmpty(toolPath) || !File.Exists(toolPath))
                return false;

            try
            {
                string dir = Path.GetDirectoryName(conflictedFullPath);
                string fileName = Path.GetFileName(conflictedFullPath);
                string mineFile = conflictedFullPath + ".mine";

                var revFiles = Directory.GetFiles(dir, $"{fileName}.r*")
                    .Where(f => !f.EndsWith(".mine"))
                    .Select(f => new { Path = f, Rev = ExtractRevisionNumber(f) })
                    .Where(x => x.Rev.HasValue)
                    .OrderBy(x => x.Rev.Value)
                    .ToList();

                if (revFiles.Count < 2 || !File.Exists(mineFile))
                    return false;

                string baseFile = revFiles.First().Path;
                string theirsFile = revFiles.Last().Path;

                string processArgs = toolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0
                    ? $"/base:\"{baseFile}\" /mine:\"{mineFile}\" /theirs:\"{theirsFile}\" /merged:\"{conflictedFullPath}\""
                    : $"\"{baseFile}\" \"{mineFile}\" \"{theirsFile}\" \"{conflictedFullPath}\"";

                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = toolPath,
                    Arguments = processArgs,
                    UseShellExecute = true
                });

                if (process == null) return false;

                LogBoth($"<color=yellow>Launched 3-way resolve tool for:</color> {relativePath}");
                return true;
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>External resolve tool failed:</color> {ex.Message}");
                return false;
            }
        }

        private static int? ExtractRevisionNumber(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            int idx = fileName.LastIndexOf(".r", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string numStr = fileName.Substring(idx + 2);
            return int.TryParse(numStr, out int num) ? num : (int?)null;
        }

        private async Task<bool> HasConflictMarkersAsync(string fullPath)
        {
            if (!File.Exists(fullPath)) return false;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 5 * 1024 * 1024)
            {
                LogBoth($"<color=#888888>[Resolve]</color> Skipping marker scan for large file: {Path.GetFileName(fullPath)}");
                return false;
            }

            try
            {
                return await Task.Run(() =>
                {
                    bool hasStart = false, hasSeparator = false, hasEnd = false;
                    using var stream = new StreamReader(fullPath, Encoding.UTF8, true, 8192);
                    string line;
                    while ((line = stream.ReadLine()) != null)
                    {
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("<<<<<<<", StringComparison.Ordinal)) hasStart = true;
                        else if (hasStart && trimmed.StartsWith("=======", StringComparison.Ordinal)) hasSeparator = true;
                        else if (hasStart && hasSeparator && trimmed.StartsWith(">>>>>>>", StringComparison.Ordinal)) hasEnd = true;

                        if (hasStart && hasSeparator && hasEnd) return true;
                    }
                    return false;
                }).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string path) =>
            string.IsNullOrWhiteSpace(path)
                ? ""
                : path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim();

        private bool TryGetRelativePath(string root, string path, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

            string normRoot = Path.GetFullPath(root.Trim()).Replace('\\', '/').TrimEnd('/');
            string normInput = path.Replace('\\', '/').Trim();

            try
            {
                string absolutePath = Path.IsPathRooted(normInput)
                    ? Path.GetFullPath(normInput)
                    : Path.GetFullPath(Path.Combine(normRoot, normInput));

                absolutePath = absolutePath.Replace('\\', '/').TrimEnd('/');
                string prefix = normRoot + "/";

                if (!absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                relativePath = absolutePath.Substring(prefix.Length).Replace('\\', '/').Trim('/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateCacheFromLatest(List<SVNConflictData> latest)
        {
            foreach (var c in latest)
                _conflictCache[c.Path] = c;

            var valid = new HashSet<string>(latest.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _conflictCache.Keys.ToList())
            {
                if (!valid.Contains(key))
                    _conflictCache.TryRemove(key, out _);
            }
        }

        private sealed class NaturalStringComparer : IComparer<string>
        {
            public static readonly NaturalStringComparer Instance = new();

            public int Compare(string x, string y)
            {
                if (x == null) return y == null ? 0 : -1;
                if (y == null) return 1;

                int ix = 0, iy = 0;
                while (ix < x.Length && iy < y.Length)
                {
                    if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                    {
                        int jx = ix, jy = iy;
                        while (jx < x.Length && char.IsDigit(x[jx])) jx++;
                        while (jy < y.Length && char.IsDigit(y[jy])) jy++;

                        string nx = x.Substring(ix, jx - ix).TrimStart('0');
                        string ny = y.Substring(iy, jy - iy).TrimStart('0');

                        int cmp = string.Compare(nx, ny, StringComparison.Ordinal);
                        if (cmp != 0) return cmp;

                        ix = jx; iy = jy;
                    }
                    else
                    {
                        int cmp = x[ix].CompareTo(y[iy]);
                        if (cmp != 0) return cmp;
                        ix++; iy++;
                    }
                }
                return (x.Length - ix).CompareTo(y.Length - iy);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            try { CancelResolve(); } catch { }

            var task = Volatile.Read(ref _activeTask);
            if (task != null)
            {
                try
                {
                    task.Wait(TimeSpan.FromSeconds(3));
                }
                catch { }
            }

            _conflictCache.Clear();
            try { _operationLock.Dispose(); } catch { }

            var cts = Interlocked.Exchange(ref _activeCts, null);
            try { cts?.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}