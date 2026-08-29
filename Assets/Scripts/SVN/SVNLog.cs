using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLog : SVNBase, IDisposable
    {
        private CancellationTokenSource _logCts;
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private int _disposed;
        private int _processingFlag;

        public SVNLog(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Cancel()
        {
            try
            {
                var cts = Volatile.Read(ref _logCts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        public async void ShowLog()
        {
            try { await ShowLogAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Critical Log Error:</color> {ex.Message}");
            }
        }

        public async void ShowLogForPath(string relativePath)
        {
            try { await ShowLogForPathAsync(relativePath).ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Critical Log Error:</color> {ex.Message}");
            }
        }

        public void ClearLog()
        {
            SVNLogBridge.ClearConsole();
        }

        private async Task ShowLogAsync()
        {
            if (!await TryEnterProcessingAsync().ConfigureAwait(false)) return;

            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path not found.");
                    return;
                }

                int count = ParseLogCount();
                ResetCts(TimeSpan.FromSeconds(60));
                var token = Volatile.Read(ref _logCts)?.Token ?? CancellationToken.None;

                SVNLogBridge.LogLine(
                    $"[{DateTime.Now:HH:mm:ss}] <color=#00FF99>Fetching last {count} log entries...</color>",
                    append: false);

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string output = await LogAsync(root, count, token).ConfigureAwait(false);

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
                    await ScrollToBottomOnMainThreadAsync().ConfigureAwait(false);
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
                ExitProcessing();
            }
        }

        private async Task ShowLogForPathAsync(string relativePath)
        {
            if (!await TryEnterProcessingAsync().ConfigureAwait(false)) return;

            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root))
                    return;

                relativePath = SvnRunner.ForceCleanPath(relativePath);
                if (string.IsNullOrWhiteSpace(relativePath))
                    return;

                bool isServerUrl =
                    relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("svn://", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase);

                string targetPath;
                if (isServerUrl)
                {
                    targetPath = relativePath;
                }
                else
                {
                    root = SvnRunner.ForceCleanPath(root);
                    targetPath = SvnRunner.ForceCleanPath(Path.Combine(root, relativePath));
                }

                int count = ParseLogCount();
                ResetCts(TimeSpan.FromSeconds(60));
                var token = Volatile.Read(ref _logCts)?.Token ?? CancellationToken.None;

                SVNLogBridge.LogLine($"<color=#00FF99>Fetching history for: {targetPath}</color>", append: false);

                if (!isServerUrl)
                {
                    string statusCheck = await SvnRunner.RunAsync(
                        $"status \"{EscapeSvnArg(targetPath)}\"", root, token: token).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(statusCheck) &&
                        statusCheck.TrimStart().StartsWith("?", StringComparison.Ordinal))
                    {
                        SVNLogBridge.LogLine(
                            "<color=yellow>File is not under version control – no history available.</color>");
                        return;
                    }
                }

                string output = await SvnRunner.RunAsync(
                    $"log -l {count} \"{EscapeSvnArg(targetPath)}\"", root, token: token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=yellow>No history found for this file.</color>");
                }
                else
                {
                    string coloredOutput = ApplyColoring(StripBanner(output));
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    SVNLogBridge.LogLine(coloredOutput);
                    SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
                    await ScrollToBottomOnMainThreadAsync().ConfigureAwait(false);
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
                ExitProcessing();
            }
        }

        private void ResetCts(TimeSpan timeout)
        {
            var oldCts = Interlocked.Exchange(ref _logCts, new CancellationTokenSource(timeout));
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                try { oldCts.Dispose(); } catch { }
            }
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            string normalized = arg.Replace('\\', '/');
            return normalized.Replace("\"", "\\\"");
        }

        public static Task<string> LogAsync(string workingDir, int lastN = 10, CancellationToken token = default)
        {
            return SvnRunner.RunAsync($"log -l {lastN}", workingDir, token: token);
        }

        private int ParseLogCount()
        {
            int count = 10;
            if (svnUI.LogCountInputField != null &&
                !string.IsNullOrWhiteSpace(svnUI.LogCountInputField.text) &&
                int.TryParse(svnUI.LogCountInputField.text, out int parsed))
            {
                count = parsed;
            }
            return Mathf.Clamp(count, 1, 500);
        }

        private static string ApplyColoring(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;

            string[] lines = rawText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var compressedLines = new List<string>(lines.Length);

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();

                if (line.StartsWith("r", StringComparison.Ordinal) && line.Contains(" | "))
                    compressedLines.Add($"<color=yellow><b>{line}</b></color>");
                else if (line.StartsWith("---", StringComparison.Ordinal))
                    compressedLines.Add($"<color=#444444>{line}</color>");
                else
                    compressedLines.Add($"<color=#E6E6E6>{line}</color>");
            }

            return string.Join("\n", compressedLines);
        }

        private Task ScrollToBottomOnMainThreadAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    if (svnUI.LogScrollRect != null)
                    {
                        Canvas.ForceUpdateCanvases();
                        svnUI.LogScrollRect.verticalNormalizedPosition = 0f;
                    }
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private async Task<bool> TryEnterProcessingAsync()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;

            try
            {
                if (!await _processingLock.WaitAsync(0).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _processingFlag, 0);
                    return false;
                }
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref _processingFlag, 0);
                return false;
            }

            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);

            try { _logCts?.Dispose(); }
            catch (ObjectDisposedException) { }
            Volatile.Write(ref _logCts, null);

            try { _processingLock.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            Cancel();

            var cts = Interlocked.Exchange(ref _logCts, null);
            if (cts != null)
            {
                try { cts.Dispose(); } catch { }
            }

            try { _processingLock.Dispose(); } catch { }
        }
    }
}