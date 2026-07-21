using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLog : SVNBase
    {
        private CancellationTokenSource _logCts;

        public SVNLog(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Cancel()
        {
            _logCts?.Cancel();
        }

        public async void ShowLog()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path not found.");
                return;
            }

            int count = 10;
            if (svnUI.LogCountInputField != null && !string.IsNullOrWhiteSpace(svnUI.LogCountInputField.text))
                int.TryParse(svnUI.LogCountInputField.text, out count);
            count = Mathf.Clamp(count, 1, 500);

            IsProcessing = true;
            _logCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = _logCts.Token;

            SVNLogBridge.LogLine($"[{DateTime.Now:HH:mm:ss}] <color=#00FF99>Fetching last {count} log entries...</color>", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string output = await LogAsync(root, count, token);

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=yellow>No history found for this path.</color>");
                }
                else
                {
                    string coloredOutput = ApplyColoring(StripBanner(output));
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    SVNLogBridge.LogLine(coloredOutput);
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    await ScrollToBottom();
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Log request cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Log Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _logCts?.Dispose();
                _logCts = null;
            }
        }

        public static async Task<string> LogAsync(string workingDir, int lastN = 10, CancellationToken token = default)
        {
            return await SvnRunner.RunAsync($"log -l {lastN}", workingDir, token: token);
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

        public async void ShowLogForPath(string relativePath)
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            relativePath = SvnRunner.ForceCleanPath(relativePath);
            root = SvnRunner.ForceCleanPath(root);
            if (string.IsNullOrWhiteSpace(relativePath)) return;

            string absolutePath = Path.Combine(root, relativePath);
            absolutePath = SvnRunner.ForceCleanPath(absolutePath);

            int count = 10;
            if (svnUI.LogCountInputField != null && !string.IsNullOrWhiteSpace(svnUI.LogCountInputField.text))
                int.TryParse(svnUI.LogCountInputField.text, out count);
            count = Mathf.Clamp(count, 1, 500);

            IsProcessing = true;
            _logCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = _logCts.Token;

            SVNLogBridge.LogLine($"<color=#00FF99>Fetching history for: {absolutePath}</color>", append: false);

            try
            {
                string statusCheck = await SvnRunner.RunAsync($"status \"{absolutePath}\"", root, token: token);
                if (string.IsNullOrEmpty(statusCheck) || statusCheck.StartsWith("?"))
                {
                    SVNLogBridge.LogLine("<color=yellow>File is not under version control – no history available.</color>");
                    return;
                }

                string output = await SvnRunner.RunAsync($"log -l {count} \"{absolutePath}\"", root, token: token);

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=yellow>No history found for this file.</color>");
                }
                else
                {
                    string coloredOutput = ApplyColoring(output);
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    SVNLogBridge.LogLine(coloredOutput);
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Log request cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Log Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _logCts?.Dispose();
                _logCts = null;
            }
        }
    }
}