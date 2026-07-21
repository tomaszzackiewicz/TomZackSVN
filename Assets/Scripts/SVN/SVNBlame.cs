using System;
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
        private (string relativePath, string output) _lastBlameResult;

        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Cancel() => _cts?.Cancel();

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI.BlameConsoleText != null ? svnUI.BlameConsoleText : svnUI.CommitConsoleContent;
            SVNLogBridge.UpdateUIField(console, msg, "BLAME", true);
        }

        public async void ExecuteBlame()
        {
            if (IsProcessing) return;

            string relativePath = svnUI.BlameTargetFileInput?.text.Trim();
            if (string.IsNullOrEmpty(relativePath))
            {
                LogBoth("<color=yellow>Please select a file path first.</color>");
                return;
            }

            IsProcessing = true;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                await ShowBlame(relativePath, token);
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange>Blame cancelled or timed out.</color>");
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public async Task ShowBlame(string relativePath, CancellationToken token = default)
        {
            await svnManager.CancelBackgroundTasksAsync();

            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=#FFAA00>Error:</color> Working Directory not set!");
                return;
            }

            LogBoth($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            string raw = await SvnRunner.RunAsync($"blame --xml \"{relativePath}\"", svnManager.WorkingDir, false, token);

            if (string.IsNullOrWhiteSpace(raw))
            {
                DisplayBlameMessage("<color=yellow>Blame returned no data. The file may not be versioned or is empty.</color>");
                return;
            }

            // Oczyść z komunikatów serwera
            int xmlStart = raw.IndexOf("<?xml", StringComparison.Ordinal);
            if (xmlStart < 0) xmlStart = raw.IndexOf("<blame", StringComparison.Ordinal);
            if (xmlStart < 0) xmlStart = raw.IndexOf("<target", StringComparison.Ordinal);
            if (xmlStart > 0)
                raw = raw.Substring(xmlStart);

            // Diagnostyka tylko do logów, nie do UI
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
                DisplayBlameMessage("<color=yellow>Could not parse blame output. See log for details.</color>");
                return;
            }

            var entries = doc.Descendants("entry").ToList();
            SVNLogBridge.LogLine($"<color=grey>[Blame] Entries found: {entries.Count}</color>");

            // ========== Plik binarny / brak danych ==========
            if (entries.Count == 0)
            {
                if (raw.IndexOf("Skipping binary file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    raw.Contains("<blame />") || raw.Contains("<blame/>"))
                {
                    DisplayBlameMessage("<color=orange>Binary file – blame not available.</color>");
                }
                else
                {
                    DisplayBlameMessage("<color=yellow>No annotatable lines found (file may be binary or empty).</color>");
                }
                return; // NIE otwieramy zewnętrznego edytora
            }

            // ========== Raport sformatowany (konsola) ==========
            var sb = new StringBuilder();
            sb.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
            sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR       | CONTENT</color>");
            sb.AppendLine("--------------------------------------------------");

            int lineNumber = 1;
            const int maxDisplayLines = 500;
            foreach (var entry in entries.Take(maxDisplayLines))
            {
                token.ThrowIfCancellationRequested();
                string rev = entry.Attribute("revision")?.Value ?? "-";
                string author = entry.Element("author")?.Value ?? "unknown";
                string content = entry.Element("line")?.Value ?? string.Empty;
                string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;
                sb.AppendLine($"<color=#666666>{lineNumber:D3}</color> | <color=#FFD700>{rev.PadRight(5)}</color> | <color=#00E5FF>{authorShort.PadRight(12)}</color> | {content}");
                lineNumber++;
            }

            if (entries.Count > maxDisplayLines)
                sb.AppendLine("\n<color=#FFAA00>... Blame truncated. Full report opened in external editor.</color>");

            DisplayBlameMessage(sb.ToString());
            LogBoth("<color=green>Blame displayed successfully.</color>");

            // ========== Pełny raport do pliku ==========
            var plainReport = new StringBuilder();
            plainReport.AppendLine("SVN BLAME REPORT");
            plainReport.AppendLine($"File: {relativePath}");
            plainReport.AppendLine($"Generated: {DateTime.Now}");
            plainReport.AppendLine(new string('-', 60));
            plainReport.AppendLine("LINE |  REV  |   AUTHOR       | CONTENT");
            plainReport.AppendLine(new string('-', 60));

            lineNumber = 1;
            foreach (var entry in entries)
            {
                string rev = entry.Attribute("revision")?.Value ?? "-";
                string author = entry.Element("author")?.Value ?? "unknown";
                string content = entry.Element("line")?.Value ?? string.Empty;
                string authorShort = author.Length > 14 ? author.Substring(0, 14) : author;
                plainReport.AppendLine($"{lineNumber:D3} | {rev,5} | {authorShort,-14} | {content}");
                lineNumber++;
            }

            WriteBlameToTempFileAndOpen(relativePath, plainReport.ToString());
        }

        private void DisplayBlameMessage(string message)
        {
            if (svnUI.BlameConsoleText != null)
            {
                svnUI.BlameConsoleText.text = message;
                Canvas.ForceUpdateCanvases();
            }
            else if (svnUI.BlameDisplayArea != null)
            {
                svnUI.BlameDisplayArea.text = message;
                Canvas.ForceUpdateCanvases();
            }
            else
            {
                var fallback = svnUI.CommitConsoleContent ?? svnUI.LogText;
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

        private void WriteBlameToTempFileAndOpen(string relativePath, string content)
        {
            string cacheFolder = Path.Combine(Application.temporaryCachePath, "SVN_Cache");
            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

            string fileName = $"Blame_{Path.GetFileNameWithoutExtension(relativePath)}.txt";
            string tempPath = Path.Combine(cacheFolder, fileName);
            File.WriteAllText(tempPath, content);

            string absoluteTempPath = Path.GetFullPath(tempPath);
            string editorPath = svnManager.MergeToolPath;

            if (!string.IsNullOrEmpty(editorPath) && File.Exists(editorPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = editorPath,
                    Arguments = $"\"{absoluteTempPath}\"",
                    UseShellExecute = true
                });
                LogBoth($"<color=green>Blame file opened in configured editor.</color>");
            }
            else
            {
                Application.OpenURL("file://" + absoluteTempPath.Replace("\\", "/"));
                LogBoth("<color=yellow>Editor path invalid, opened with system default.</color>");
            }
        }

        // Poniższe metody pozostawione dla ewentualnych wywołań z innych miejsc,
        // ale nie są już używane w podstawowym panelu Blame.
        public async Task ShowBlameInExternalEditor(string relativePath)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

                LogBoth($"Preparing Blame for external editor: <color=green>{relativePath}</color>...");

                string output = await SvnRunner.RunAsync($"blame --xml \"{relativePath}\"", svnManager.WorkingDir, false, token);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=yellow>Blame output is empty.</color>");
                    return;
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Parse(output);
                }
                catch
                {
                    LogBoth("<color=yellow>Could not parse XML. Writing raw output to file.</color>");
                    WriteBlameToTempFileAndOpen(relativePath, output);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("SVN BLAME REPORT");
                sb.AppendLine($"File: {relativePath}");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("LINE |  REV  |   AUTHOR       | CONTENT");
                sb.AppendLine(new string('-', 60));

                int lineNumber = 1;
                foreach (var entry in doc.Descendants("entry"))
                {
                    token.ThrowIfCancellationRequested();

                    string rev = entry.Attribute("revision")?.Value ?? "-";
                    string author = entry.Element("author")?.Value ?? "unknown";
                    string content = entry.Element("line")?.Value ?? string.Empty;

                    string authorShort = author.Length > 14 ? author.Substring(0, 14) : author;
                    sb.AppendLine($"{lineNumber:D3} | {rev,5} | {authorShort,-14} | {content}");
                    lineNumber++;
                }

                WriteBlameToTempFileAndOpen(relativePath, sb.ToString());
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange>Blame cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"External Blame Error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public async Task ShowBlameInMainConsole(string relativePath)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

                string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);
                relativePath = SvnRunner.ForceCleanPath(relativePath);
                if (string.IsNullOrWhiteSpace(relativePath)) return;

                string absolutePath = Path.Combine(root, relativePath);
                absolutePath = SvnRunner.ForceCleanPath(absolutePath);

                LogBoth($"Fetching Annotations for console: <color=green>{absolutePath}</color>...");

                string output = await SvnRunner.RunAsync($"blame --xml \"{absolutePath}\"", root, false, token);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=yellow>Blame output is empty.</color>");
                    return;
                }

                XDocument doc;
                try
                {
                    int xmlStart = output.IndexOf('<');
                    if (xmlStart > 0)
                        output = output.Substring(xmlStart);
                    doc = XDocument.Parse(output);
                }
                catch
                {
                    LogBoth("<color=yellow>Could not parse XML. Raw output:</color>");
                    LogBoth(output);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"\n<size=110%><b>[ BLAME HISTORY: {Path.GetFileName(absolutePath)} ]</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR       | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                int lineNumber = 1;
                const int maxDisplayLines = 500;
                foreach (var entry in doc.Descendants("entry").Take(maxDisplayLines))
                {
                    token.ThrowIfCancellationRequested();

                    string rev = entry.Attribute("revision")?.Value ?? "-";
                    string author = entry.Element("author")?.Value ?? "unknown";
                    string content = entry.Element("line")?.Value ?? string.Empty;

                    string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;

                    sb.AppendLine($"<color=#666666>{lineNumber:D3}</color> | <color=#FFD700>{rev.PadRight(5)}</color> | <color=#00E5FF>{authorShort.PadRight(12)}</color> | {content}");
                    lineNumber++;
                }

                int totalEntries = doc.Descendants("entry").Count();
                if (totalEntries > maxDisplayLines)
                {
                    sb.AppendLine("\n<color=#FFAA00>... Blame truncated. Full report opened in external editor.</color>");
                }

                sb.AppendLine("--------------------------------------------------\n");

                if (svnUI.LogText != null)
                {
                    svnUI.LogText.text = sb.ToString();
                    Canvas.ForceUpdateCanvases();
                    var scroll = svnUI.LogText.GetComponentInParent<UnityEngine.UI.ScrollRect>();
                    if (scroll != null)
                    {
                        scroll.StopMovement();
                        scroll.verticalNormalizedPosition = 1f;
                    }
                    LogBoth("<color=green>Full blame displayed successfully.</color>");
                }
            }
            catch (OperationCanceledException)
            {
                LogBoth("<color=orange>Blame cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Blame Console Error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}