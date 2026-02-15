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
                SVNLogBridge.LogLine("<color=green>Success:</color> Missing files processing completed.");
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
                SVNLogBridge.LogLine($"Found {missingFiles.Count} missing files. Scheduling for deletion...");

                foreach (var path in missingFiles)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"delete --force \"{path}\"", root);
                        SVNLogBridge.LogLine($"<color=#888888>Deleted from SVN:</color> {path}");
                    }
                    catch (System.Exception)
                    {
                        Debug.LogWarning($"[SVN] Ignoring untracked or locked file: {path}");
                    }
                }
            }
            else
            {
                SVNLogBridge.LogLine("<color=yellow>No missing files detected.</color>");
            }
        }
    }
}