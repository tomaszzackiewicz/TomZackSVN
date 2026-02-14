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
                foreach (var path in missingFiles)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"delete --force \"{path}\"", root);
                    }
                    catch (System.Exception)
                    {
                        UnityEngine.Debug.LogWarning($"[SVN] Ignorowanie nieśledzonego pliku: {path}");
                    }
                }
            }
        }
    }
}