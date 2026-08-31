using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNResolve : SVNBase, IDisposable
    {
        private readonly SVNConflictCache _conflictCache;
        private readonly SVNBackupManager _backup;
        private readonly SVNConflictParser _parser;
        private readonly SVNConflictResolver _resolver;

        private CancellationTokenSource _activeCts;
        private Task _activeTask;
        private int _processingFlag;
        private int _uiRefreshingFlag;
        private int _disposed;

        public bool IsResolveBusy => Volatile.Read(ref _processingFlag) == 1;

        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _conflictCache = new SVNConflictCache();
            _backup = new SVNBackupManager(manager, LogBoth);
            _parser = new SVNConflictParser(manager, _conflictCache, LogBoth);
            _resolver = new SVNConflictResolver(manager, _parser, _conflictCache, _backup, LogBoth, LogOverwrite);
        }

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
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveSingleCoreAsync(path, "mine-full", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> ResolveSingleTheirs(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveSingleCoreAsync(path, "theirs-full", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task RefreshConflictUI() =>
            await RefreshConflictUIAsync(CancellationToken.None).ConfigureAwait(false);

        // === FIX P1: normalizacja path przez TryGetRelativePath (jak ResolveTree...).
        // Path.Combine(root, absolutePath) na Windows POMIJA root — plik mógłby
        // być otwierany z poza working copy lub w złym miejscu.
        public async Task OpenSingle(string path)
        {
            if (!SVNPathUtilities.TryGetRelativePath(svnManager.WorkingDir, path, out string relativePath))
            {
                LogBoth($"<color=#FFAA00>Invalid path (outside working copy):</color> {path}");
                return;
            }

            string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string full = Path.Combine(svnManager.WorkingDir, relativePath);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {relativePath}");
                    return;
                }

                if (!TryLaunchExternalResolveTool(full, relativePath, resolveToolPath))
                {
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        LogBoth("<color=#FFAA00>Merge tool path missing!</color>");
                        return;
                    }

                    try
                    {
                        LogBoth($"Opening editor for: <color=green>{relativePath}</color>");
                        Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>Failed to launch editor:</color> {ex.Message}");
                        return;
                    }
                }

                var existing = _conflictCache.Get(relativePath) ?? new SVNConflictData { Path = relativePath };

                _conflictCache.AddOrUpdate(new SVNConflictData
                {
                    Path = existing.Path,
                    Type = existing.Type == SVNConflictType.Tree ? SVNConflictType.Tree : SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing,
                    TreeConflictReason = existing.TreeConflictReason,
                    TreeConflictAction = existing.TreeConflictAction,
                    TreeConflictVictim = existing.TreeConflictVictim,
                    TreeConflictNodeKind = existing.TreeConflictNodeKind
                });

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
            });
        }

        public async Task<bool> MarkSingleResolved(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.MarkSingleResolvedAsync(path, token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> ResolveTreeMine(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveTreeStrategyAsync(path, "mine-full", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> ResolveTreeTheirs(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveTreeStrategyAsync(path, "theirs-full", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> ResolveTreeBase(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveTreeStrategyAsync(path, "base", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> ResolveTreeWorking(string path) =>
            await RunWithLockAsync(async token =>
            {
                bool ok = await _resolver.ResolveTreeStrategyAsync(path, "working", token).ConfigureAwait(false);
                if (ok) await RefreshAfterResolveAsync().ConfigureAwait(false);
                else await RefreshConflictUIAsync(token).ConfigureAwait(false);
                return ok;
            }).ConfigureAwait(false);

        public async Task<bool> DeleteObstruction(string path, bool refreshUi = true) =>
            await DeleteObstructionAsync(path, refreshUi).ConfigureAwait(false);

        public async Task<bool> DeleteObstructionAsync(string path, bool refreshUi = true)
        {
            return await RunWithLockAsync(async token =>
            {
                bool success = await _resolver.DeleteObstructionCoreAsync(path, token).ConfigureAwait(false);
                if (refreshUi)
                {
                    await RefreshConflictUIAsync(token).ConfigureAwait(false);
                    await RefreshMainUIAfterResolve().ConfigureAwait(false);
                }
                return success;
            }).ConfigureAwait(false);
        }

        #endregion

        #region UI Refresh

        public async Task AutoRefreshConflictListAsync(CancellationToken externalToken)
        {
            await RunWithLockAsync(async internalToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalToken, externalToken);
                var combinedToken = linkedCts.Token;

                LogOverwrite("<color=yellow>Scanning for conflicts...</color>");
                var conflicts = await _parser.GetConflictsAsync(svnManager.WorkingDir, combinedToken).ConfigureAwait(false);
                if (combinedToken.IsCancellationRequested) return;

                await RefreshConflictUIAsync(combinedToken).ConfigureAwait(false);

                if (conflicts.Count == 0)
                    LogOverwrite("<color=green>No conflicts found.</color>");
                else
                    LogOverwrite($"<color=green>Conflicts refreshed: {conflicts.Count} found.</color>");
            });
        }

        public async Task RefreshConflictUIAsync(CancellationToken token = default)
        {
            if (svnUI?.ResolveConsoleContent == null || svnUI.ConflictPrefab == null || IsDisposed) return;
            if (!TryEnterUiRefresh()) return;

            try
            {
                var root = svnManager.WorkingDir;
                var conflicts = await _parser.GetConflictsAsync(root, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                var infos = new List<(string path, SVNConflictItem.ConflictType type, bool markers, string treeReason)>();

                foreach (var c in conflicts)
                {
                    token.ThrowIfCancellationRequested();

                    // === FIX (konsolidacja): użyj wspólnej implementacji z Core (przez resolver)
                    // (wraz z token dla cancel w trakcie skanu)
                    bool markers = await _resolver.HasConflictMarkersAsync(Path.Combine(root, c.Path), token).ConfigureAwait(false);
                    infos.Add((c.Path, ConvertConflictType(c.Type), markers, c.TreeConflictReason));
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                UnityMainThreadDispatcher.Enqueue(() =>
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
                            UnityEngine.Object.DestroyImmediate(child);
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

                // === FIX 10: delay bez tokenu — bez fałszywego timeout przy cancel.
                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
                if (completed != tcs.Task && !token.IsCancellationRequested)
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

        #region Private Helpers

        private async Task OpenInEditorAsync()
        {
            string editorPath = svnManager.MergeToolPath ?? PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
            string targetFromUI = svnUI?.ResolveTargetFileInput?.text;
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string root = svnManager.WorkingDir;
                string targetFile = !string.IsNullOrWhiteSpace(targetFromUI) ? targetFromUI.Trim() : null;

                if (string.IsNullOrEmpty(targetFile))
                {
                    targetFile = _conflictCache.Values
                        .OrderBy(x => x.Path)
                        .FirstOrDefault(x => x.State != SVNConflictState.Resolved)?.Path;

                    if (string.IsNullOrEmpty(targetFile))
                    {
                        var conflicts = await _parser.GetConflictsAsync(root, token).ConfigureAwait(false);
                        foreach (var c in conflicts)
                            _conflictCache.AddOrUpdate(c);
                        targetFile = conflicts.FirstOrDefault()?.Path;
                    }
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    LogBoth("<color=yellow>No conflicted file found.</color>");
                    return;
                }

                // === FIX P1: normalizacja przez TryGetRelativePath
                if (!SVNPathUtilities.TryGetRelativePath(root, targetFile, out string normalizedTarget))
                {
                    LogBoth($"<color=#FFAA00>Invalid path (outside working copy):</color> {targetFile}");
                    return;
                }

                string full = Path.Combine(root, normalizedTarget);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {normalizedTarget}");
                    return;
                }

                if (!TryLaunchExternalResolveTool(full, normalizedTarget, resolveToolPath))
                {
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        LogBoth("<color=#FFAA00>Merge tool path missing!</color>");
                        return;
                    }

                    try
                    {
                        LogBoth($"Opening editor for: <color=green>{normalizedTarget}</color>");
                        Process.Start(new ProcessStartInfo(editorPath, $"\"{full}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>Failed to launch editor:</color> {ex.Message}");
                        return;
                    }
                }

                var existing = _conflictCache.Get(normalizedTarget) ?? new SVNConflictData { Path = normalizedTarget };

                _conflictCache.AddOrUpdate(new SVNConflictData
                {
                    Path = existing.Path,
                    Type = existing.Type == SVNConflictType.Tree ? SVNConflictType.Tree : SVNConflictType.Manual,
                    State = SVNConflictState.ManualEditing,
                    TreeConflictReason = existing.TreeConflictReason,
                    TreeConflictAction = existing.TreeConflictAction,
                    TreeConflictVictim = existing.TreeConflictVictim,
                    TreeConflictNodeKind = existing.TreeConflictNodeKind
                });

                await RefreshConflictUIAsync(token).ConfigureAwait(false);
            });
        }

        private async Task ResolveAllConflictsAsync(string strategy)
        {
            await RunWithLockAsync(token => _resolver.ResolveAllConflictsAsync(strategy, token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task ResolveAllTreeAsync(string strategy)
        {
            await RunWithLockAsync(token => _resolver.ResolveAllTreeAsync(strategy, token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task MarkAsResolvedAsync()
        {
            await RunWithLockAsync(token => _resolver.ResolveAllConflictsAsync("working", token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task DeleteAllObstructionsAsync()
        {
            await RunWithLockAsync(token => _resolver.DeleteAllObstructionsAsync(token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task ResolveAllTreeForceAsync(string strategy)
        {
            await RunWithLockAsync(token => _resolver.ResolveAllTreeForceAsync(strategy, token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task ResolveTreeForceAsync(string path, string strategy)
        {
            await RunWithLockAsync(token => _resolver.ResolveTreeForceAsync(path, strategy, token)).ConfigureAwait(false);
            await RefreshAfterResolveAsync().ConfigureAwait(false);
        }

        private async Task RefreshAfterResolveAsync()
        {
            await svnManager.RefreshStatus().ConfigureAwait(false);
            svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);
            await RefreshConflictUIAsync(CancellationToken.None).ConfigureAwait(false);
        }

        #endregion

        #region Utility

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

        // === FIX P1 (Dispose): usunięty task.Wait(3s) — nie blokuj main thread.
        // Problem: Dispose z main thread Unity + task czeka na UnityMainThreadDispatcher
        // → deadlock. Teraz: Cancel + fire-and-forget — task skończy się naturalnie.
        // Delayed dispose CTS (zgodnie z patternem projektowym).
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
            if (previousCts != null)
            {
                previousCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ => { try { previousCts.Dispose(); } catch { } });
            }

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
                    _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });

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
            if (previousCts != null)
            {
                previousCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ => { try { previousCts.Dispose(); } catch { } });
            }

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
                    _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });

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

                // === FIX 16: Directory.GetFiles z patternem puszczał znaki glob
                // ([, *, ?) z nazwy pliku. Ręczny filtr jest odporny na dowolne nazwy.
                var revFiles = Directory.GetFiles(dir ?? ".")
                    .Where(f =>
                    {
                        string fn = Path.GetFileName(f);
                        return fn.StartsWith(fileName + ".r", StringComparison.OrdinalIgnoreCase)
                               && !fn.EndsWith(".mine", StringComparison.OrdinalIgnoreCase);
                    })
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

        // === FIX P1 (Dispose): bez task.Wait() — nie blokuj main thread Unity.
        // Cancel + fire-and-forget. Task skończy się naturalnie (SvnRunner kill na cancel).
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            try { CancelResolve(); } catch { }

            _conflictCache.Clear();

            var cts = Interlocked.Exchange(ref _activeCts, null);
            if (cts != null)
            {
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}