using System;
using System.Threading.Tasks;
using UnityEngine;

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
            if (IsProcessing) return;

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath))
            {
                LogToClean("<color=red>Error:</color> No valid path found for Cleanup.", append: false);
                return;
            }

            IsProcessing = true;
            LogToClean("<b>Attempting to release SVN database locks...</b>", append: false);

            try
            {
                string output = await CleanupAsync(targetPath);

                LogToClean("<color=green>Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output)) LogToClean(output);

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
                LogToClean("<color=yellow>Hint:</color> Close external SVN tools and try again.");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void VacuumCleanup()
        {
            if (IsProcessing) return;

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath)) return;

            IsProcessing = true;
            LogToClean("<b>Starting Deep Vacuum Cleanup (Optimization)...</b>", append: false);
            LogToClean("<color=yellow>This may take a while for large projects.</color>");

            try
            {
                string output = await VacuumCleanupAsync(targetPath);

                LogToClean("<color=green>Vacuum Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output)) LogToClean(output);

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("invalid option"))
                {
                    LogToClean("<color=red>Error:</color> Your SVN version is too old for Vacuum Cleanup (requires 1.9+).");
                }
                else
                {
                    LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void DeepRepair()
        {
            if (IsProcessing) return;
            string targetPath = GetTargetPath();
            IsProcessing = true;

            LogToClean("<b>[Deep Repair]</b> Running full diagnostic...", append: false);
            LogToClean("<color=orange>Warning: Auto-resolving conflicts using YOUR version.</color>");

            try
            {
                LogToClean("Step 1/3: Basic Cleanup...");
                await SvnRunner.RunAsync("cleanup", targetPath);

                LogToClean("Step 2/3: Repairing timestamps...");
                try
                {
                    await SvnRunner.RunAsync("cleanup --fix-recorded-timestamps", targetPath);
                }
                catch (Exception ex) when (ex.Message.Contains("invalid option"))
                {
                    LogToClean("<color=yellow>Note:</color> Timestamp repair skipped (version not supported).");
                }

                LogToClean("Step 3/3: Resolving conflicts...");
                await SvnRunner.RunAsync("resolve --accept working -R .", targetPath);

                LogToClean("<color=green>Deep Repair Finished!</color> Project is now stable.");
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Repair Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus(force: true);
            }
        }

        public async void DiscardUnversioned()
        {
            if (IsProcessing) return;

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath)) return;

            IsProcessing = true;
            LogToClean("<b>Cleaning up unversioned files [?]...</b>", append: false);

            try
            {
                await SvnRunner.RunAsync("cleanup . --remove-unversioned", targetPath);

                if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearCurrentData();
                }

                LogToClean("<color=green>Unversioned files removed successfully!</color>");

                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                LogToClean($"<color=red>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;

                await svnManager.RefreshStatus(force: true);
            }
        }

        private string GetTargetPath()
        {
            string path = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[SVN] Target path is empty! Make sure WorkingDir is set in SVNManager.");
            }

            return path;
        }

        public static async Task<string> CleanupAsync(string workingDir)
        {
            try
            {
                return await SvnRunner.RunAsync("cleanup", workingDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommands] Standard cleanup failed, trying extended: {ex.Message}");
                return await SvnRunner.RunAsync("cleanup --include-externals", workingDir);
            }
        }

        public static async Task<string> VacuumCleanupAsync(string workingDir)
        {
            try
            {
                return await SvnRunner.RunAsync("cleanup --vacuum-pristines --include-externals", workingDir);
            }
            catch
            {
                return await SvnRunner.RunAsync("cleanup", workingDir);
            }
        }
    }
}