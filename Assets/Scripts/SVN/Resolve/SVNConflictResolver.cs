using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNConflictResolver
    {
        private readonly SVNManager _svnManager;
        private readonly SVNConflictParser _parser;
        private readonly SVNConflictCache _cache;
        private readonly SVNConflictCore _core;
        private readonly Action<string> _logBoth;
        private readonly Action<string> _logOverwrite;

        public SVNConflictResolver(
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
            _logBoth = logBoth;
            _logOverwrite = logOverwrite;
            _core = new SVNConflictCore(manager, parser, cache, backup, logBoth, logOverwrite);
        }

        public Task<bool> HasConflictMarkersAsync(string fullPath, CancellationToken token = default)
        {
            return _core.HasConflictMarkersAsync(fullPath, token);
        }

        private async Task SafeCleanupAsync(CancellationToken token)
        {
            try
            {
                await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FFAA00>Post-resolve cleanup failed:</color> {ex.Message}");
            }
        }

        private async Task<bool> VerifyConflictStillExistsAsync(string path, CancellationToken token)
        {
            try
            {
                var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                string normalized = SVNPathUtilities.NormalizePath(path);
                return remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FFAA00>Post-resolve verification failed:</color> {ex.Message}");
                return true;
            }
        }

        public async Task<bool> ResolveSingleCoreAsync(string path, string strategy, CancellationToken token)
        {
            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            _logBoth($"[Resolve] {strategy} → {path}");

            var result = await _core.ResolveSingleCoreSilentAsync(path, strategy, token).ConfigureAwait(false);

            if (result.success)
                _logBoth($"<color=green>Resolved:</color> {result.path}");
            else
                _logBoth($"<color=#FF4444>Resolution failed for:</color> {result.path}" +
                         (string.IsNullOrEmpty(result.error) ? "" : $" ({result.error})"));

            return result.success;
        }

        /// <summary>
        /// Tree strategy resolution delegates entirely to the force core. The force
        /// core already runs Phase 1 (standard resolve) internally and falls back to
        /// structural fix if needed. This is the single reliable path for tree
        /// conflicts: Phase 1 handles simple cases, Phase 3 handles W195024.
        /// </summary>
        public async Task<bool> ResolveTreeStrategyAsync(string rawPath, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
            {
                _logBoth($"<color=#FFAA00>Invalid path:</color> {rawPath}");
                return false;
            }

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            List<SVNConflictData> allConflicts = null;
            try
            {
                allConflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                if (SVNPathUtilities.HasUnresolvedParentConflict(path, allConflicts))
                    _logBoth("<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Consider resolving children first, then the parent.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logBoth($"<color=#FFAA00>Conflict pre-check failed:</color> {ex.Message}"); }

            var info = _cache.Get(path);
            string reason = info?.TreeConflictReason ?? "unknown";

            _logBoth($"[TREE RESOLVE] {path}");
            _logBoth($"   Strategy : <color=yellow>{strategy}</color>");
            _logBoth($"   Reason   : <color=#FFAA00>{reason}</color>");

            var data = allConflicts?.FirstOrDefault(c =>
                           SVNPathUtilities.NormalizePath(c.Path).Equals(
                               SVNPathUtilities.NormalizePath(path), StringComparison.OrdinalIgnoreCase))
                       ?? info
                       ?? new SVNConflictData { Path = path, Type = SVNConflictType.Tree };

            bool success = false;
            string error = null;

            try
            {
                var force = await _core.ResolveTreeForceCoreAsync(data, strategy, token).ConfigureAwait(false);
                success = force.success;
                if (!force.success && !string.IsNullOrEmpty(force.error)) error = force.error;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; }

            await SafeCleanupAsync(token).ConfigureAwait(false);
            bool stillExists = await VerifyConflictStillExistsAsync(path, token).ConfigureAwait(false);

            if (success && !stillExists)
            {
                _cache.Remove(SVNPathUtilities.NormalizePath(path));
                _logBoth($"<color=green>Tree conflict resolved with '{strategy}':</color> {path}");
                return true;
            }

            _logBoth($"<color=#FF4444>Failed to resolve tree conflict with '{strategy}':</color> {path}");
            if (!string.IsNullOrEmpty(error)) _logBoth($"<color=#FFAA00>Error:</color> {error}");
            return false;
        }

        public async Task<bool> MarkSingleResolvedAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
            {
                _logBoth($"<color=#FFAA00>Invalid path (outside working copy):</color> {rawPath}");
                return false;
            }

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            string fullPath = Path.Combine(_svnManager.WorkingDir, path);

            var data = _cache.Get(path);
            if (data?.Type == SVNConflictType.Tree)
            {
                _logBoth($"<color=#FFAA00>Tree conflict requires explicit strategy (Mine/Theirs/Base/Delete):</color> {path}");
                return false;
            }

            bool isBinary = data?.Type == SVNConflictType.Binary
                            || (File.Exists(fullPath) && SVNPathUtilities.IsBinaryFile(fullPath));

            if (!isBinary
                && File.Exists(fullPath)
                && await _core.HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
            {
                _logBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}");
                return false;
            }

            try
            {
                await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FF4444>Resolve failed for:</color> {path} ({ex.Message})");
                return false;
            }

            await SafeCleanupAsync(token).ConfigureAwait(false);
            _cache.Remove(path);

            if (isBinary)
                _logBoth($"<color=green>Binary conflict marked resolved (working copy kept):</color> {path}");
            else
                _logBoth($"<color=green>Resolved manually:</color> {path}");

            return true;
        }

        public void ExecuteDeleteShelf(string shelfName)
        {
            var shelve = _svnManager.GetModule<SVNShelve>();
            if (shelve != null)
                shelve.ExecuteDeleteShelf(shelfName);
        }

        public async Task ResolveAllConflictsAsync(string strategy, CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            conflicts = SVNPathUtilities.SortConflictsDeepestFirst(conflicts);

            var paths = new List<string>();
            int textCount = 0, binaryCount = 0;

            foreach (var c in conflicts)
            {
                if (c.Type == SVNConflictType.Tree)
                {
                    _logBoth($"<color=#FFAA00>Skipped tree conflict (use dedicated tree strategies):</color> {c.Path}");
                    continue;
                }
                if (c.Type == SVNConflictType.Binary) binaryCount++;
                else textCount++;
                paths.Add(c.Path);
            }

            int total = paths.Count;
            if (total == 0) { _logOverwrite("<color=yellow>No text or binary conflicts found.</color>"); return; }

            _logOverwrite($"<color=yellow>Starting {strategy} for {total} conflict(s) [text: {textCount}, binary: {binaryCount}] (deepest first)...</color>");
            int successCount = 0;
            var failedFiles = new List<string>();

            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                _logOverwrite($"<color=yellow>[{i + 1}/{total}] Resolving: {paths[i]}</color>");

                if (strategy.Equals("working", StringComparison.OrdinalIgnoreCase))
                {
                    var c = conflicts.FirstOrDefault(x =>
                        SVNPathUtilities.NormalizePath(x.Path).Equals(
                            SVNPathUtilities.NormalizePath(paths[i]), StringComparison.OrdinalIgnoreCase));

                    if (c != null && c.Type != SVNConflictType.Binary)
                    {
                        string full = Path.Combine(_svnManager.WorkingDir, paths[i]);
                        if (File.Exists(full) && await _core.HasConflictMarkersAsync(full).ConfigureAwait(false))
                        {
                            _logBoth($"<color=#FFAA00>Skipped (conflict markers still present):</color> {paths[i]}");
                            failedFiles.Add(paths[i]);
                            continue;
                        }
                    }
                }

                var result = await _core.ResolveSingleCoreSilentAsync(paths[i], strategy, token).ConfigureAwait(false);
                if (result.success) successCount++;
                else failedFiles.Add(result.path);
            }

            await SafeCleanupAsync(token).ConfigureAwait(false);

            List<SVNConflictData> latest = null;
            try { latest = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logBoth($"<color=#FFAA00>Post-resolve verification failed:</color> {ex.Message}"); }

            if (latest != null)
                _cache.SynchronizeFrom(latest);

            if (failedFiles.Count == 0)
                _logOverwrite($"<color=green>Successfully resolved all {successCount}/{total} conflicts ({strategy}).</color>");
            else
                _logOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
        }

        /// <summary>
        /// Resolves all tree conflicts using the reliable force core per item.
        /// The force core already tries the standard resolve first, then falls
        /// back to structural fix — so this single method handles both simple
        /// and W195024-conflicted tree conflicts. No separate "Force" variant
        /// is needed; the old non-force path silently failed on W195024.
        /// </summary>
        public async Task ResolveAllTreeAsync(string strategy, CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            var treeConflicts = conflicts.Where(c => c.Type == SVNConflictType.Tree).ToList();

            if (treeConflicts.Count == 0) { _logOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

            treeConflicts = SVNPathUtilities.SortConflictsDeepestFirst(treeConflicts);
            int total = treeConflicts.Count;
            _logOverwrite($"<color=yellow>Resolving {total} tree conflicts with '{strategy}' (deepest first)...</color>");

            int successCount = 0;
            var failedFiles = new List<string>();

            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var c = treeConflicts[i];
                _logOverwrite($"<color=yellow>[{i + 1}/{total}] {strategy} -> {c.Path}</color>");

                var result = await _core.ResolveTreeForceCoreAsync(c, strategy, token).ConfigureAwait(false);
                if (result.success) successCount++;
                else failedFiles.Add(c.Path);
            }

            await SafeCleanupAsync(token).ConfigureAwait(false);

            List<SVNConflictData> latest = null;
            try { latest = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logBoth($"<color=#FFAA00>Post-resolve verification failed:</color> {ex.Message}"); }

            if (latest != null)
                _cache.SynchronizeFrom(latest);

            if (failedFiles.Count == 0)
                _logOverwrite($"<color=green>Resolved all {successCount}/{total} tree conflicts ({strategy}).</color>");
            else
            {
                _logOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
                foreach (var f in failedFiles)
                    _logBoth($"<color=#FF4444>  -> Failed to resolve tree conflict ({strategy}): {f}</color>");
            }
        }

        public async Task DeleteAllObstructionsAsync(CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            var treeConflicts = conflicts.Where(x => x.Type == SVNConflictType.Tree).ToList();

            if (treeConflicts.Count == 0) { _logOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

            treeConflicts = SVNPathUtilities.SortConflictsDeepestFirst(treeConflicts);
            int total = treeConflicts.Count;
            _logOverwrite($"<color=#FF4444><b>RESOLVING {total} TREE CONFLICTS (deepest first)...</b></color>");

            int successCount = 0;
            var failedPaths = new List<string>();
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var c = treeConflicts[i];
                _logOverwrite($"<color=yellow>[{i + 1}/{total}] Processing: {c.Path}</color>");
                var result = await _core.DeleteObstructionCoreSilentAsync(c.Path, token).ConfigureAwait(false);
                if (result.success) successCount++;
                else failedPaths.Add(result.path);
            }

            await SafeCleanupAsync(token).ConfigureAwait(false);

            if (failedPaths.Count == 0)
                _logOverwrite($"<color=green>Successfully cleared {successCount} tree conflicts.</color>\n" +
                              "<color=#FFAA00>Important: Some items may now be scheduled for deletion. Use Revert to restore them or Commit to accept the deletion.</color>");
            else
                _logOverwrite($"<color=#FFAA00>Cleared {successCount}/{total}. Failed: {failedPaths.Count}</color>");
        }

        public async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            return await _core.DeleteObstructionCoreAsync(rawPath, token).ConfigureAwait(false);
        }

        public async Task<(bool success, string path)> DeleteObstructionCoreSilentAsync(string rawPath, CancellationToken token)
        {
            return await _core.DeleteObstructionCoreSilentAsync(rawPath, token).ConfigureAwait(false);
        }
    }
}