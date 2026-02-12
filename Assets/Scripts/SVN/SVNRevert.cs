using System;
using System.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevert : SVNBase
    {
        public SVNRevert(SVNUI ui, SVNManager manager) : base(ui, manager) { }
        public async void RevertAll()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Working directory not set.\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text = "<b>Starting Revert process...</b>\n";

            try
            {
                // 1. Get status to find files that can be reverted (Modified, Added, Deleted, etc.)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 2. Filter files with revertable statuses
                var filesToRevert = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && "MADRC".Contains(x.Value.status))
                    .Select(x => x.Key)
                    .ToArray();

                if (filesToRevert.Length == 0)
                {
                    svnUI.LogText.text += "<color=yellow>No local changes detected to revert.</color>\n";
                    return;
                }

                svnUI.LogText.text += $"Reverting {filesToRevert.Length} files to their original state...\n";

                // 3. Execute the Revert command via SvnRunner
                await SvnRunner.RevertAsync(root, filesToRevert);

                svnUI.LogText.text += $"<color=green>Success!</color> Reverted <b>{filesToRevert.Length}</b> files.\n";

                // 4. Trigger UI refresh to update the tree view and clear status markers
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Revert Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }
}