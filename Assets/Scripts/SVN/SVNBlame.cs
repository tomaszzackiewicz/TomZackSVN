using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using UnityEngine;
using SFB;

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
                await ShowBlame(relativePath);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ShowBlame(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=red>Error:</color> Working Directory not set!");
                return;
            }

            LogBoth($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync($"blame \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=white>No data received. File might be unversioned or empty.</color>");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR     | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                using (var reader = new StringReader(output))
                {
                    string line;
                    int lineCount = 1;

                    while ((line = reader.ReadLine()) != null)
                    {
                        var match = Regex.Match(line, @"^\s*(\d+|-)\s+([^\s]+)\s?(.*)$");

                        if (match.Success)
                        {
                            string rev = match.Groups[1].Value.Trim();
                            string author = match.Groups[2].Value.Trim();
                            string content = match.Groups[3].Value;

                            string lineNumStr = $"<color=#666666>{lineCount:D3}</color> | ";
                            string revStr = $"<color=#FFD700>{rev.PadRight(5)}</color> | ";
                            string authorStr = $"<color=#00E5FF>{author.PadRight(10)}</color> | ";

                            sb.AppendLine($"{lineNumStr}{revStr}{authorStr}{content}");
                        }
                        else
                        {
                            sb.AppendLine($"<color=#666666>{lineCount:D3}</color> | {line}");
                        }
                        lineCount++;
                    }
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
                SVNLogBridge.LogError($"<color=red>Parsing Error:</color> {ex.Message}");
            }
        }

        public async Task ShowBlameInExternalEditor(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

            LogBoth($"Preparing Blame for external editor: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync($"blame \"{relativePath}\"", svnManager.WorkingDir, false);
                if (string.IsNullOrWhiteSpace(output)) return;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"SVN BLAME REPORT");
                sb.AppendLine($"File: {relativePath}");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("LINE |  REV  |   AUTHOR   | CONTENT");
                sb.AppendLine(new string('-', 60));

                using (var reader = new StringReader(output))
                {
                    string line;
                    int lineCount = 1;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var match = Regex.Match(line, @"^\s*(\d+|-)\s+([^\s]+)\s?(.*)$");
                        if (match.Success)
                        {
                            sb.AppendLine($"{lineCount:D3} | {match.Groups[1].Value,5} | {match.Groups[2].Value,10} | {match.Groups[3].Value}");
                        }
                        else
                        {
                            sb.AppendLine($"{lineCount:D3} | {line}");
                        }
                        lineCount++;
                    }
                }

                string cacheFolder = Path.Combine(Application.temporaryCachePath, "SVN_Cache");
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                string fileName = $"Blame_{Path.GetFileNameWithoutExtension(relativePath)}.txt";
                string tempPath = Path.Combine(cacheFolder, fileName);
                File.WriteAllText(tempPath, sb.ToString());

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
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"External Blame Error: {ex.Message}");
            }
        }

        public async Task ShowBlameInMainConsole(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

            LogBoth($"Fetching Annotations for console: <color=green>{relativePath}</color>...");

            try
            {
                string output = await SvnRunner.RunAsync($"blame \"{relativePath}\"", svnManager.WorkingDir, false);
                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=yellow>Blame output is empty.</color>");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"\n<size=110%><b>[ BLAME HISTORY: {Path.GetFileName(relativePath)} ]</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR     | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                using (var reader = new StringReader(output))
                {
                    string line;
                    int lineCount = 1;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var match = Regex.Match(line, @"^\s*(\d+|-)\s+([^\s]+)\s?(.*)$");
                        if (match.Success)
                        {
                            string rev = match.Groups[1].Value.Trim();
                            string author = match.Groups[2].Value.Trim();
                            string content = match.Groups[3].Value;

                            string lineNumStr = $"<color=#666666>{lineCount:D3}</color> | ";
                            string revStr = $"<color=#FFD700>{rev.PadRight(5)}</color> | ";
                            string authorStr = $"<color=#00E5FF>{author.PadRight(10)}</color> | ";

                            sb.AppendLine($"{lineNumStr}{revStr}{authorStr}{content}");
                        }
                        else
                        {
                            sb.AppendLine($"<color=#666666>{lineCount:D3}</color> | {line}");
                        }
                        lineCount++;
                    }
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
    }
}