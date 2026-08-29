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

        public async Task<bool> ResolveSingleCoreAsync(string path, string strategy, CancellationToken token)
        {
            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            _logBoth($"[Resolve] {strategy} → {path}");

            var result = await _core.ResolveSingleCoreSilentAsync(path, strategy, token).ConfigureAwait(false);

            if (result.success)
            {
                _logBoth($"<color=green>Resolved:</color> {result.path}");
                await _svnManager.RefreshStatus().ConfigureAwait(false);
                return true;
            }
            else
            {
                _logBoth($"<color=#FF4444>Resolution failed for:</color> {result.path}" +
                         (string.IsNullOrEmpty(result.error) ? "" : $" ({result.error})"));
                await _svnManager.RefreshStatus().ConfigureAwait(false);
                return false;
            }
        }

        public async Task<bool> ResolveTreeStrategyAsync(string rawPath, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
            {
                _logBoth($"<color=#FFAA00>Invalid path:</color> {rawPath}");
                return false;
            }

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            var allConflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            if (SVNPathUtilities.HasUnresolvedParentConflict(path, allConflicts))
                _logBoth($"<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Consider resolving children first, then the parent.");

            var info = _cache.Get(path);
            string reason = info?.TreeConflictReason ?? "unknown";

            _logBoth($"[TREE RESOLVE] {path}");
            _logBoth($"   Strategy : <color=yellow>{strategy}</color>");
            _logBoth($"   Reason   : <color=#FFAA00>{reason}</color>");

            bool success = false;
            string error = null;

            try
            {
                if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    success = true;
                }
                else
                {
                    await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
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
                        await SvnRunner.RunAsync($"resolve --accept {fallback} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        success = true;
                        error = null;
                        _logBoth($"<color=yellow>Fallback to {fallback} succeeded.</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception fbEx) { error = fbEx.Message; }
                }
            }

            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            string normalized = SVNPathUtilities.NormalizePath(path);
            bool stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (success && !stillExists)
            {
                _cache.Remove(normalized);
                await _svnManager.RefreshStatus().ConfigureAwait(false);
                _logBoth($"<color=green>Tree conflict resolved with '{strategy}':</color> {path}");
                return true;
            }
            else
            {
                _logBoth($"<color=#FF4444>Failed to resolve tree conflict with '{strategy}':</color> {path}");
                if (!string.IsNullOrEmpty(error)) _logBoth($"<color=#FFAA00>Error:</color> {error}");
                await _svnManager.RefreshStatus().ConfigureAwait(false);
                return false;
            }
        }

        public async Task<bool> MarkSingleResolvedAsync(string path, CancellationToken token)
        {
            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            string fullPath = Path.Combine(_svnManager.WorkingDir, path);

            var data = _cache.Get(path);
            if (data?.Type == SVNConflictType.Tree)
            {
                _logBoth($"<color=#FFAA00>Tree conflict requires explicit strategy (Mine/Theirs/Base/Delete):</color> {path}");
                return false;
            }

            if (File.Exists(fullPath) && await _core.HasConflictMarkersAsync(fullPath).ConfigureAwait(false))
            {
                _logBoth($"<color=#FFAA00>Conflict markers still exist:</color> {path}");
                return false;
            }

            await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            _cache.Remove(path);

            await Task.Delay(150).ConfigureAwait(false);
            await _svnManager.RefreshStatus().ConfigureAwait(false);

            _logBoth($"<color=green>Resolved manually:</color> {path}");
            return true;
        }

        public async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            return await _core.DeleteObstructionCoreAsync(rawPath, token).ConfigureAwait(false);
        }

        public async Task<(bool success, string path)> DeleteObstructionCoreSilentAsync(string rawPath, CancellationToken token)
        {
            return await _core.DeleteObstructionCoreSilentAsync(rawPath, token).ConfigureAwait(false);
        }

        public async Task ResolveAllConflictsAsync(string strategy, CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            conflicts = SVNPathUtilities.SortConflictsDeepestFirst(conflicts);
            var paths = conflicts.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            int total = paths.Count;
            if (total == 0) { _logOverwrite("<color=yellow>No conflicts found.</color>"); return; }

            _logOverwrite($"<color=yellow>Starting {strategy} for {total} conflicts (deepest first)...</color>");
            int successCount = 0;
            var failedFiles = new List<string>();

            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                _logOverwrite($"<color=yellow>[{i + 1}/{total}] Resolving: {paths[i]}</color>");
                var result = await _core.ResolveSingleCoreSilentAsync(paths[i], strategy, token).ConfigureAwait(false);
                if (result.success) successCount++;
                else failedFiles.Add(result.path);
            }

            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var latest = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            _cache.SynchronizeFrom(latest);
            await _svnManager.RefreshStatus().ConfigureAwait(false);

            if (failedFiles.Count == 0)
                _logOverwrite($"<color=green>Successfully resolved all {successCount}/{total} conflicts ({strategy}).</color>");
            else
                _logOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
        }

        public async Task ResolveAllTreeAsync(string strategy, CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            var treeConflicts = conflicts.Where(c => c.Type == SVNConflictType.Tree).ToList();

            if (treeConflicts.Count == 0) { _logOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

            treeConflicts = SVNPathUtilities.SortConflictsDeepestFirst(treeConflicts);
            int total = treeConflicts.Count;
            _logOverwrite($"<color=yellow>Resolving {total} tree conflicts with '{strategy}' (deepest first)...</color>");

            int successCount = 0;
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var c = treeConflicts[i];
                _logOverwrite($"<color=yellow>[{i + 1}/{total}] {c.Path}</color>");

                try
                {
                    if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                    {
                        await SvnRunner.RunAsync($"revert \"{c.Path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        await SvnRunner.RunAsync($"resolve --accept working \"{c.Path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        successCount++;
                    }
                    else
                    {
                        await SvnRunner.RunAsync($"resolve --accept {strategy} \"{c.Path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        successCount++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
                        _logBoth($"<color=#FFAA00>Failed to restore base for {c.Path}: {ex.Message}</color>");

                    if (strategy.EndsWith("-full"))
                    {
                        try
                        {
                            string fb = strategy.Replace("-full", "-conflict");
                            await SvnRunner.RunAsync($"resolve --accept {fb} \"{c.Path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            successCount++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
            }

            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var latest = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            _cache.SynchronizeFrom(latest);
            await _svnManager.RefreshStatus().ConfigureAwait(false);

            int remaining = latest.Count(c => c.Type == SVNConflictType.Tree);
            if (remaining == 0)
                _logOverwrite($"<color=green>All tree conflicts resolved with '{strategy}'.</color>");
            else
                _logOverwrite($"<color=#FFAA00>Resolved {successCount}/{total}. Remaining tree conflicts: {remaining}</color>");
        }

        public async Task ResolveAllTreeForceAsync(string strategy, CancellationToken token)
        {
            var conflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            var treeConflicts = conflicts.Where(c => c.Type == SVNConflictType.Tree).ToList();

            if (treeConflicts.Count == 0) { _logOverwrite("<color=yellow>No tree conflicts found.</color>"); return; }

            treeConflicts = SVNPathUtilities.SortConflictsDeepestFirst(treeConflicts);
            int total = treeConflicts.Count;
            _logOverwrite($"<color=yellow>Force Resolving {total} tree conflicts with '{strategy}' (deepest first)...</color>");

            int successCount = 0;
            var failedFiles = new List<string>();

            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var c = treeConflicts[i];
                _logOverwrite($"<color=cyan>[TREE FORCE RESOLVE] {strategy} -> {c.Path}</color>");
                var result = await _core.ResolveTreeForceCoreAsync(c, strategy, token).ConfigureAwait(false);
                if (result.success) successCount++;
                else failedFiles.Add(c.Path);
            }

            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var latest = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            _cache.SynchronizeFrom(latest);
            await _svnManager.RefreshStatus().ConfigureAwait(false);

            if (failedFiles.Count == 0)
                _logOverwrite($"<color=green>Force resolved all {successCount}/{total} tree conflicts ({strategy}).</color>");
            else
            {
                _logOverwrite($"<color=#FFAA00>Force resolved {successCount}/{total}. Failed: {failedFiles.Count}</color>");
                foreach (var f in failedFiles)
                    _logBoth($"<color=#FF4444>  -> Failed to force-resolve tree conflict ({strategy}): {f}</color>");
            }
        }

        public async Task ResolveTreeForceAsync(string path, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, path, out string relativePath))
            {
                _logBoth($"<color=#FFAA00>Invalid path:</color> {path}");
                return;
            }

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            var allConflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            var conflictData = allConflicts.FirstOrDefault(c => c.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

            if (conflictData == null || conflictData.Type != SVNConflictType.Tree)
            {
                _logBoth($"<color=#FFAA00>Not a valid tree conflict:</color> {relativePath}");
                return;
            }

            _logOverwrite($"<color=cyan>[TREE FORCE RESOLVE] {strategy} -> {relativePath}</color>");
            var result = await _core.ResolveTreeForceCoreAsync(conflictData, strategy, token).ConfigureAwait(false);

            await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
            var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
            bool stillExists = remaining.Any(c => c.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

            if (result.success && !stillExists)
            {
                _cache.Remove(relativePath);
                await _svnManager.RefreshStatus().ConfigureAwait(false);
                _logBoth($"<color=green>Force resolved tree conflict ({strategy}):</color> {relativePath}");
            }
            else
            {
                _logBoth($"<color=#FF4444>Failed to force-resolve tree conflict ({strategy}):</color> {relativePath}");
                if (!string.IsNullOrEmpty(result.error)) _logBoth($"<color=#FFAA00>Error:</color> {result.error}");
                await _svnManager.RefreshStatus().ConfigureAwait(false);
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

            await _svnManager.RefreshStatus().ConfigureAwait(false);

            if (failedPaths.Count == 0)
                _logOverwrite($"<color=green>Successfully cleared {successCount} tree conflicts.</color>\n" +
                              "<color=#FFAA00>Important: Some items may now be scheduled for deletion. Use Revert to restore them or Commit to accept the deletion.</color>");
            else
                _logOverwrite($"<color=#FFAA00>Cleared {successCount}/{total}. Failed: {failedPaths.Count}</color>");
        }
    }
}