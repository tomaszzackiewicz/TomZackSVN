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

        public async Task<(bool success, string path, string error)> ResolveSingleCoreSilentAsync(
    string rawPath, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath, "Invalid path");

            if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    return (true, path, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (false, path, ex.Message); }
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when ((ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
                                        && strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackStrategy = strategy.Replace("-full", "-conflict");
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
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
                    await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _cache.Remove(path);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            else if (data != null)
            {
                // === FIX 13: przywrócenie stanu sprzed "Resolving" — wcześniej nieudany
                // resolve zostawiał wpis w stanie Resolving na zawsze (GetConflictsAsync
                // podtrzymuje cached.State, więc śmieć się utrwalał).
                _cache.AddOrUpdate(new SVNConflictData
                {
                    Path = path,
                    Type = data.Type,
                    State = data.State,
                    TreeConflictReason = data.TreeConflictReason,
                    TreeConflictAction = data.TreeConflictAction,
                    TreeConflictVictim = data.TreeConflictVictim,
                    TreeConflictNodeKind = data.TreeConflictNodeKind
                });
            }

            return (resolved, path, errorMsg);
        }

        public async Task<(bool success, string error)> ResolveTreeForceCoreAsync(
            SVNConflictData conflict, string strategy, CancellationToken token)
        {
            _logBoth($"<color=magenta>[FORCE CORE ENTER] strategy={strategy} path={conflict.Path}</color>");

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
                        if (backupPath != null)
                        {
                            _logBoth("<color=#AAAAAA>  -> Backup exists, deleting original permanently.</color>");
                            PermanentDelete(fullPath);
                        }
                        else
                        {
                            await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false);
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
                        catch { }
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

                return (true, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return false;

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            // === FIX 2: pre-check (ostrzeżenie o rodzicu) chroniony.
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
                        PermanentDelete(fullPath);
                    }
                    else
                    {
                        await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false);
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

            // === FIX 2: bezpieczny cleanup.
            try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            // === FIX 2 + FIX 6: weryfikacja chroniona; RefreshStatus usunięty (wrapper odświeża).
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

                try { await SvnRunner.RunAsync($"delete \"{path}\" --force", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (backupPath != null)
                    {
                        PermanentDelete(fullPath);
                    }
                    else
                    {
                        try { await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false); }
                        catch { }
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
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

            // === FIX 2: weryfikacja chroniona.
            List<SVNConflictData> remaining;
            try { remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return (false, path); // nie możemy potwierdzić sukcesu — konserwatywnie porażka
            }

            string normalizedPath = SVNPathUtilities.NormalizePath(path);
            bool stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists) return (false, path);

            _cache.Remove(normalizedPath);
            return (true, path);
        }

        public async Task<bool> HasConflictMarkersAsync(string fullPath)
        {
            if (!File.Exists(fullPath)) return false;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 5 * 1024 * 1024) return false;

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
                        if (trimmed.StartsWith("<<<<<<<", StringComparison.Ordinal))
                        {
                            hasStart = true;
                            hasSeparator = false;
                            hasEnd = false;
                        }
                        else if (hasStart && trimmed.StartsWith("=======", StringComparison.Ordinal))
                        {
                            hasSeparator = true;
                        }
                        else if (hasStart && hasSeparator && trimmed.StartsWith(">>>>>>>", StringComparison.Ordinal))
                        {
                            hasEnd = true;
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
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(dir, FileAttributes.Normal);
                    }
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }
    }
}