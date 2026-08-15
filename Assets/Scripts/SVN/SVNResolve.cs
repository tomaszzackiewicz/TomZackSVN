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
        private int _processingFlag;
        private int _uiRefreshingFlag;
        private int _disposed;

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

        private async Task OpenInEditorAsync()
        {
            await RunWithLockAsync(async token =>
            {
                string root = svnManager.WorkingDir;
                string targetFile = !string.IsNullOrWhiteSpace(svnUI?.ResolveTargetFileInput?.text)
                    ? NormalizePath(svnUI.ResolveTargetFileInput.text.Trim())
                    : null;

                if (string.IsNullOrEmpty(targetFile))
                {

                    targetFile = _conflictCache.Values
                        .OrderBy(x => x.Path)
                        .FirstOrDefault(x => x.State != SVNConflictState.Resolved)?.Path;

                    if (string.IsNullOrEmpty(targetFile))
                    {
                        var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);
                        foreach (var c in conflicts) _conflictCache.AddOrUpdate(c.Path, c, (_, __) => c);
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

                if (!TryLaunchExternalResolveTool(full, targetFile))
                {
                    string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
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
                    Type = SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing
                };

                await RefreshConflictUIAsync().ConfigureAwait(false);
            });
        }

        public async Task OpenSingle(string path)
        {
            await RunWithLockAsync(async token =>
            {
                string full = Path.Combine(svnManager.WorkingDir, path);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {path}");
                    return;
                }

                if (!TryLaunchExternalResolveTool(full, path))
                {
                    string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
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
                    Type = SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing
                };

                await RefreshConflictUIAsync().ConfigureAwait(false);
            });
        }

        public void AutoRefreshConflictList() => SafeFireAndForget(AutoRefreshConflictListAsync);
        public void MarkAsResolved() => SafeFireAndForget(MarkAsResolvedAsync);
        public void DeleteAllObstructions() => SafeFireAndForget(DeleteAllObstructionsAsync);
        public void ResolveTheirs() => SafeFireAndForget(() => ResolveBatchAsync("theirs-full"));
        public void ResolveMine() => SafeFireAndForget(() => ResolveBatchAsync("mine-full"));
        public void OpenInEditor() => SafeFireAndForget(OpenInEditorAsync);
        public void ResolveAllMine() => SafeFireAndForget(() => ResolveAllAsync("mine-full"));
        public void ResolveAllTheirs() => SafeFireAndForget(() => ResolveAllAsync("theirs-full"));

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

        private async Task ResolveBatchAsync(string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);

                int total = conflicts.Count;
                LogOverwrite($"<color=yellow>Starting {strategy} for {total} files...</color>");

                int successCount = 0;
                var failedFiles = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var c = conflicts[i];
                    if (c == null) continue;

                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] Resolving: {c.Path}</color>");

                    var result = await ResolveSingleCoreSilentAsync(c.Path, strategy, token).ConfigureAwait(false);
                    if (result.success)
                        successCount++;
                    else
                        failedFiles.Add(result.path);
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                _conflictCache.Clear();
                var latest = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                foreach (var c in latest) _conflictCache[c.Path] = c;

                await RefreshConflictUIAsync().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                if (failedFiles.Count == 0)
                    LogOverwrite($"<color=green>Successfully resolved all {successCount}/{total} conflicts ({strategy}).</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
            });
        }

        public async Task ResolveSingleMine(string path) => await RunWithLockAsync(token => ResolveSingleCoreAsync(path, "mine-full", token));
        public async Task ResolveSingleTheirs(string path) => await RunWithLockAsync(token => ResolveSingleCoreAsync(path, "theirs-full", token));

        public async Task RefreshConflictUI() => await RefreshConflictUIAsync().ConfigureAwait(false);

        private static void SafeFireAndForget(Func<Task> operation)
        {
            try
            {
                operation().ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    if (t.IsFaulted && t.Exception != null)
                        SVNLogBridge.LogException(t.Exception.InnerException ?? t.Exception);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogException(ex);
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

        private bool TryLaunchExternalResolveTool(string conflictedFullPath, string relativePath)
        {
            string toolPath = GetResolveToolPath();
            if (string.IsNullOrEmpty(toolPath) || !File.Exists(toolPath)) return false;

            try
            {
                string dir = Path.GetDirectoryName(conflictedFullPath);
                string fileName = Path.GetFileName(conflictedFullPath);
                string mineFile = conflictedFullPath + ".mine";
                var revFiles = Directory.GetFiles(dir, $"{fileName}.r*")
                    .Where(f => !f.EndsWith(".mine"))
                    .OrderBy(f => f, NaturalStringComparer.Instance)
                    .ToList();

                if (revFiles.Count < 2 || !File.Exists(mineFile)) return false;

                string baseFile = revFiles.First();
                string theirsFile = revFiles.Last();
                string processArgs = toolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0
                    ? $"/base:\"{baseFile}\" /mine:\"{mineFile}\" /theirs:\"{theirsFile}\" /merged:\"{conflictedFullPath}\""
                    : $"\"{baseFile}\" \"{mineFile}\" \"{theirsFile}\" \"{conflictedFullPath}\"";

                var process = Process.Start(new ProcessStartInfo { FileName = toolPath, Arguments = processArgs, UseShellExecute = true });
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

        private async Task AutoRefreshConflictListAsync()
        {
            await RunWithLockAsync(async token =>
            {
                if (IsDisposed) return;

                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root)) return;

                await Task.Delay(120).ConfigureAwait(false);
                await GetConflictsAsync(root).ConfigureAwait(false);
                await RefreshConflictUIAsync().ConfigureAwait(false);
            });
        }

        private async Task RunWithLockAsync(Func<CancellationToken, Task> operation)
        {
            if (!TryEnterProcessing()) return;

            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock)
                {
                    LogBoth("<color=yellow>[Resolve] Another operation is already running. Please wait.</color>");
                    return;
                }

                var cts = new CancellationTokenSource();
                Volatile.Write(ref _activeCts, cts);

                await operation(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange><b>[Resolve]</b> Operation was cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Operation error:</color> {ex.Message}");
            }
            finally
            {
                var oldCts = Volatile.Read(ref _activeCts);
                if (oldCts != null)
                {
                    Volatile.Write(ref _activeCts, null);
                    try { oldCts.Dispose(); } catch { }
                }

                if (hasLock)
                {
                    try { _operationLock.Release(); } catch { }
                }

                ExitProcessing();
            }
        }

        private async Task<(bool success, string path, string error)> ResolveSingleCoreSilentAsync(string rawPath, string strategy, CancellationToken token)
        {
            if (IsDisposed) return (false, rawPath, "Module disposed");

            if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath, "Invalid path");

            if (_conflictCache.TryGetValue(path, out var data))
            {
                _conflictCache[path] = new SVNConflictData
                {
                    Path = data.Path,
                    Type = data.Type,
                    State = SVNConflictState.Resolving
                };
            }

            bool resolved = false;
            string errorMsg = null;

            try
            {
                await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                resolved = true;
            }
            catch (Exception ex) when (ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
            {
                string fallbackStrategy = strategy.Replace("-full", "-conflict");
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    resolved = true;
                }
                catch (Exception fallbackEx)
                {
                    errorMsg = fallbackEx.Message;
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            if (resolved)
            {
                try
                {
                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _conflictCache.TryRemove(path, out _);
                }
                catch { }
            }

            return (resolved, path, errorMsg);
        }

        private async Task ResolveSingleCoreAsync(string rawPath, string strategy, CancellationToken token)
        {
            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            LogBoth($"[Resolve] {strategy} -> {rawPath}");

            var result = await ResolveSingleCoreSilentAsync(rawPath, strategy, token).ConfigureAwait(false);

            if (result.success)
                LogBoth($"<color=green>Resolved:</color> {result.path}");
            else
                LogBoth($"<color=#FF4444>Resolution failed for:</color> {result.path} {(!string.IsNullOrEmpty(result.error) ? $"({result.error})" : "")}");
        }

        private async Task MarkAsResolvedAsync()
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
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
                    await RefreshConflictUIAsync().ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);

                    svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                    LogOverwrite($"<color=green>Successfully marked {successCount}/{clean.Count} files as resolved.</color>");
                }
                else
                {
                    LogOverwrite("<color=yellow>No files were marked.</color>");
                }

                if (blocked.Count > 0)
                {
                    LogBoth($"<color=#FFAA00>{blocked.Count} conflict(s) need manual action:</color>");
                    foreach (var c in blocked) LogBoth($"<color=#FFAA00>  • {c.Path}</color>");
                }
            });
        }

        private async Task ResolveAllAsync(string strategy)
        {
            await RunWithLockAsync(async token =>
            {
                var paths = _conflictCache.Values
                    .Where(x => x != null)
                    .Select(x => x.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = paths.Count;
                LogOverwrite($"<color=yellow>Starting {strategy} for {total} cached conflicts...</color>");

                int successCount = 0;

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] Resolving cached conflict...</color>");

                    var result = await ResolveSingleCoreSilentAsync(paths[i], strategy, token).ConfigureAwait(false);
                    if (result.success)
                        successCount++;
                }

                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                _conflictCache.Clear();
                var latest = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                foreach (var c in latest) _conflictCache[c.Path] = c;

                await RefreshConflictUIAsync().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                int remainingCount = _conflictCache.Count;

                if (remainingCount == 0)
                    LogOverwrite($"<color=green>All conflicts resolved ({strategy}).</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Remaining: {remainingCount}</color>");
            });
        }

        private void LogOverwrite(string msg)
        {
            PostToMainThread(() =>
            {
                if (svnUI?.ResolveLogConsole != null)
                    SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", false); // false = nadpisz, nie dopisuj
            });
        }

        private async Task DeleteAllObstructionsAsync()
        {
            await RunWithLockAsync(async token =>
            {
                var conflicts = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
                var treeConflicts = conflicts.Where(x => x.Type == SVNConflictType.Tree).ToList();

                if (treeConflicts.Count == 0)
                {
                    LogOverwrite("<color=yellow>No tree obstructions found.</color>");
                    return;
                }

                int total = treeConflicts.Count;
                LogOverwrite($"<color=#FF4444><b>DELETING {total} TREE CONFLICTS...</b></color>");

                int successCount = 0;
                var failedPaths = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var c = treeConflicts[i];

                    LogOverwrite($"<color=yellow>[{i + 1}/{total}] Removing obstruction: {c.Path}</color>");

                    var result = await DeleteObstructionCoreSilentAsync(c.Path, token).ConfigureAwait(false);
                    if (result.success)
                        successCount++;
                    else
                        failedPaths.Add(result.path);
                }

                await RefreshConflictUIAsync().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);

                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);

                if (failedPaths.Count == 0)
                    LogOverwrite($"<color=green>Successfully removed all {successCount} obstructions. You MUST perform Revert or Commit to fix the project state.</color>");
                else
                    LogOverwrite($"<color=#FFAA00>Removed {successCount}/{total} obstructions. Failed: {failedPaths.Count}</color>");
            });
        }

        public async Task RefreshConflictUIAsync()
        {
            if (svnUI?.ResolveConsoleContent == null || svnUI.ConflictPrefab == null || IsDisposed) return;
            if (!TryEnterUiRefresh()) return;

            try
            {
                var root = svnManager.WorkingDir;
                var conflicts = await GetConflictsAsync(root).ConfigureAwait(false);

                var infos = new List<(string path, SVNConflictItem.ConflictType type, bool markers)>();
                foreach (var c in conflicts)
                {
                    bool markers = await HasConflictMarkersAsync(Path.Combine(root, c.Path)).ConfigureAwait(false);
                    infos.Add((c.Path, ConvertConflictType(c.Type), markers));
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                PostToMainThread(() =>
                {
                    try
                    {
                        var parent = svnUI.ResolveConsoleContent.transform;

                        for (int i = parent.childCount - 1; i >= 0; i--)
                        {
                            var child = parent.GetChild(i).gameObject;
                            child.transform.SetParent(null);
                            GameObject.Destroy(child);
                        }

                        foreach (var info in infos)
                        {
                            var obj = GameObject.Instantiate(svnUI.ConflictPrefab, parent);
                            obj.SetActive(true);
                            var item = obj.GetComponent<SVNConflictItem>();
                            item?.Setup(info.path, info.type, info.markers);
                        }
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
            }
            finally
            {
                ExitUiRefresh();
            }
        }

        public async Task MarkSingleResolved(string path)
        {
            await RunWithLockAsync(async token =>
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string fullPath = Path.Combine(svnManager.WorkingDir, path);

                if (_conflictCache.TryGetValue(path, out var data) && data.Type == SVNConflictType.Tree)
                {
                    LogBoth($"<color=#FFAA00>Tree conflict requires explicit obstruction deletion:</color> {path}");
                    return;
                }

                if (File.Exists(fullPath) && await HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
                {
                    LogBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}");
                    return;
                }

                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                _conflictCache.TryRemove(path, out _);
                await Task.Delay(150).ConfigureAwait(false);
                await RefreshConflictUIAsync().ConfigureAwait(false);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshMainUIAfterResolve().ConfigureAwait(false);
                LogBoth($"<color=green>Resolved manually:</color> {path}");
            });
        }

        public async Task DeleteObstruction(string path, bool refreshUi = true) => await DeleteObstructionAsync(path, refreshUi).ConfigureAwait(false);

        public async Task DeleteObstructionAsync(string path, bool refreshUi = true)
        {
            await RunWithLockAsync(async token =>
            {
                await DeleteObstructionCoreAsync(path, token).ConfigureAwait(false);

                if (refreshUi)
                {
                    await RefreshConflictUIAsync().ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                }
            });
        }

        private async Task DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            if (!TryGetRelativePath(svnManager.WorkingDir, rawPath, out string path))
            {
                LogBoth($"<color=#FFAA00>Invalid path:</color> {rawPath}");
                return;
            }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            string fullPath = Path.Combine(svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);
            LogBoth($"[TREE RESOLVE] {path} (exists: {fileExists})");

            if (fileExists)
            {
                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex.Message}");
                    return;
                }
            }
            else
            {
                try
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (Exception revertEx)
                {
                    LogBoth($"<color=#FFFF00>Revert failed:</color> {revertEx.Message} - trying theirs-full...");
                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (Exception theirsEx)
                    {
                        LogBoth($"<color=#FFAA00>theirs-full failed:</color> {theirsEx.Message}");
                    }
                }

                try
                {
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (Exception workingEx)
                {
                    LogBoth($"<color=#FFAA00>resolve working failed:</color> {workingEx.Message}");
                }
            }

            await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

            var remaining = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
            string normalizedPath = NormalizePath(path);
            bool stillExists = remaining.Any(c => NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists)
            {
                LogBoth($"<color=#FF4444>Tree conflict still exists:</color> {path}");
            }
            else
            {
                _conflictCache.TryRemove(normalizedPath, out _);
                await svnManager.RefreshStatus().ConfigureAwait(false);
                svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(path);
                LogBoth($"<color=green>Tree conflict resolved:</color> {path}");
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
                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch { return (false, path); }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch
                {
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch { }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch { }
            }

            try { await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch { }

            var remaining = await GetConflictsAsync(svnManager.WorkingDir).ConfigureAwait(false);
            string normalizedPath = NormalizePath(path);
            bool stillExists = remaining.Any(c => NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists)
            {
                return (false, path);
            }
            else
            {
                _conflictCache.TryRemove(normalizedPath, out _);
                return (true, path);
            }
        }

        private static string NormalizePath(string path) =>
            string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim();

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
                if (!absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

                relativePath = absolutePath.Substring(prefix.Length).Replace('\\', '/').Trim('/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch
            {
                return false;
            }
        }

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
                    if (!valid.Contains(key)) _conflictCache.TryRemove(key, out _);

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

            try
            {
                CancelResolve();
            }
            catch { }

            _conflictCache.Clear();
            try { _operationLock.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }
    }
}