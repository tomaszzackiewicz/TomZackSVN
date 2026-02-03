using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMissing : SVNBase
    {
        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        /// <summary>
        /// Public entry point for UI buttons. Removes files from SVN that were deleted manually from disk.
        /// </summary>
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

        /// <summary>
        /// Logic that can be awaited by other modules (e.g., during a full cleanup before Commit).
        /// </summary>
        public async Task FixMissingLogic()
        {
            string root = svnManager.WorkingDir;
            // 1. Pobieramy statusy wszystkich plików
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

            // 2. Szukamy tylko tych, które maj¹ status '!' (Brakuj¹cy w bazie)
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
                        // --force jest kluczowe, aby usun¹æ wpis z bazy nawet bez pliku na dysku.
                        // Jeœli plik rzuci b³êdem "not a working copy", catch go z³apie i przejdzie dalej.
                        await SvnRunner.RunAsync($"delete --force \"{path}\"", root);
                    }
                    catch (System.Exception ex)
                    {
                        // Jeœli plik nie jest w Working Copy, to znaczy ¿e i tak go nie ma w SVN.
                        // Mo¿emy to bezpiecznie zignorowaæ.
                        UnityEngine.Debug.LogWarning($"[SVN] Ignorowanie nieœledzonego pliku: {path}");
                    }
                }
            }
        }
    }
}