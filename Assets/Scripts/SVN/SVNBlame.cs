using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBlame : SVNBase
    {
        private CancellationTokenSource _cts;
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        #region Thread Safety & Lifecycle

        public void Cancel() => _cts?.Cancel();

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

        private void PostLog(string msg)
        {
            PostUI(() => LogBoth(msg));
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
            });
        }

        #endregion

        #region Logging

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI?.BlameConsoleText ?? svnUI?.CommitConsoleContent;
            if (console != null)
                SVNLogBridge.UpdateUIField(console, msg, "BLAME", true);
        }

        #endregion

        #region Public API

        public void ExecuteBlame()
        {
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;

                string relativePath = svnUI?.BlameTargetFileInput?.text?.Trim();
                if (string.IsNullOrEmpty(relativePath))
                {
                    PostLog("<color=yellow>Please select a file path first.</color>");
                    ExitProcessing();
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                _cts = cts;
                try
                {
                    await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                    await ShowBlameInternal(relativePath, cts.Token, outputToConsole: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    PostLog("<color=orange>Blame cancelled or timed out.</color>");
                }
                finally
                {
                    _cts = null;
                    ExitProcessing();
                }
            });
        }

        public async Task ShowBlame(string relativePath, CancellationToken token = default)
        {
            await ShowBlameInternal(relativePath, token, outputToConsole: true).ConfigureAwait(false);
        }

        public async Task ShowBlameInExternalEditor(string relativePath)
        {
            if (!TryEnterProcessing()) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _cts = cts;
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, cts.Token, outputToConsole: false).ConfigureAwait(false);
            }
            finally
            {
                _cts = null;
                ExitProcessing();
            }
        }

        public async Task ShowBlameInMainConsole(string relativePath)
        {
            if (!TryEnterProcessing()) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _cts = cts;
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, cts.Token, outputToConsole: true, targetMainConsole: true).ConfigureAwait(false);
            }
            finally
            {
                _cts = null;
                ExitProcessing();
            }
        }

        #endregion

        #region Core Logic

        private async Task ShowBlameInternal(string relativePath, CancellationToken token, bool outputToConsole, bool targetMainConsole = false)
        {
            if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir))
            {
                PostLog("<color=#FFAA00>Error:</color> Working Directory not set!");
                return;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                PostLog("<color=yellow>No file selected.</color>");
                return;
            }

            PostLog($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            string raw = await SvnRunner.RunAsync(
                $"blame --xml \"{EscapeSvnArg(relativePath)}\"",
                svnManager.WorkingDir,
                false,
                token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(raw))
            {
                PostUI(() => DisplayBlameMessage("<color=yellow>Blame returned no data. The file may not be versioned or is empty.</color>", targetMainConsole));
                return;
            }

            int xmlStart = raw.IndexOf("<?xml", StringComparison.Ordinal);
            if (xmlStart < 0) xmlStart = raw.IndexOf("<blame", StringComparison.Ordinal);
            if (xmlStart < 0) xmlStart = raw.IndexOf("<target", StringComparison.Ordinal);
            if (xmlStart > 0)
                raw = raw.Substring(xmlStart);

            string logPreview = raw.Length > 300 ? raw.Substring(0, 300) + "…" : raw;
            SVNLogBridge.LogLine($"<color=grey>[Blame] XML preview:</color> {logPreview}");

            XDocument doc;
            try
            {
                doc = XDocument.Parse(raw);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=yellow>[Blame] XML parse error:</color> {ex.Message}");
                PostUI(() => DisplayBlameMessage("<color=yellow>Could not parse blame output. See log for details.</color>", targetMainConsole));
                return;
            }

            var entries = doc.Descendants("entry").ToList();
            SVNLogBridge.LogLine($"<color=grey>[Blame] Entries found: {entries.Count}</color>");

            if (entries.Count == 0)
            {
                bool isBinary = raw.IndexOf("Skipping binary file", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.Contains("<blame />") || raw.Contains("<blame/>");

                PostUI(() => DisplayBlameMessage(
                    isBinary
                        ? "<color=orange>Binary file – blame not available.</color>"
                        : "<color=yellow>No annotatable lines found (file may be binary or empty).</color>",
                    targetMainConsole));
                return;
            }

            var richReport = new StringBuilder();
            var plainReport = new StringBuilder();
            const int maxDisplayLines = 500;

            richReport.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
            richReport.AppendLine("<color=#444444>LINE | REV   | AUTHOR       | CONTENT</color>");
            richReport.AppendLine("--------------------------------------------------");

            plainReport.AppendLine("SVN BLAME REPORT");
            plainReport.AppendLine($"File: {relativePath}");
            plainReport.AppendLine($"Generated: {DateTime.Now}");
            plainReport.AppendLine(new string('-', 60));
            plainReport.AppendLine("LINE |  REV  |   AUTHOR       | CONTENT");
            plainReport.AppendLine(new string('-', 60));

            int lineNumber = 1;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                string rev = entry.Attribute("revision")?.Value ?? "-";
                string author = entry.Element("author")?.Value ?? "unknown";
                string content = entry.Element("line")?.Value ?? string.Empty;
                string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;
                string authorPlain = author.Length > 14 ? author.Substring(0, 14) : author;

                if (lineNumber <= maxDisplayLines)
                {
                    richReport.AppendLine(
                        $"<color=#666666>{lineNumber:D3}</color> | " +
                        $"<color=#FFD700>{rev.PadRight(5)}</color> | " +
                        $"<color=#00E5FF>{authorShort.PadRight(12)}</color> | {content}");
                }

                plainReport.AppendLine($"{lineNumber:D3} | {rev,5} | {authorPlain,-14} | {content}");
                lineNumber++;
            }

            if (entries.Count > maxDisplayLines)
            {
                richReport.AppendLine("\n<color=#FFAA00>... Blame truncated. Full report opened in external editor.</color>");
                plainReport.AppendLine("\n... (truncated)");
            }

            if (outputToConsole)
            {
                PostUI(() =>
                {
                    DisplayBlameMessage(richReport.ToString(), targetMainConsole);
                    LogBoth("<color=green>Blame displayed successfully.</color>");
                });
            }

            string cacheFolder = Path.Combine(Application.temporaryCachePath, "SVN_Cache");
            Directory.CreateDirectory(cacheFolder);

            string fileName = $"Blame_{Path.GetFileNameWithoutExtension(relativePath)}.txt";
            string tempPath = Path.Combine(cacheFolder, fileName);
            await File.WriteAllTextAsync(tempPath, plainReport.ToString(), token).ConfigureAwait(false);

            string absoluteTempPath = Path.GetFullPath(tempPath);
            string editorPath = GetMergeToolPath();

            PostUI(() =>
            {
                if (!string.IsNullOrEmpty(editorPath) && File.Exists(editorPath))
                {
                    using (Process.Start(new ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"\"{absoluteTempPath}\"",
                        UseShellExecute = true
                    })) { }
                    LogBoth("<color=green>Blame file opened in configured editor.</color>");
                }
                else
                {
                    Application.OpenURL("file://" + absoluteTempPath.Replace("\\", "/"));
                    LogBoth("<color=yellow>Editor path invalid, opened with system default.</color>");
                }
            });
        }

        #endregion

        #region UI Helpers

        private void DisplayBlameMessage(string message, bool targetMainConsole)
        {
            if (targetMainConsole && svnUI?.LogText != null)
            {
                svnUI.LogText.text = message;
                Canvas.ForceUpdateCanvases();
                if (svnUI.LogText.GetComponentInParent<UnityEngine.UI.ScrollRect>() is { } scroll)
                {
                    scroll.StopMovement();
                    scroll.verticalNormalizedPosition = 1f;
                }
                return;
            }

            if (svnUI?.BlameConsoleText != null)
            {
                svnUI.BlameConsoleText.text = message;
                Canvas.ForceUpdateCanvases();
            }
            else if (svnUI?.BlameDisplayArea != null)
            {
                svnUI.BlameDisplayArea.text = message;
                Canvas.ForceUpdateCanvases();
            }
            else
            {
                var fallback = svnUI?.CommitConsoleContent ?? svnUI?.LogText;
                if (fallback != null)
                {
                    fallback.text = message;
                    Canvas.ForceUpdateCanvases();
                }
                else
                {
                    SVNLogBridge.LogLine(message);
                }
            }
        }

        #endregion

        #region Helpers

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            return arg.Replace("\"", "\\\"");
        }

        private string GetMergeToolPath()
        {
            string path = svnManager?.MergeToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = svnUI?.SettingsMergeToolPathInput?.text;
            if (string.IsNullOrWhiteSpace(path))
                path = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");

            return path?.Trim().Replace("\"", "");
        }

        #endregion
    }
}