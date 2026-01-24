using System;
using System.Threading.Tasks; // Dodane dla Task.Yield
using UnityEngine;

namespace SVN.Core
{
    public class SVNLog : SVNBase
    {
        public SVNLog(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void ShowLog()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                LogToUI("<color=red>Error:</color> Path not found.");
                return;
            }

            int count = 10;
            if (svnUI.LogCountInputField != null && !string.IsNullOrWhiteSpace(svnUI.LogCountInputField.text))
            {
                if (!int.TryParse(svnUI.LogCountInputField.text, out count)) count = 10;
            }
            count = Mathf.Clamp(count, 1, 500);

            IsProcessing = true;
            svnUI.LogText.text = $"<b>Fetching last {count} log entries...</b>\n";

            try
            {
                string output = await SvnRunner.LogAsync(root, count);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogToUI("<color=yellow>No history found for this path.</color>");
                }
                else
                {
                    // 1. USTAWIANIE TEKSTU
                    svnUI.LogText.text = "------------------------------------------\n";
                    svnUI.LogText.text += output;
                    svnUI.LogText.text += "\n------------------------------------------\n";

                    // 2. PRZEWIJANIE (Czekamy chwilê, a¿ UI siê odœwie¿y)
                    await ScrollToBottom();
                }
            }
            catch (Exception ex)
            {
                LogToUI($"<color=red>Log Error:</color> {ex.Message}");
                Debug.LogError($"[SVN Log] {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async void LogToUI(string message)
        {
            if (svnUI.LogText != null)
            {
                svnUI.LogText.text += message + "\n";
                await ScrollToBottom();
            }
        }

        // Metoda pomocnicza do przewijania
        private async Task ScrollToBottom()
        {
            if (svnUI.LogScrollRect != null)
            {
                // Wymuszamy aktualizacjê siatki UI
                Canvas.ForceUpdateCanvases();
                // Czekamy jeden cykl ramki, ¿eby Content Size Fitter zd¹¿y³ zareagowaæ
                await Task.Yield();
                svnUI.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }
    }
}