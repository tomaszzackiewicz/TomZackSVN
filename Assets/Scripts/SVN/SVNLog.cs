using System;
using System.Threading.Tasks;
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
                SVNLogBridge.LogLine("<color=red>Error:</color> Path not found.");
                return;
            }

            int count = 10;
            if (svnUI.LogCountInputField != null && !string.IsNullOrWhiteSpace(svnUI.LogCountInputField.text))
            {
                if (!int.TryParse(svnUI.LogCountInputField.text, out count)) count = 10;
            }
            count = Mathf.Clamp(count, 1, 500);

            IsProcessing = true;
            SVNLogBridge.LogLine($"<b>Fetching last {count} log entries...</b>", append: false);

            try
            {
                string output = await LogAsync(root, count);

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=yellow>No history found for this path.</color>");
                }
                else
                {
                    SVNLogBridge.LogLine("------------------------------------------");
                    SVNLogBridge.LogLine(output);
                    SVNLogBridge.LogLine("------------------------------------------");

                    await ScrollToBottom();
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Log Error:</color> {ex.Message}");
                Debug.LogError($"[SVN Log] {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public static async Task<string> LogAsync(string workingDir, int lastN = 10)
        {
            return await SvnRunner.RunAsync($"log -l {lastN}", workingDir);
        }

        private async Task ScrollToBottom()
        {
            if (svnUI.LogScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                await Task.Yield();
                svnUI.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }
    }
}