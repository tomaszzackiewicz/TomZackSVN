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
                svnUI.LogText.text += "<color=red>Error:</color> Working directory not set.\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text = "<b>Starting Revert process...</b>\n";

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

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

                await RevertAsync(root, filesToRevert);

                svnUI.LogText.text += $"<color=green>Success!</color> Reverted <b>{filesToRevert.Length}</b> files.\n";

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

        public static async Task<string> RevertAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "No files to revert.";

            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
            return await SvnRunner.RunAsync($"revert -R {fileArgs}", workingDir);
        }
    }
}