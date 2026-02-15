using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNClean : SVNBase
    {
        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void LightCleanup()
        {
            if (IsProcessing) return;

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> No valid path found for Cleanup.");
                return;
            }

            IsProcessing = true;
            SVNLogBridge.LogLine("<b>Attempting to release SVN database locks...</b>", append: false);

            try
            {
                // Wykonanie standardowej komendy cleanup
                string output = await CleanupAsync(targetPath);

                SVNLogBridge.LogLine("<color=green>Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output)) SVNLogBridge.LogLine(output);

                // Odświeżenie UI
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Cleanup Failed:</color> {ex.Message}");
                SVNLogBridge.LogLine("<color=yellow>Hint:</color> Close external SVN tools and try again.");
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
            SVNLogBridge.LogLine("<b>Starting Deep Vacuum Cleanup (Optimization)...</b>", append: false);
            SVNLogBridge.LogLine("<color=yellow>This may take a while for large projects.</color>");

            try
            {
                // Wykonanie vacuum cleanup (usuwa nieużywane kopie bazy)
                string output = await VacuumCleanupAsync(targetPath);

                SVNLogBridge.LogLine("<color=green>Vacuum Cleanup Successful!</color>");
                if (!string.IsNullOrWhiteSpace(output)) SVNLogBridge.LogLine(output);

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("invalid option"))
                {
                    SVNLogBridge.LogLine("<color=red>Error:</color> Your SVN version is too old for Vacuum Cleanup (requires 1.9+).");
                }
                else
                {
                    SVNLogBridge.LogLine($"<color=red>Cleanup Failed:</color> {ex.Message}");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void DiscardUnversioned()
        {
            if (IsProcessing) return;

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath)) return;

            IsProcessing = true;
            SVNLogBridge.LogLine("<b>Cleaning up unversioned files [?]...</b>", append: false);

            try
            {
                await SvnRunner.RunAsync("cleanup . --remove-unversioned", targetPath);
                SVNLogBridge.LogLine("<color=green>Unversioned files removed successfully!</color>");

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Cleanup Failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
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