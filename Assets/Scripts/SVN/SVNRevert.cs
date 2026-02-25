using System;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

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
                    .Select(x =>
                    {
                        string fullPath = Path.Combine(root, x.Key).Replace("/", "\\");
                        return Path.GetFullPath(fullPath);
                    })
                    .ToArray();

                if (filesToRevert.Length == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No local changes detected to revert.</color>");
                    return;
                }

                await RevertAsync(root, filesToRevert, (msg) =>
                {
                    SVNLogBridge.LogLine($"<color=green>[Progress]</color> {msg}");
                });

                if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                svnManager.GetModule<SVNStatus>().ClearCurrentData();

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

        public static async Task<string> RevertAsync(string workingDir, string[] files, Action<string> onProgress = null)
        {
            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim()).Replace('\\', '/');

            try
            {
                onProgress?.Invoke("Performing recursive revert on working directory...");

                string result = await SvnRunner.RunAsync("revert -R .", cleanWorkingDir);

                if (result.Contains("svn: E"))
                {
                    onProgress?.Invoke("Revert failed, attempting cleanup...");
                    await SvnRunner.RunAsync("cleanup", cleanWorkingDir);

                    onProgress?.Invoke("Retrying recursive revert...");
                    result = await SvnRunner.RunAsync("revert -R .", cleanWorkingDir);
                }

                UnityEngine.Debug.Log("<color=green>[SVN]</color> Recursive revert completed successfully.");
                return result;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[SVN Revert Error] Recursive revert failed: {ex.Message}");
                throw;
            }
        }
    }
}