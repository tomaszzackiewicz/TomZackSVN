using System;
using System.Linq;
using System.Threading.Tasks;
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

                SVNLogBridge.LogLine($"Reverting {filesToRevert.Length} files to their original state...");

                await RevertAsync(root, filesToRevert);

                SVNLogBridge.LogLine($"<color=green>Success!</color> Reverted <b>{filesToRevert.Length}</b> files.");

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