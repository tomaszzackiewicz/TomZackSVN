using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNClean : SVNBase
    {
        private SVNStatus _cachedStatusModule;
        private int _operationRunning;

        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        #region Logging

        private void LogToClean(string message, bool append = true)
        {
            if (svnUI?.CleanText != null)
                SVNLogBridge.UpdateUIField(svnUI.CleanText, message, "CLEAN", append);
            else
                SVNLogBridge.LogLine(message, append);
        }

        private void ClearLog() => LogToClean(string.Empty, append: false);

        #endregion

        #region Public API (Unity-safe async void wrappers)

        public void LightCleanup() => SafeFireAndForget(LightCleanupAsync);
        public void VacuumCleanup() => SafeFireAndForget(VacuumCleanupAsync);
        public void DeepRepair() => SafeFireAndForget(DeepRepairAsync);
        public void DiscardUnversioned() => SafeFireAndForget(DiscardUnversionedAsync);
        public void HardReset() => SafeFireAndForget(HardResetAsync);
        public void RepairStructure() => SafeFireAndForget(RepairStructureAsync);

        private async void SafeFireAndForget(Func<CancellationToken, Task> operation)
        {
            try { await operation(default).ConfigureAwait(false); }
            catch (Exception ex) { SVNLogBridge.LogException(ex); }
        }

        #endregion

        #region Core Operations

        public async Task LightCleanupAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>Attempting to release SVN database locks...</b>");

                string output = await CleanupAsync(targetPath, token).ConfigureAwait(false);

                LogToClean("<color=green>Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output))
                    LogToClean(output);

                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Cleanup Failed:</color> {ex.Message}");
                LogToClean("<color=yellow>Hint:</color> Close external SVN tools and try again.");
            }
            finally
            {
                EndOperation();
            }
        }

        public static async Task<string> CleanupAsync(string workingDir, CancellationToken token = default)
        {
            try
            {
                return await SvnRunner.RunAsync("cleanup", workingDir, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Standard cleanup failed, trying extended cleanup: {ex.Message}");
                token.ThrowIfCancellationRequested();
                return await SvnRunner.RunAsync("cleanup --include-externals", workingDir, false, token).ConfigureAwait(false);
            }
        }

        public async Task VacuumCleanupAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>Starting Deep Vacuum Cleanup (Optimization)...</b>");
                LogToClean("<color=yellow>This may take a while for large projects.</color>");

                string output = await ExecuteVacuumCleanupAsync(targetPath, token).ConfigureAwait(false);

                LogToClean("<color=green>Vacuum Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output))
                    LogToClean(output);

                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Vacuum cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
            }
        }

        public static async Task<string> ExecuteVacuumCleanupAsync(string workingDir, CancellationToken token = default)
        {
            try
            {
                return await SvnRunner.RunAsync("cleanup --vacuum-pristines --include-externals", workingDir, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex.Message.Contains("invalid option"))
            {
                SVNLogBridge.LogError("[SVN] Vacuum cleanup unsupported. Falling back to normal cleanup.");
                token.ThrowIfCancellationRequested();
                return await SvnRunner.RunAsync("cleanup", workingDir, false, token).ConfigureAwait(false);
            }
        }

        public async Task DeepRepairAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>[Deep Repair]</b> Running full diagnostic...");
                LogToClean("<color=orange>Warning: Auto-resolving conflicts using SERVER version.</color>");

                LogToClean("Step 1/3: Basic Cleanup...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("cleanup", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Step 2/3: Repairing timestamps...");
                token.ThrowIfCancellationRequested();
                try
                {
                    await SvnRunner.RunAsync("update --force", targetPath, true, token).ConfigureAwait(false);
                }
                catch (Exception) when (!(token.IsCancellationRequested))
                {
                    LogToClean("<color=yellow>Timestamp repair skipped (using SVN fallback behavior).</color>");
                }

                LogToClean("Step 3/3: Resolving conflicts...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("resolve --accept theirs-full -R .", targetPath, true, token).ConfigureAwait(false);

                LogToClean("<color=green>Deep Repair Finished!</color> Project is now stable.");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Repair cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Repair Error:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
        }

        public async Task DiscardUnversionedAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>Cleaning up unversioned files [?]...</b>");

                await SvnRunner.RunAsync("cleanup . --remove-unversioned", targetPath, false, token).ConfigureAwait(false);

                svnUI.SvnTreeView?.ClearView();
                svnUI.SVNCommitTreeDisplay?.ClearView();

                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, string.Empty, "TREE", append: false);

                _cachedStatusModule ??= svnManager.GetModule<SVNStatus>();
                _cachedStatusModule?.ClearCurrentData();

                LogToClean("<color=green>Unversioned files removed successfully!</color>");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
        }

        public async Task HardResetAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>[HARD RESET]</b> Cleaning project to match HEAD...");
                LogToClean("<color=#FFAA00>WARNING: All unversioned files will be permanently deleted!</color>");

                LogToClean("Step 1/3: Reverting all local modifications...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("revert -R .", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Step 2/3: Removing unversioned files (Deep Clean)...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("cleanup --remove-unversioned --include-externals", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Step 3/3: Forcing update to HEAD...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("update --force --accept theirs-full", targetPath, true, token).ConfigureAwait(false);

                LogToClean("<color=orange>Hard Reset Complete!</color> Project is now identical to HEAD.");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Hard reset cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Hard Reset Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
        }

        public async Task RepairStructureAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation()) return;

            try
            {
                string targetPath = ResolveAndValidatePath();
                if (targetPath == null) return;

                ClearLog();
                LogToClean("<b>[SVN]</b> Starting structure repair...");
                LogToClean("Reading repository information...");

                token.ThrowIfCancellationRequested();
                string currentUrl = await SvnRunner.RunAsync("info --show-item url", targetPath, true, token).ConfigureAwait(false);
                currentUrl = currentUrl?.Trim();

                if (string.IsNullOrWhiteSpace(currentUrl))
                    throw new InvalidOperationException("Failed to retrieve repository URL.");

                LogToClean($"Targeting URL: {currentUrl}");

                LogToClean("Cleaning working copy...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("cleanup --remove-unversioned --vacuum-pristines --non-interactive", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Re-aligning working copy structure...");
                token.ThrowIfCancellationRequested();
                string safeUrl = currentUrl.Replace("\"", "\\\"");
                await SvnRunner.RunAsync($"switch \"{safeUrl}\" . --ignore-ancestry", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Synchronizing with repository...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("update --set-depth infinity --force --accept theirs-full --non-interactive", targetPath, true, token).ConfigureAwait(false);

                LogToClean("Resolving remaining conflicts...");
                token.ThrowIfCancellationRequested();
                await SvnRunner.RunAsync("resolve --accept theirs-full -R .", targetPath, true, token).ConfigureAwait(false);

                LogToClean("<color=green><b>[SVN]</b> Structure repaired successfully!</color>");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Repair cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=#FFAA00>Repair failed:</color> {ex.Message}");
                LogToClean("<color=yellow>Suggestion:</color> Perform a full fresh checkout.");
            }
            finally
            {
                EndOperation();
                await RefreshStatusAsync(token).ConfigureAwait(false);
            }
        }

        #endregion

        #region Helpers

        private string ResolveAndValidatePath()
        {
            string path = svnManager?.WorkingDir;

            if (string.IsNullOrWhiteSpace(path))
            {
                LogToClean("<color=#FFAA00>Error:</color> Working directory is not set.", false);
                return null;
            }

            if (!Directory.Exists(path))
            {
                LogToClean($"<color=#FFAA00>Error:</color> Directory does not exist:\n{path}", false);
                return null;
            }

            if (!Directory.Exists(Path.Combine(path, ".svn")))
            {
                LogToClean($"<color=#FFAA00>Error:</color> Not a valid SVN working copy:\n{path}", false);
                return null;
            }

            return path;
        }

        private async Task RefreshStatusAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            try { await svnManager.RefreshStatus(force: true).ConfigureAwait(false); }
            catch (Exception ex) { SVNLogBridge.LogError($"Failed to refresh status: {ex.Message}"); }
        }

        private bool TryBeginOperation()
        {
            if (Interlocked.Exchange(ref _operationRunning, 1) == 1)
            {
                LogToClean("<color=yellow>[SVN] Operation already running...</color>");
                return false;
            }

            IsProcessing = true;
            return true;
        }

        private void EndOperation()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _operationRunning, 0);
        }

        #endregion
    }
}