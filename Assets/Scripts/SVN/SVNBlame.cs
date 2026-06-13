using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBlame : SVNBase
    {
        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager) { }

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
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                await ShowBlame(relativePath);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ShowBlame(string relativePath)
        {
            await svnManager.CancelBackgroundTasksAsync();

            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=red>Error:</color> Working Directory not set!");
                return;
            }

            LogBoth($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"blame --xml \"{relativePath}\"",
                    svnManager.WorkingDir,
                    false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=white>No data received. File might be unversioned or empty.</color>");
                    return;
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Parse(output);
                }
                catch (Exception)
                {
                    LogBoth("<color=yellow>Could not parse blame output as XML. Raw output:</color>");
                    LogBoth(output);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR       | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                var entries = doc.Descendants("entry");
                int lineNumber = 1;

                foreach (var entry in entries)
                {
                    string rev = entry.Attribute("revision")?.Value ?? "-";
                    string author = entry.Element("author")?.Value ?? "unknown";
                    string content = entry.Element("line")?.Value ?? string.Empty;

                    // Przycinamy autora do 12 znaków, by tabela się nie rozjeżdżała
                    string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;

                    string lineNumStr = $"<color=#666666>{lineNumber:D3}</color> | ";
                    string revStr = $"<color=#FFD700>{rev.PadRight(5)}</color> | ";
                    string authorStr = $"<color=#00E5FF>{authorShort.PadRight(12)}</color> | ";

                    sb.AppendLine($"{lineNumStr}{revStr}{authorStr}{content}");
                    lineNumber++;
                }

                if (svnUI.BlameDisplayArea != null)
                {
                    SVNLogBridge.UpdateUIField(svnUI.BlameDisplayArea, sb.ToString(), "BLAME_RESULT", append: false);
                    LogBoth("<color=green>Blame completed successfully.</color>");
                }
                else
                {
                    SVNLogBridge.LogError("BlameDisplayArea is null! Outputting to console.");
                    LogBoth(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"<color=red>Blame Error:</color> {ex.Message}");
            }
        }

        public async Task ShowBlameInExternalEditor(string relativePath)
        {
            await svnManager.CancelBackgroundTasksAsync();

            if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

            LogBoth($"Preparing Blame for external editor: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"blame --xml \"{relativePath}\"",
                    svnManager.WorkingDir,
                    false);

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
                catch (Exception)
                {
                    LogBoth("<color=yellow>Could not parse XML. Writing raw output to file.</color>");
                    WriteBlameToTempFileAndOpen(relativePath, output);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"SVN BLAME REPORT");
                sb.AppendLine($"File: {relativePath}");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("LINE |  REV  |   AUTHOR       | CONTENT");
                sb.AppendLine(new string('-', 60));

                var entries = doc.Descendants("entry");
                int lineNumber = 1;
                foreach (var entry in entries)
                {
                    string rev = entry.Attribute("revision")?.Value ?? "-";
                    string author = entry.Element("author")?.Value ?? "unknown";
                    string content = entry.Element("line")?.Value ?? string.Empty;

                    // Przycinanie autora dla czytelności
                    string authorShort = author.Length > 14 ? author.Substring(0, 14) : author;

                    sb.AppendLine($"{lineNumber:D3} | {rev,5} | {authorShort,-14} | {content}");
                    lineNumber++;
                }

                WriteBlameToTempFileAndOpen(relativePath, sb.ToString());
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"External Blame Error: {ex.Message}");
            }
        }

        public async Task ShowBlameInMainConsole(string relativePath)
        {
            await svnManager.CancelBackgroundTasksAsync();

            if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

            LogBoth($"Fetching Annotations for console: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"blame --xml \"{relativePath}\"",
                    svnManager.WorkingDir,
                    false);

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
                catch (Exception)
                {
                    LogBoth("<color=yellow>Could not parse XML. Raw output:</color>");
                    LogBoth(output);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"\n<size=110%><b>[ BLAME HISTORY: {Path.GetFileName(relativePath)} ]</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR       | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                var entries = doc.Descendants("entry");
                int lineNumber = 1;
                foreach (var entry in entries)
                {
                    string rev = entry.Attribute("revision")?.Value ?? "-";
                    string author = entry.Element("author")?.Value ?? "unknown";
                    string content = entry.Element("line")?.Value ?? string.Empty;

                    string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;

                    string lineNumStr = $"<color=#666666>{lineNumber:D3}</color> | ";
                    string revStr = $"<color=#FFD700>{rev.PadRight(5)}</color> | ";
                    string authorStr = $"<color=#00E5FF>{authorShort.PadRight(12)}</color> | ";

                    sb.AppendLine($"{lineNumStr}{revStr}{authorStr}{content}");
                    lineNumber++;
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
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Blame Console Error: {ex.Message}");
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
    }
}