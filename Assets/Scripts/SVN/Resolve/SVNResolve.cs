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
        private static readonly object PreviewLock = new object();

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

        // Tree conflicts — single reliable action per strategy. The underlying
        // implementation (ResolveAllTreeAsync -> force core per item) tries the
        // standard resolve first, then falls back to structural fix. There is no
        // separate "Force" variant — the old non-force path silently failed on
        // W195024 tree conflicts, so it has been consolidated into this one.
        public void ResolveAllTreeMine() => SafeFireAndForget(() => ResolveAllTreeAsync("mine-full"));
        public void ResolveAllTreeTheirs() => SafeFireAndForget(() => ResolveAllTreeAsync("theirs-full"));
        public void ResolveAllTreeBase() => SafeFireAndForget(() => ResolveAllTreeAsync("base"));

        public void CancelResolve()
        {
            try
            {
                var cts = Volatile.Read(ref _activeCts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
                LogBoth("<color=#8B0000>><b>[Resolve]</b> Cancel requested...</color>");
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

        public async Task OpenSingle(string path)
        {
            if (!SVNPathUtilities.TryGetRelativePath(svnManager.WorkingDir, path, out string relativePath))
            {
                LogBoth($"<color=#FFAA00>Invalid path (outside working copy):</color> {path}");
                return;
            }

            string textEditorPath = ResolveTextEditorPath();
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string full = Path.Combine(svnManager.WorkingDir, relativePath);
                if (!File.Exists(full))
                {
                    LogBoth($"<color=#FFAA00>File not found:</color> {relativePath}");
                    return;
                }

                if (SVNPathUtilities.IsBinaryFile(full))
                {
                    LogBoth($"<color=#FFAA00>Binary file — cannot be resolved by text editing:</color> {relativePath}");
                    LogBoth("<color=yellow>  Use Mine / Theirs / Base buttons in the conflict list.</color>");
                    LogBoth($"<color=yellow>  Current size: {new FileInfo(full).Length:N0} bytes</color>");
                    return;
                }

                bool launched = TryLaunchExternalResolveTool(full, relativePath, resolveToolPath);
                if (!launched)
                {
                    if (!TryOpenFileInEditor(full, relativePath, textEditorPath, token))
                    {
                        LogBoth("<color=#FFAA00>Could not open the file — configure a text editor in settings or check file associations.</color>");
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

        // Tree per-item — single reliable action per strategy, backed by the
        // same force-core path as bulk. No separate Force wrapper needed.
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

                    bool markers = await _resolver.HasConflictMarkersAsync(Path.Combine(root, c.Path), token).ConfigureAwait(false);
                    infos.Add((c.Path, ConvertConflictType(c), markers, c.TreeConflictReason));
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
            string textEditorPath = ResolveTextEditorPath();
            string targetFromUI = svnUI?.ResolveTargetFileInput?.text;
            string resolveToolPath = GetResolveToolPath();

            await RunWithLockAsync(async token =>
            {
                string root = svnManager.WorkingDir;
                string targetFile = !string.IsNullOrWhiteSpace(targetFromUI) ? targetFromUI.Trim() : null;

                if (string.IsNullOrEmpty(targetFile))
                {
                    targetFile = _conflictCache.Values
                        .Where(x => x.State != SVNConflictState.Resolved && x.Type != SVNConflictType.Binary)
                        .OrderBy(x => x.Path)
                        .FirstOrDefault()?.Path;

                    if (string.IsNullOrEmpty(targetFile))
                    {
                        var conflicts = await _parser.GetConflictsAsync(root, token).ConfigureAwait(false);
                        foreach (var c in conflicts)
                            _conflictCache.AddOrUpdate(c);

                        targetFile = conflicts
                            .Where(c => c.Type != SVNConflictType.Binary)
                            .FirstOrDefault()?.Path;

                        if (string.IsNullOrEmpty(targetFile) && conflicts.Count > 0)
                        {
                            LogBoth("<color=#FFAA00>All current conflicts are binary — use Mine/Theirs/Base buttons.</color>");
                            return;
                        }
                    }
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    LogBoth("<color=yellow>No conflicted file found.</color>");
                    return;
                }

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

                if (SVNPathUtilities.IsBinaryFile(full))
                {
                    LogBoth($"<color=#FFAA00>Binary file — cannot be resolved by text editing:</color> {normalizedTarget}");
                    LogBoth("<color=yellow>  Use Mine / Theirs / Base buttons in the conflict list.</color>");
                    return;
                }

                bool launched = TryLaunchExternalResolveTool(full, normalizedTarget, resolveToolPath);
                if (!launched)
                {
                    if (!TryOpenFileInEditor(full, normalizedTarget, textEditorPath, token))
                    {
                        LogBoth("<color=#FFAA00>Could not open the file — configure a text editor in settings or check file associations.</color>");
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

        private async Task RefreshAfterResolveAsync()
        {
            await svnManager.RefreshStatus().ConfigureAwait(false);
            svnManager.GetModule<SVNExternal>()?.RefreshWindowsShellIcons(svnManager.WorkingDir);
            await RefreshConflictUIAsync(CancellationToken.None).ConfigureAwait(false);

            _ = _backup.AutoCleanupIfNeededAsync();
        }

        #endregion

        #region Utility

        private SVNConflictItem.ConflictType ConvertConflictType(SVNConflictData data)
        {
            return data.Type switch
            {
                SVNConflictType.Tree => SVNConflictItem.ConflictType.Tree,
                SVNConflictType.Manual => SVNConflictItem.ConflictType.Manual,
                SVNConflictType.Binary => SVNConflictItem.ConflictType.Binary,
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
                LogBoth("<color=#8B0000>><b>[Resolve]</b> Operation canceled.</color>");
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
                LogBoth("<color=#8B0000>><b>[Resolve]</b> Operation canceled.</color>");
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

        private string ResolveTextEditorPath()
        {
            string editor = PlayerPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");
            if (!string.IsNullOrWhiteSpace(editor))
                return editor.Trim().Trim('"');

            return svnManager.MergeToolPath?.Trim().Trim('"') ?? "";
        }

        private bool TryOpenFileInEditor(
            string fullPath,
            string relativePath,
            string editorPath,
            CancellationToken token)
        {
            if (!string.IsNullOrEmpty(editorPath) && File.Exists(editorPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(editorPath, $"\"{fullPath}\"") { UseShellExecute = true });
                    LogBoth($"<color=green>Opening editor for:</color> {relativePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    LogBoth($"<color=#FFAA00>Configured editor failed: {ex.Message} — trying system default...</color>");
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                LogBoth($"<color=green>Opening file (system default):</color> {relativePath}");
                if (string.IsNullOrEmpty(editorPath))
                    LogBoth("<color=yellow>Note: no text editor configured. Set one in settings to edit working file directly.</color>");
                return true;
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>System default open failed: {ex.Message} — falling back to combined preview...</color>");
            }

            try
            {
                string previewPath = BuildConflictPreviewFile(fullPath, relativePath, token);
                if (!string.IsNullOrEmpty(previewPath))
                {
                    Process.Start(new ProcessStartInfo(previewPath) { UseShellExecute = true });
                    LogBoth($"<color=green>Opened conflict preview (read-only):</color> {relativePath}");
                    LogBoth($"<color=yellow>  Preview :</color> {SVNPathUtilities.ForDisplay(previewPath)}");
                    LogBoth("<color=yellow>  This is a snapshot of MERGED/MINE/BASE/THEIRS. Edit the working file to resolve.</color>");
                    LogBoth("<color=yellow>  Tip: configure a text editor in settings for direct editing.</color>");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Conflict preview failed: {ex.Message}</color>");
            }

            return false;
        }

        private static string BuildConflictPreviewFile(
            string conflictedFullPath,
            string relativePath,
            CancellationToken token)
        {
            string dir = Path.GetDirectoryName(conflictedFullPath);
            if (string.IsNullOrEmpty(dir)) return null;

            string fileName = Path.GetFileName(conflictedFullPath);
            string safeName = relativePath.Replace('/', '_').Replace('\\', '_');

            var revFiles = Directory.GetFiles(dir)
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

            string mineFile = conflictedFullPath + ".mine";
            bool hasMine = File.Exists(mineFile);
            bool hasMerged = File.Exists(conflictedFullPath);

            if (!hasMerged && !hasMine && revFiles.Count == 0)
                return null;

            string previewRoot = Path.Combine(SVNPrefs.TemporaryCachePath, "SVNConflictPreviews");
            Directory.CreateDirectory(previewRoot);
            CleanupOldPreviews(previewRoot);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string previewPath = Path.Combine(previewRoot, $"{safeName}_{stamp}.txt");

            var sb = new System.Text.StringBuilder(64 * 1024);

            sb.AppendLine("============================================================");
            sb.AppendLine($"CONFLICT PREVIEW: {relativePath}");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("============================================================");
            sb.AppendLine();
            sb.AppendLine("Read-only snapshot for review. Edit the original working file");
            sb.AppendLine("(not this preview) to resolve the conflict.");
            sb.AppendLine();

            if (hasMerged)
            {
                sb.AppendLine("============================================================");
                sb.AppendLine("MERGED (working file — contains conflict markers)");
                sb.AppendLine("============================================================");
                AppendFileContentSafe(sb, conflictedFullPath, token);
                sb.AppendLine();
            }

            if (hasMine)
            {
                sb.AppendLine("============================================================");
                sb.AppendLine("MINE (local changes)");
                sb.AppendLine("============================================================");
                AppendFileContentSafe(sb, mineFile, token);
                sb.AppendLine();
            }

            if (revFiles.Count >= 2)
            {
                sb.AppendLine("============================================================");
                sb.AppendLine($"BASE (common ancestor, r{revFiles.First().Rev})");
                sb.AppendLine("============================================================");
                AppendFileContentSafe(sb, revFiles.First().Path, token);
                sb.AppendLine();

                sb.AppendLine("============================================================");
                sb.AppendLine($"THEIRS (repository, r{revFiles.Last().Rev})");
                sb.AppendLine("============================================================");
                AppendFileContentSafe(sb, revFiles.Last().Path, token);
                sb.AppendLine();
            }
            else if (revFiles.Count == 1)
            {
                sb.AppendLine("============================================================");
                sb.AppendLine($"REVISION r{revFiles.First().Rev}");
                sb.AppendLine("============================================================");
                AppendFileContentSafe(sb, revFiles.First().Path, token);
                sb.AppendLine();
            }

            string tempPath = previewPath + ".tmp";
            lock (PreviewLock)
            {
                File.WriteAllText(tempPath, sb.ToString(), new System.Text.UTF8Encoding(true));
                if (File.Exists(previewPath)) File.Delete(previewPath);
                File.Move(tempPath, previewPath);
            }

            return previewPath;
        }

        private static void AppendFileContentSafe(
            System.Text.StringBuilder sb,
            string path,
            CancellationToken token)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int read;
                using (var fs = File.OpenRead(path))
                    read = fs.Read(buffer, 0, buffer.Length);

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == 0)
                    {
                        sb.AppendLine($"(binary content — {new FileInfo(path).Length} bytes, not shown)");
                        return;
                    }
                }

                using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true, 8192);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    sb.AppendLine(line);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sb.AppendLine($"(failed to read: {ex.Message})");
            }
        }

        private static void CleanupOldPreviews(string previewDir)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var f in Directory.GetFiles(previewDir, "*.txt"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                            File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool TryLaunchExternalResolveTool(string conflictedFullPath, string relativePath, string toolPath)
        {
            if (string.IsNullOrEmpty(toolPath) || !File.Exists(toolPath))
                return false;

            if (SVNPathUtilities.IsBinaryFile(conflictedFullPath))
                return false;

            try
            {
                string dir = Path.GetDirectoryName(conflictedFullPath);
                string fileName = Path.GetFileName(conflictedFullPath);
                string mineFile = conflictedFullPath + ".mine";

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

        #region Backup Management API (UI)

        public void CleanBackups() => SafeFireAndForget(async () =>
        {
            LogOverwrite("<color=yellow>[Backup] Cleaning old backups...</color>");
            await _backup.CleanupAsync().ConfigureAwait(false);
        });

        public void ShowBackupInfo() => SafeFireAndForget(async () =>
        {
            string info = await _backup.DescribeBackupsAsync().ConfigureAwait(false);
            LogBoth(info);
        });

        public void OpenBackupFolder() => SafeFireAndForget(() => Task.Run(() =>
        {
            string root = _backup.GetBackupRootForUi();
            if (string.IsNullOrEmpty(root))
            {
                LogBoth("<color=#FFAA00>[Backup] Backup folder unavailable.</color>");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
                LogBoth($"<color=yellow>[Backup] Opened folder:</color> {SVNPathUtilities.ForDisplay(root)}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>[Backup] Failed to open folder: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
            }
        }));

        public void PurgeAllBackups() => SafeFireAndForget(async () =>
        {
            LogOverwrite("<color=#FF4444>[Backup] PURGING ALL BACKUPS...</color>");
            await _backup.PurgeAllAsync().ConfigureAwait(false);
        });

        public bool IsBackupEnabled => _backup.BackupEnabled;

        public bool SetBackupEnabled(bool enabled)
        {
            _backup.BackupEnabled = enabled;

            LogBoth(enabled
                ? "<color=green>[Backup] ENABLED — backups before destructive resolve steps ON.</color>"
                : "<color=#FF4444>[Backup] DISABLED — NO backups, obstruction deletes are PERMANENT (faster).</color>");

            return enabled;
        }

        public bool ToggleBackupEnabled() => SetBackupEnabled(!_backup.BackupEnabled);
        public string DescribeBackupEnabled() => _backup.BackupEnabled ? "on" : "off";

        public int GetBackupRetentionDays() => _backup.RetentionDays;

        public string SetBackupRetentionDays(int days)
        {
            _backup.RetentionDays = days;
            string desc = DescribeBackupRetention();
            LogBoth($"<color=yellow>[Backup] Retention set to: {desc}</color>");
            return desc;
        }

        public string DescribeBackupRetention() =>
            _backup.RetentionDays <= 0 ? "off" : $"{_backup.RetentionDays}d";

        public int GetBackupMaxSizeMB() => _backup.MaxSizeMB;

        public string SetBackupMaxSizeMB(int mb)
        {
            _backup.MaxSizeMB = mb;
            string desc = DescribeBackupMaxSize();
            LogBoth($"<color=yellow>[Backup] Size cap set to: {desc}</color>");
            return desc;
        }

        public string DescribeBackupMaxSize() =>
            _backup.MaxSizeMB <= 0 ? "no cap"
            : _backup.MaxSizeMB >= 1024 ? $"{_backup.MaxSizeMB / 1024.0:0.#} GB"
            : $"{_backup.MaxSizeMB} MB";

        #endregion
    }
}