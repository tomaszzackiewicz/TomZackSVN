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

            string timestamp = $"[{DateTime.Now:HH:mm:ss}]";
            SVNLogBridge.LogLine($"{timestamp} <color=#00FF99>Fetching last {count} log entries...</color>", append: false);

            try
            {
                string output = await LogAsync(root, count);

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=yellow>No history found for this path.</color>");
                }
                else
                {
                    string cleanOutput = StripBanner(output);

                    string coloredOutput = ApplyColoring(cleanOutput);

                    if (string.IsNullOrWhiteSpace(coloredOutput))
                    {
                        SVNLogBridge.LogLine("<color=yellow>Log is empty after filtering.</color>");
                    }
                    else
                    {
                        SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                        SVNLogBridge.LogLine(coloredOutput);
                        SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    }

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

        private string ApplyColoring(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;

            string[] lines = rawText.Split(new[] { "\n", "\r" }, StringSplitOptions.None);
            var compressedLines = new System.Collections.Generic.List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();

                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("r") && line.Contains(" | "))
                {
                    compressedLines.Add($"<color=yellow><b>{line}</b></color>");
                }
                else if (line.StartsWith("---"))
                {
                    compressedLines.Add($"<color=#444444>{line}</color>");
                }
                else
                {
                    compressedLines.Add($"<color=#E6E6E6>{line}</color>");
                }
            }

            return string.Join("\n", compressedLines);
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