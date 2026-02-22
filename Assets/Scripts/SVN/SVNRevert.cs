using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

                // 1. NAPRAWA ŚCIEŻEK: Budujemy pełne, czyste ścieżki systemowe
                var filesToRevert = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && "MADRC".Contains(x.Value.status))
                    .Select(x =>
                    {
                        // Łączymy root z relatywną ścieżką i zamieniamy / na \ (standard Windows)
                        string fullPath = Path.Combine(root, x.Key).Replace("/", "\\");
                        return Path.GetFullPath(fullPath); // To usunie wszelkie podwójne ukośniki itp.
                    })
                    .ToArray();

                if (filesToRevert.Length == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No local changes detected to revert.</color>");
                    return;
                }

                // 2. WYWOŁANIE REVERTU
                await RevertAsync(root, filesToRevert, (msg) =>
                {
                    SVNLogBridge.LogLine($"<color=cyan>[Progress]</color> {msg}");
                });

                // Odświeżanie UI (Twoje istniejące wywołania)
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
            // Czyścimy ścieżkę roboczą
            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim()).Replace('\\', '/');

            try
            {
                onProgress?.Invoke("Performing recursive revert on working directory...");

                // Komenda: revert -R .
                // -R: Rekurencyjnie (wszystkie podfoldery, w tym trunk)
                // . : Bieżący folder (workingDir)
                // To cofnie wszystkie zmiany wykryte przez SVN w tym projekcie.
                string result = await SvnRunner.RunAsync("revert -R .", cleanWorkingDir);

                if (result.Contains("svn: E"))
                {
                    // Jeśli wystąpi błąd (np. blokada), próbujemy najpierw cleanup
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