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

        // === FIX Ś2: odczyt inputu na main thread (wejście z przycisku).
        public async void ShowLog()
        {
            int count = ParseLogCount();
            try { await ShowLogAsync(count).ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Critical Log Error:</color> {ex.Message}");
            }
        }

        public async void ShowLogForPath(string relativePath)
        {
            int count = ParseLogCount();
            try { await ShowLogForPathAsync(relativePath, count).ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Critical Log Error:</color> {ex.Message}");
            }
        }

        public void ClearLog()
        {
            SVNLogBridge.ClearConsole();
        }

        private async Task ShowLogAsync(int count)
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

                // === FIX K1: token z LOKALNEJ referencji; Exchange + delayed dispose.
                // Wcześniej 'Volatile.Read(_logCts)?.Token ?? None' — między odczytem
                // referencji a pobraniem Token inny wątek (ExitProcessing) mógł
                // zdisposować CTS → ObjectDisposedException POZA try/catch callerów.
                var token = ResetCts(TimeSpan.FromSeconds(60));

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
                    await RenderLogOutputAsync(output).ConfigureAwait(false);
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

        private async Task ShowLogForPathAsync(string relativePath, int count)
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

                // === FIX K1: jw. — token z lokalnej referencji.
                var token = ResetCts(TimeSpan.FromSeconds(60));

                SVNLogBridge.LogLine($"<color=#00FF99>Fetching history for: {targetPath}</color>", append: false);

                if (!isServerUrl)
                {
                    // === FIX Ś1: status na ścieżce WZGLĘDNEJ — wystarcza do checku '?',
                    // a output svn nie rozjeżdża się z Expectacją (na absolutnej
                    // svn zwraca ścieżki absolutne i check 'StartsWith("?")' był kruchy).
                    string statusCheck = await SvnRunner.RunAsync(
                        $"status \"{EscapeSvnArg(relativePath)}\"", root, token: token).ConfigureAwait(false);

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
                    await RenderLogOutputAsync(output).ConfigureAwait(false);
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

        // === FIX Ś-drobne: wspólny rendering (dedylikacja duplikatu z dwóch metod).
        private async Task RenderLogOutputAsync(string output)
        {
            string coloredOutput = ApplyColoring(StripBanner(output));
            SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
            SVNLogBridge.LogLine(coloredOutput);
            SVNLogBridge.LogLine("<color=#444444>------------------------------------------</color>");
            await ScrollToBottomOnMainThreadAsync().ConfigureAwait(false);
        }

        // === FIX K1: Exchange + delayed dispose; ZWRACA token (wywołujący trzyma
        // lokalną referencję — pole może być podmienione przez następną operację,
        // ale TEN token pozostaje ważny do końca bieżącej).
        private CancellationToken ResetCts(TimeSpan timeout)
        {
            var newCts = new CancellationTokenSource(timeout);
            var oldCts = Interlocked.Exchange(ref _logCts, newCts);

            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }

            return newCts.Token;
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

        // UWAGA: czyta UI — wywoływać na main thread (wejścia publiczne to robią).
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

        // === FIX K1: ExitProcessing NIE rusza _logCts (własność operacji, nie
        // guardu). Wcześniej dispose+null w locie — źródło race z Cancel() i
        // tokenami trzymanymi przez callerów.
        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);

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
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }

            try { _processingLock.Dispose(); } catch { }
        }
    }
}