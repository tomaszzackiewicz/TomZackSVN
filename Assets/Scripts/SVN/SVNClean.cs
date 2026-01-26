using System;
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
                svnUI.LogText.text += "<color=red>Error:</color> No valid path found for Cleanup.\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text = "<b>Attempting to release SVN database locks...</b>\n";

            try
            {
                // Execute standard cleanup command
                string output = await SvnRunner.CleanupAsync(targetPath);

                svnUI.LogText.text += "<color=green>Cleanup Successful!</color>\n";
                if (!string.IsNullOrWhiteSpace(output)) svnUI.LogText.text += output + "\n";

                // Refresh UI to reflect current state
                svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Cleanup Failed:</color> {ex.Message}\n";
                svnUI.LogText.text += "<color=yellow>Hint:</color> Close external SVN tools and try again.\n";
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
            svnUI.LogText.text = "<b>Starting Deep Vacuum Cleanup (Optimization)...</b>\n" +
                                 "<color=yellow>This may take a while for large projects.</color>\n";

            try
            {
                // Execute vacuum cleanup (removes unused pristine copies)
                string output = await SvnRunner.VacuumCleanupAsync(targetPath);

                svnUI.LogText.text += "<color=green>Vacuum Cleanup Successful!</color>\n";
                if (!string.IsNullOrWhiteSpace(output)) svnUI.LogText.text += output + "\n";

                svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("invalid option"))
                {
                    svnUI.LogText.text += "<color=red>Error:</color> Your SVN version is too old for Vacuum Cleanup (requires 1.9+).\n";
                }
                else
                {
                    svnUI.LogText.text += $"<color=red>Cleanup Failed:</color> {ex.Message}\n";
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private string GetTargetPath()
        {
            // Manager zawsze trzyma aktualn¹ œcie¿kê (za³adowan¹ z PlayerPrefs 
            // lub zaktualizowan¹ przez InputField.onValueChanged)
            string path = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[SVN] Target path is empty! Make sure WorkingDir is set in SVNManager.");
            }

            return path;
        }
    }
}