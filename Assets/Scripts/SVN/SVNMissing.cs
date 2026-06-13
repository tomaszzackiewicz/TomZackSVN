using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMissing : SVNBase
    {
        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void FixMissingFiles()
        {
            if (IsProcessing) return;

            IsProcessing = true;

            SVNLogBridge.LogLine(
                "<b>[Missing Files]</b> Scanning for items removed from disk...",
                append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                await FixMissingLogic();

                await Task.Delay(250);

                var statusModule = svnManager.GetModule<SVNStatus>();

                if (statusModule != null)
                {
                    statusModule.ClearCurrentData();

                    if (svnUI.SvnTreeView != null)
                        svnUI.SvnTreeView.ClearView();

                    if (svnUI.SVNCommitTreeDisplay != null)
                        svnUI.SVNCommitTreeDisplay.ClearView();

                    if (svnUI.TreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    }

                    if (svnUI.CommitTreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                    }

                    svnManager.ExpandedPaths.Clear();
                    svnManager.ExpandedPaths.Add("");

                    SVNLogBridge.LogLine("<color=#4FC3F7>Refreshing SVN status...</color>");

                    await statusModule.ExecuteRefreshWithAutoExpand(force: true);

                    if (statusModule.GetCurrentData().Count == 0)
                    {
                        if (svnUI.TreeDisplay != null)
                        {
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
                        }

                        if (svnUI.CommitTreeDisplay != null)
                        {
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);
                        }
                    }
                }

                SVNLogBridge.LogLine(
                    "<color=green><b>SUCCESS!</b></color> Missing files have been removed from SVN index.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine(
                    $"<color=red>FixMissing Error:</color> {ex.Message}");

                SVNLogBridge.LogError(
                    $"[SVN] FixMissing: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task FixMissingLogic()
        {
            string root = svnManager.WorkingDir;
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

            var missingFiles = statusDict
                .Where(x => x.Value.status.Contains("!"))
                .Select(x => x.Key)
                .ToList();

            if (missingFiles.Count > 0)
            {
                SVNLogBridge.LogLine($"Found <b>{missingFiles.Count}</b> missing files. Deleting from SVN index...");

                int batchSize = 25;
                for (int i = 0; i < missingFiles.Count; i += batchSize)
                {
                    var batch = missingFiles.Skip(i).Take(batchSize);
                    string filesArgs = string.Join(" ", batch.Select(f => $"\"{f}\""));

                    try
                    {
                        await SvnRunner.RunAsync($"delete --force {filesArgs}", root);
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogError($"[SVN] Batch delete partial failure: {ex.Message}");
                    }
                }
            }
            else
            {
                SVNLogBridge.LogLine("<color=yellow>No missing files (!) detected.</color>");
            }
        }
    }
}