using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNConflictCore
    {
        private readonly SVNManager _svnManager;
        private readonly SVNConflictParser _parser;
        private readonly SVNConflictCache _cache;
        private readonly SVNBackupManager _backup;
        private readonly Action<string> _logBoth;
        private readonly Action<string> _logOverwrite;

        public SVNConflictCore(
            SVNManager manager,
            SVNConflictParser parser,
            SVNConflictCache cache,
            SVNBackupManager backup,
            Action<string> logBoth,
            Action<string> logOverwrite)
        {
            _svnManager = manager;
            _parser = parser;
            _cache = cache;
            _backup = backup;
            _logBoth = logBoth;
            _logOverwrite = logOverwrite;
        }

        #region Single-file resolve

        public async Task<(bool success, string path, string error)> ResolveSingleCoreSilentAsync(
    string rawPath, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath, "Invalid path");

            if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                var baseCached = _cache.Get(path);
                if (baseCached != null)
                {
                    _cache.AddOrUpdate(new SVNConflictData
                    {
                        Path = path,
                        Type = baseCached.Type,
                        State = SVNConflictState.Resolving,
                        TreeConflictReason = baseCached.TreeConflictReason,
                        TreeConflictAction = baseCached.TreeConflictAction,
                        TreeConflictVictim = baseCached.TreeConflictVictim,
                        TreeConflictNodeKind = baseCached.TreeConflictNodeKind
                    });
                }

                try
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);

                    var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                    bool stillConflicted = remaining.Any(c =>
                        SVNPathUtilities.NormalizePath(c.Path).Equals(
                            SVNPathUtilities.NormalizePath(path), StringComparison.OrdinalIgnoreCase));

                    if (stillConflicted)
                    {
                        if (baseCached != null)
                            _cache.AddOrUpdate(baseCached);
                        return (false, path, "Conflict still present after base resolve (possible tree/obstruction)");
                    }

                    _cache.Remove(SVNPathUtilities.NormalizePath(path));
                    return (true, path, null);
                }
                catch (OperationCanceledException)
                {
                    if (baseCached != null)
                        _cache.AddOrUpdate(baseCached);
                    throw;
                }
                catch (Exception ex)
                {
                    if (baseCached != null)
                        _cache.AddOrUpdate(baseCached);
                    return (false, path, ex.Message);
                }
            }

            var data = _cache.Get(path);
            if (data != null)
            {
                _cache.AddOrUpdate(new SVNConflictData
                {
                    Path = path,
                    Type = data.Type,
                    State = SVNConflictState.Resolving,
                    TreeConflictReason = data.TreeConflictReason,
                    TreeConflictAction = data.TreeConflictAction,
                    TreeConflictVictim = data.TreeConflictVictim,
                    TreeConflictNodeKind = data.TreeConflictNodeKind
                });
            }

            bool resolved = false;
            string errorMsg = null;

            try
            {
                await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                resolved = true;
            }
            catch (OperationCanceledException)
            {
                if (data != null) _cache.AddOrUpdate(data);
                throw;
            }
            catch (Exception ex) when ((ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
                                        && strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackStrategy = strategy.Replace("-full", "-conflict");
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    resolved = true;
                }
                catch (OperationCanceledException)
                {
                    if (data != null) _cache.AddOrUpdate(data);
                    throw;
                }
                catch (Exception fallbackEx) { errorMsg = fallbackEx.Message; }
            }
            catch (Exception ex) { errorMsg = ex.Message; }

            if (resolved)
            {
                _cache.Remove(SVNPathUtilities.NormalizePath(path));

                try
                {
                    await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            else if (data != null)
            {
                _cache.AddOrUpdate(data);
            }

            return (resolved, path, errorMsg);
        }

        #endregion

        #region Tree force resolve

        public async Task<(bool success, string error)> ResolveTreeForceCoreAsync(
    SVNConflictData conflict, string strategy, CancellationToken token)
        {
            _logBoth($"<color=green>[FORCE CORE] strategy={strategy} path={conflict.Path}</color>");

            string path = conflict.Path;
            string fullPath = Path.Combine(_svnManager.WorkingDir, path);

            bool isTheirs = strategy.Contains("theirs", StringComparison.OrdinalIgnoreCase);
            bool isMine = strategy.Contains("mine", StringComparison.OrdinalIgnoreCase);
            bool isBase = strategy.Equals("base", StringComparison.OrdinalIgnoreCase);

            try
            {
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"",
                        _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _cache.Remove(SVNPathUtilities.NormalizePath(path));
                    return (true, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (SVNErrorHelper.IsInapplicableOrObstruction(ex))
                {
                    _logBoth($"<color=yellow>  -> Standard resolve not applicable ({SVNErrorHelper.GetShortError(ex)}). Forcing structural fix...</color>");
                }

                if (strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
                {
                    string conflictVariant = strategy.Replace("-full", "-conflict");
                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept {conflictVariant} \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        _cache.Remove(SVNPathUtilities.NormalizePath(path));
                        return (true, null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                bool itemExists = File.Exists(fullPath) || Directory.Exists(fullPath);

                if (isTheirs)
                {
                    string backupPath = null;
                    if (itemExists)
                    {
                        _logBoth($"<color=yellow>  -> Creating backup before removing obstruction: {path}</color>");
                        backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"delete \"{path}\" --force",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception delEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> svn delete failed: {SVNErrorHelper.GetShortError(delEx)}</color>");

                        if (SVNErrorHelper.IsInapplicableOrObstruction(delEx))
                        {
                            if (backupPath != null)
                            {
                                _logBoth("<color=#AAAAAA>  -> Obstruction confirmed, backup exists. Removing local file...</color>");
                                bool deleted = PermanentDelete(fullPath, _logBoth);
                                if (!deleted)
                                {
                                    _logBoth("<color=#FF4444>  -> Local delete also failed — aborting force resolve.</color>");
                                    return (false, "Failed to remove obstruction (both svn delete and local delete failed)");
                                }
                            }
                            else
                            {
                                await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false);
                                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                                {
                                    _logBoth("<color=#FF4444>  -> SafeDelete failed — aborting force resolve.</color>");
                                    return (false, "Failed to safely remove obstruction");
                                }
                            }
                        }
                        else
                        {
                            _logBoth("<color=#FF4444>  -> Non-obstruction error — aborting (file preserved).</color>");
                            return (false, $"svn delete failed: {SVNErrorHelper.GetShortError(delEx)}");
                        }
                    }

                    try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception resEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> resolve working failed: {SVNErrorHelper.GetShortError(resEx)}</color>");
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"update \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception upEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> update after force: {SVNErrorHelper.GetShortError(upEx)}</color>");
                    }
                }
                else if (isMine)
                {
                    if (!itemExists)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception revEx)
                        {
                            _logBoth($"<color=#FF4444>  -> revert failed: {SVNErrorHelper.GetShortError(revEx)} — aborting (cannot restore base for MINE).</color>");
                            return (false, $"revert failed: {SVNErrorHelper.GetShortError(revEx)}");
                        }
                    }

                    await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                        _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                else if (isBase)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                        _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                else
                {
                    return (false, $"Unknown strategy: {strategy}");
                }

                await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);

                var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                bool stillConflicted = remaining.Any(c =>
                    SVNPathUtilities.NormalizePath(c.Path).Equals(SVNPathUtilities.NormalizePath(path), StringComparison.OrdinalIgnoreCase));

                if (stillConflicted)
                    return (false, "Conflict still present after force structural fix");

                _cache.Remove(SVNPathUtilities.NormalizePath(path));
                return (true, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #endregion

        #region Delete obstruction

        public async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return false;

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            try
            {
                var allConflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                if (SVNPathUtilities.HasUnresolvedParentConflict(path, allConflicts))
                    _logBoth($"<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Resolve children first, then the parent.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logBoth($"<color=#FFAA00>Conflict pre-check failed:</color> {ex.Message}"); }

            string fullPath = Path.Combine(_svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            var conflictInfo = _cache.Get(path);
            string reason = conflictInfo?.TreeConflictReason ?? "unknown";
            string nodeKind = conflictInfo?.TreeConflictNodeKind ?? (fileExists ? "file/dir" : "missing");

            _logBoth($"[TREE RESOLVE] {path}");
            _logBoth($"   Reason : <color=#FFAA00>{reason}</color>");
            _logBoth($"   Kind   : {nodeKind} | Exists on disk: {fileExists}");

            if (fileExists)
            {
                string backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);

                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _logBoth($"<color=yellow>Scheduled for deletion:</color> {path}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex.Message}");

                    if (backupPath != null)
                    {
                        bool deleted = PermanentDelete(fullPath, _logBoth);
                        if (!deleted)
                        {
                            _logBoth("<color=#FF4444>Local delete also failed — aborting.</color>");
                            return false;
                        }
                    }
                    else
                    {
                        await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false);

                        if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        {
                            _logBoth("<color=#FF4444>SafeDelete failed — file preserved. Aborting.</color>");
                            return false;
                        }
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return false;
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception revertEx)
                {
                    _logBoth($"<color=#FFFF00>Revert failed:</color> {revertEx.Message} → trying theirs-full...");
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception theirsEx) { _logBoth($"<color=#FFAA00>theirs-full failed:</color> {theirsEx.Message}"); }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception workingEx) { _logBoth($"<color=#FFAA00>resolve working failed:</color> {workingEx.Message}"); }
            }

            try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            bool stillExists;
            try
            {
                var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                string normalizedPath = SVNPathUtilities.NormalizePath(path);
                stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FFAA00>Post-resolve verification failed:</color> {ex.Message}");
                return false;
            }

            if (stillExists)
            {
                _logBoth($"<color=#FF4444>Tree conflict still exists:</color> {path}");
                return false;
            }

            _cache.Remove(SVNPathUtilities.NormalizePath(path));
            _logBoth($"<color=green>Tree conflict resolved:</color> {path}");
            return true;
        }

        public async Task<(bool success, string path)> DeleteObstructionCoreSilentAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath);

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string fullPath = Path.Combine(_svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            if (fileExists)
            {
                string backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);

                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception delEx)
                {
                    if (!SVNErrorHelper.IsInapplicableOrObstruction(delEx))
                        return (false, path);

                    if (backupPath != null)
                    {
                        PermanentDelete(fullPath);
                    }
                    else
                    {
                        try { await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false); }
                        catch { }
                    }

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        return (false, path);

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return (false, path);
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            List<SVNConflictData> remaining;
            try { remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return (false, path);
            }

            string normalizedPath = SVNPathUtilities.NormalizePath(path);
            bool stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists) return (false, path);

            _cache.Remove(normalizedPath);
            return (true, path);
        }

        #endregion

        #region Conflict markers check

        public async Task<bool> HasConflictMarkersAsync(string fullPath, CancellationToken token = default)
        {
            if (!File.Exists(fullPath)) return false;

            try
            {
                token.ThrowIfCancellationRequested();

                return await Task.Run(() =>
                {
                    bool hasStart = false, hasSeparator = false;
                    using var stream = new StreamReader(fullPath, Encoding.UTF8, true, 8192);
                    string line;
                    while ((line = stream.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("<<<<<<<", StringComparison.Ordinal))
                        {
                            hasStart = true;
                            hasSeparator = false;
                        }
                        else if (hasStart && trimmed.StartsWith("=======", StringComparison.Ordinal))
                        {
                            hasSeparator = true;
                        }
                        else if (hasStart && hasSeparator && trimmed.StartsWith(">>>>>>>", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                    return false;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Utility

        private static bool PermanentDelete(string path, Action<string> log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }

                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(dir, FileAttributes.Normal);
                    Directory.Delete(path, true);
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"<color=#FF4444>PermanentDelete failed for {SVNPathUtilities.ForDisplay(path)}: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return false;
            }
        }

        #endregion
    }
}