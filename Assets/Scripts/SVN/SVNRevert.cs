using System;
using System.Linq;
using System.Threading.Tasks;

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
                SVNLogBridge.LogLine("<color=red>Error:</color> Working directory not set.");
                return;
            }

            IsProcessing = true;
            SVNLogBridge.LogLine("<b>Starting Revert process...</b>", append: false);

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var filesToRevert = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && "MADRC".Contains(x.Value.status))
                    .Select(x => x.Key)
                    .ToArray();

                if (filesToRevert.Length == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No local changes detected to revert.</color>");
                    return;
                }

                await RevertAsync(root, filesToRevert);

                if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule.ClearCurrentData();

                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);

                if (svnUI.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<color=green>No changes to commit.</color>", "COMMIT_TREE", append: false);

                SVNLogBridge.LogLine($"<color=green><b>SUCCESS!</b></color> Reverted <b>{filesToRevert.Length}</b> files.");

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public static async Task<string> RevertAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "No files to revert.";

            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
            return await SvnRunner.RunAsync($"revert -R {fileArgs}", workingDir);
        }
    }
}