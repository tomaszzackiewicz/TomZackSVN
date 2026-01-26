using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMissing : SVNBase
    {
        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        /// <summary>
        /// Public entry point for UI buttons. Removes files from SVN that were deleted manually from disk.
        /// </summary>
        public async void FixMissingFiles()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            svnUI.LogText.text = "<b>[Missing Files]</b> Scanning for items removed from disk...\n";

            try
            {
                await FixMissingLogic();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>FixMissing Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] FixMissing: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Logic that can be awaited by other modules (e.g., during a full cleanup before Commit).
        /// </summary>
        private async Task FixMissingLogic()
        {
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Working directory is not set.\n";
                return;
            }

            // 1. Get status of all files (includeIgnored = false)
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

            // 2. Filter for status '!' (missing - exists in DB but not on disk)
            var missingPaths = statusDict
                .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("!"))
                .Select(x => x.Key)
                .ToArray();

            if (missingPaths.Length > 0)
            {
                svnUI.LogText.text += $"Found {missingPaths.Length} missing files. Removing from SVN index...\n";

                // 3. Execute 'svn delete' on those paths to synchronize SVN DB with disk
                string output = await SvnRunner.DeleteAsync(root, missingPaths);

                svnUI.LogText.text += $"<color=green>Success!</color> Removed {missingPaths.Length} missing meta-entries.\n";

                // 4. Refresh the explorer view
                svnManager.Button_RefreshStatus();
            }
            else
            {
                svnUI.LogText.text += "No missing files found. Everything is synchronized.\n";
            }
        }
    }
}