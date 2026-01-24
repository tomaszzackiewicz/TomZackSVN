using System;
using UnityEngine;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase
    {
        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void Update()
        {
            if (IsProcessing) return;

            // Pobieramy dane z Managera, nie z InputFielda UI
            string targetPath = svnManager.WorkingDir;

            // Sprawdzamy czy ścieżka jest ustawiona
            if (string.IsNullOrEmpty(targetPath))
            {
                if (svnUI.LogText != null)
                    svnUI.LogText.text = "<color=red>Error:</color> Working Directory is not set in SVNManager.";
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "<b>[SVN]</b> Checking for server updates...\n";

            try
            {
                Debug.Log($"[SVN] Starting Update for: {targetPath}");

                if (svnUI.LogText != null)
                    svnUI.LogText.text += "<color=orange>Connecting to repository...</color>\n";

                // Wykonanie komendy SVN
                string output = await SvnRunner.UpdateAsync(targetPath);
                Debug.Log($"[SVN] Update Output: {output}");

                // 1. Liczymy zaktualizowane elementy (Regex szuka linii zaczynających się od U, G, A, D)
                int updatedCount = System.Text.RegularExpressions.Regex.Matches(output, @"^[UGAD]\s", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

                // 2. Wyciągamy numer rewizji z outputu (np. z "At revision 19.")
                string revision = svnManager.ParseRevision(output);

                // 3. Fallback: jeśli update nic nie zmienił, output może nie zawierać numeru w czytelny sposób
                if (string.IsNullOrEmpty(revision) || revision == "Unknown")
                {
                    string infoOutput = await SvnRunner.GetInfoAsync(targetPath);
                    revision = svnManager.ParseRevisionFromInfo(infoOutput);
                }

                // 4. Wyświetlanie wyników w UI
                if (svnUI.LogText != null)
                {
                    // Czyścimy log i wypisujemy podsumowanie
                    svnUI.LogText.text = "<b>[SVN UPDATE COMPLETED]</b>\n";

                    if (updatedCount > 0)
                    {
                        svnUI.LogText.text += $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";
                    }
                    else
                    {
                        svnUI.LogText.text += "<color=blue>No changes found.</color> Project is already up to date.\n";
                    }

                    svnUI.LogText.text += $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";
                    svnUI.LogText.text += "-----------------------------------";
                }

                // 5. Odświeżamy informacje o gałęzi i drzewo plików
                svnManager.UpdateBranchInfo();
                svnManager.Button_RefreshStatus();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Update Critical Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"\n<color=red><b>Update Failed:</b></color> {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

}