using System;
using System.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace SVN.Core
{
    public class SVNClean : SVNBase
    {
        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogToClean(string message, bool append = true)
        {
            if (svnUI.CleanText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.CleanText, message, "CLEAN", append);
            }
            else
            {
                SVNLogBridge.LogLine(message, append);
            }
        }

        public async void LightCleanup()
        {
            try
            {
                await LightCleanupAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task LightCleanupAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            string targetPath = GetTargetPath();

            if (string.IsNullOrEmpty(targetPath))
            {
                EndOperation();
                return;
            }

            LogToClean("<b>Attempting to release SVN database locks...</b>", false);

            try
            {
                string output = await CleanupAsync(targetPath, token);

                LogToClean("<color=green>Cleanup Successful!</color>");

                if (!string.IsNullOrWhiteSpace(output))
                    LogToClean(output);

                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
                LogToClean("<color=yellow>Hint:</color> Close external SVN tools and try again.");
            }
            finally
            {
                EndOperation();
            }
        }

        public static async Task<string> CleanupAsync(
    string workingDir,
    CancellationToken token = default)
        {
            try
            {
                return await SvnRunner.RunAsync(
                    "cleanup",
                    workingDir,
                    false,
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError(
                    $"[SVN] Standard cleanup failed, trying extended cleanup: {ex.Message}");

                return await SvnRunner.RunAsync(
                    "cleanup --include-externals",
                    workingDir,
                    false,
                    token);
            }
        }

        public async void VacuumCleanup()
        {
            try
            {
                await VacuumCleanupAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task VacuumCleanupAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            try
            {
                string targetPath = GetTargetPath();

                if (string.IsNullOrEmpty(targetPath))
                    return;

                LogToClean("<b>Starting Deep Vacuum Cleanup (Optimization)...</b>", false);
                LogToClean("<color=yellow>This may take a while for large projects.</color>");

                string output = await ExecuteVacuumCleanupAsync(targetPath, token);

                LogToClean("<color=green>Vacuum Cleanup Successful!</color>");

                if (!string.IsNullOrWhiteSpace(output))
                    LogToClean(output);

                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Vacuum cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
            }
        }

        public static async Task<string> ExecuteVacuumCleanupAsync(
            string workingDir,
            CancellationToken token = default)
        {
            try
            {
                return await SvnRunner.RunAsync(
                    "cleanup --vacuum-pristines --include-externals",
                    workingDir,
                    false,
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex.Message.Contains("invalid option"))
            {
                SVNLogBridge.LogError(
                    "[SVN] Vacuum cleanup unsupported. Falling back to normal cleanup.");

                return await SvnRunner.RunAsync(
                    "cleanup",
                    workingDir,
                    false,
                    token);
            }
        }

        public async void DeepRepair()
        {
            try
            {
                await DeepRepairAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task DeepRepairAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            try
            {
                string targetPath = GetTargetPath();

                if (string.IsNullOrEmpty(targetPath))
                    return;

                LogToClean("<b>[Deep Repair]</b> Running full diagnostic...", false);
                LogToClean("<color=orange>Warning: Auto-resolving conflicts using SERVER version.</color>");

                LogToClean("Step 1/3: Basic Cleanup...");

                await SvnRunner.RunAsync(
                    "cleanup",
                    targetPath,
                    false,
                    token);

                LogToClean("Step 2/3: Repairing timestamps...");

                try
                {
                    await SvnRunner.RunAsync(
                        "update --force",
                        targetPath,
                        false,
                        token);
                }
                catch (Exception ex)
                {
                    LogToClean("<color=yellow>Timestamp repair skipped (using SVN fallback behavior).</color>");
                }

                LogToClean("Step 3/3: Resolving conflicts...");

                await SvnRunner.RunAsync(
                    "resolve --accept theirs-full -R .",
                    targetPath,
                    false,
                    token);

                LogToClean("<color=green>Deep Repair Finished!</color> Project is now stable.");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Repair cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Repair Error:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await svnManager.RefreshStatus(force: true);
            }
        }

        public async void DiscardUnversioned()
        {
            try
            {
                await DiscardUnversionedAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task DiscardUnversionedAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            try
            {
                string targetPath = GetTargetPath();

                if (string.IsNullOrEmpty(targetPath))
                    return;

                LogToClean("<b>Cleaning up unversioned files [?]...</b>", false);

                await SvnRunner.RunAsync(
                    "cleanup . --remove-unversioned",
                    targetPath,
                    false,
                    token);

                svnUI.SvnTreeView?.ClearView();
                svnUI.SVNCommitTreeDisplay?.ClearView();

                if (svnUI.TreeDisplay != null)
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.TreeDisplay,
                        "",
                        "TREE",
                        false);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();

                statusModule?.ClearCurrentData();

                LogToClean("<color=green>Unversioned files removed successfully!</color>");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Cleanup cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await svnManager.RefreshStatus(force: true);
            }
        }

        private string GetTargetPath()
        {
            string path = svnManager.WorkingDir;

            if (string.IsNullOrWhiteSpace(path))
            {
                SVNLogBridge.LogError("[SVN] Working directory is null or empty.");
                return null;
            }

            if (!System.IO.Directory.Exists(path))
            {
                SVNLogBridge.LogError($"[SVN] Directory does not exist: {path}");
                return null;
            }

            return path;
        }

        public async void HardReset()
        {
            try
            {
                await HardResetAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task HardResetAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            try
            {
                string targetPath = GetTargetPath();

                if (string.IsNullOrEmpty(targetPath))
                    return;

                Debug.LogWarning("[SVN] Hard Reset started - deleting unversioned and reverting changes!");

                LogToClean("<b>[HARD RESET]</b> Cleaning project to match HEAD...", false);

                LogToClean("Step 1/2: Reverting all local modifications...");

                await SvnRunner.RunAsync(
                    "revert -R .",
                    targetPath,
                    false,
                    token);

                LogToClean("Step 2/2: Removing unversioned and ignored files (Deep Clean)...");

                await SvnRunner.RunAsync(
                    "revert -R .",
                    targetPath,
                    false,
                    token);

                await SvnRunner.RunAsync(
                    "cleanup --remove-unversioned --remove-ignored --include-externals",
                    targetPath,
                    false,
                    token);

                await SvnRunner.RunAsync(
                    "update --force --accept theirs-full",
                    targetPath,
                    false,
                    token);

                LogToClean("<color=orange>Hard Reset Complete!</color> Project is now identical to HEAD.");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Hard reset cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Hard Reset Failed:</color> {ex.Message}");
            }
            finally
            {
                EndOperation();
                await svnManager.RefreshStatus(force: true);
            }
        }

        public async void RepairStructure()
        {
            try
            {
                await RepairStructureAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async Task RepairStructureAsync(CancellationToken token = default)
        {
            if (!TryBeginOperation())
                return;

            try
            {
                string targetPath = GetTargetPath();

                if (string.IsNullOrEmpty(targetPath))
                    return;

                LogToClean("<b>[SVN]</b> Starting structure repair...", false);

                LogToClean("Reading repository information...");

                string currentUrl = await SvnRunner.RunAsync(
                    "info --show-item url",
                    targetPath,
                    false,
                    token);

                currentUrl = currentUrl.Trim();

                if (string.IsNullOrWhiteSpace(currentUrl))
                {
                    throw new Exception("Failed to retrieve repository URL.");
                }

                LogToClean($"Targeting URL: {currentUrl}");

                LogToClean("Cleaning working copy...");

                await SvnRunner.RunAsync(
                    "cleanup --remove-unversioned --vacuum-pristines --non-interactive",
                    targetPath,
                    false,
                    token);

                LogToClean("Re-aligning working copy structure...");

                await SvnRunner.RunAsync(
                    $"switch \"{currentUrl}\" . --ignore-ancestry",
                    targetPath,
                    false,
                    token);

                LogToClean("Synchronizing with repository...");

                await SvnRunner.RunAsync(
                    "update --set-depth infinity --force --accept theirs-full --non-interactive",
                    targetPath,
                    false,
                    token);

                LogToClean("Resolving remaining conflicts...");

                await SvnRunner.RunAsync(
                    "resolve --accept theirs-full -R .",
                    targetPath,
                    false,
                    token);

                LogToClean("<color=green><b>[SVN]</b> Structure repaired successfully!</color>");
            }
            catch (OperationCanceledException)
            {
                LogToClean("<color=yellow>Repair cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Repair failed:</color> {ex.Message}");
                LogToClean("<color=yellow>Suggestion:</color> Perform a full fresh checkout.");
            }
            finally
            {
                EndOperation();
                await svnManager.RefreshStatus(force: true);
            }
        }

        private int _operationRunning;

        private bool TryBeginOperation()
        {
            if (Interlocked.Exchange(ref _operationRunning, 1) == 1)
            {
                LogToClean("[SVN] Operation already running...");
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
    }
}