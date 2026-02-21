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

            SVNLogBridge.LogLine("<b>[Missing Files]</b> Scanning for items removed from disk...", append: false);

            try
            {
                await FixMissingLogic();

                IsProcessing = false;

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    //statusModule.IsProcessing = false;
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();

                    SVNLogBridge.LogLine("<color=#4FC3F7>Rebuilding tree...</color>");
                    await statusModule.ExecuteRefreshWithAutoExpand();
                }

                SVNLogBridge.LogLine("<color=green><b>SUCCESS!</b></color> Missing files have been removed from SVN index.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>FixMissing Error:</color> {ex.Message}");
                Debug.LogError($"[SVN] FixMissing: {ex}");
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
                        Debug.LogWarning($"[SVN] Batch delete partial failure: {ex.Message}");
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