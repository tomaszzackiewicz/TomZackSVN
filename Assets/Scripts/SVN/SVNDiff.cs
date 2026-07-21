using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    public class SVNDiff : SVNBase
    {
        public SVNDiff(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI.DiffConsoleText != null ? svnUI.DiffConsoleText : svnUI.CommitConsoleContent;
            SVNLogBridge.UpdateUIField(console, msg, "DIFF", true);
        }

        public void Button_BrowseDiffFilePath()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                LogBoth("<color=#FFAA00>Error:</color> Working Directory is not set!");
                return;
            }

            var extensions = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');
                else
                    LogBoth("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");

                if (svnUI.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    LogBoth($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
            }
        }

        public async void ExecuteDiff()
        {
            if (IsProcessing) return;

            string relativePath = svnUI.DiffTargetFileInput?.text.Trim();
            if (string.IsNullOrEmpty(relativePath))
            {
                LogBoth("<color=yellow>Please select or enter a file path first.</color>");
                return;
            }

            IsProcessing = true;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                await ShowDiff(relativePath);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ShowDiff(string relativePath)
        {
            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                if (string.IsNullOrEmpty(svnManager.WorkingDir))
                {
                    LogBoth("<color=#FFAA00>Error:</color> Working Directory is not set!");
                    return;
                }

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    LogBoth("<color=yellow>No file selected.</color>");
                    return;
                }

                string editorPath = GetMergeToolPath();
                if (string.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
                {
                    LogBoth($"<color=#FFAA00>Error:</color> Invalid Diff Tool path! (Found: '{editorPath}')");
                    return;
                }

                LogBoth($"Comparing: <color=green>{relativePath}</color>...");

                string diffContent = await SvnRunner.RunAsync($"diff \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    LogBoth("<color=white>No local changes detected.</color>");
                    return;
                }

                if (diffContent.Contains("Cannot display: file marked as a binary type"))
                {
                    LogBoth("<color=orange>Binary File:</color> Opening Explorer...");
                    string fullPath = Path.Combine(svnManager.WorkingDir, relativePath).Replace("/", "\\");
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                    return;
                }

                // Formatuj diff do wyświetlenia w Unity
                string formatted = FormatDiffForUnity(diffContent);

                // Ogranicz liczbę linii, aby nie przeciążyć TextMeshPro
                string[] previewLines = formatted.Split(new[] { '\n' }, StringSplitOptions.None);
                if (previewLines.Length > 500)
                {
                    formatted = string.Join("\n", previewLines.Take(500))
                                + "\n\n<color=#FFAA00>... Diff truncated. Full diff opened in external editor.</color>";
                }

                // Wyświetl sformatowany diff w konsoli panelu
                var targetField = svnUI.DiffConsoleText ?? svnUI.CommitConsoleContent ?? svnUI.LogText;
                if (targetField != null)
                {
                    targetField.text = formatted;
                    var scrollRect = targetField.GetComponentInParent<UnityEngine.UI.ScrollRect>();
                    if (scrollRect != null)
                        scrollRect.verticalNormalizedPosition = 1f;
                }

                // Wyświetl podsumowanie statystyk
                (int added, int removed) = CountDiffStats(diffContent);
                LogBoth($"<color=#00D0FF><b>Diff Summary:</b></color> <color=#6AFF9E>+{added} lines added</color>, <color=#800020>-{removed} lines removed</color>");

                // Zapisz i otwórz w zewnętrznym edytorze
                string tempDiffPath = Path.Combine(Application.temporaryCachePath, "svn_diff_preview.diff");
                string enrichedContent = FormatDiffForExternalEditor(diffContent);
                await File.WriteAllTextAsync(tempDiffPath, enrichedContent);
                System.Diagnostics.Process.Start(editorPath, $"\"{tempDiffPath}\"");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Exception:</color> {ex.Message}");
            }
        }

        private (int added, int removed) CountDiffStats(string diffContent)
        {
            int added = 0, removed = 0;
            string[] lines = diffContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("+") && !line.StartsWith("+++")) added++;
                else if (line.StartsWith("-") && !line.StartsWith("---")) removed++;
            }
            return (added, removed);
        }

        private string GetMergeToolPath()
        {
            string path = svnManager.MergeToolPath;
            if (!string.IsNullOrWhiteSpace(path))
                return path.Trim().Replace("\"", "");

            if (svnUI.SettingsMergeToolPathInput != null)
            {
                path = svnUI.SettingsMergeToolPathInput.text;
                if (!string.IsNullOrWhiteSpace(path))
                    return path.Trim().Replace("\"", "");
            }

            path = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
            return path.Trim().Replace("\"", "");
        }

        private string FormatDiffForExternalEditor(string rawDiff)
        {
            string[] lines = rawDiff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            System.Text.StringBuilder formattedBody = new System.Text.StringBuilder();

            int oldLine = 0;
            int newLine = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("@@"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+),?\d* \+(\d+),?\d* @@");
                    if (match.Success)
                    {
                        oldLine = int.Parse(match.Groups[1].Value);
                        newLine = int.Parse(match.Groups[2].Value);
                    }
                    formattedBody.AppendLine($"\n[ SEKCJA: Linia {oldLine} ]\n" + line);
                }
                else if (line.StartsWith("-"))
                {
                    formattedBody.AppendLine($"{oldLine,-5} |       | {line}");
                    oldLine++;
                }
                else if (line.StartsWith("+"))
                {
                    formattedBody.AppendLine($"      | {newLine,-5} | {line}");
                    newLine++;
                }
                else if (line.StartsWith(" ") || string.IsNullOrEmpty(line))
                {
                    formattedBody.AppendLine($"{oldLine,-5} | {newLine,-5} | {line}");
                    oldLine++;
                    newLine++;
                }
                else
                {
                    formattedBody.AppendLine(line);
                }
            }

            return formattedBody.ToString();
        }

        public async Task ShowPreviewInUnity(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir))
            {
                LogBoth("<color=#FFAA00>Error:</color> Working Directory is not set!");
                return;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                LogBoth("<color=yellow>No file selected.</color>");
                return;
            }

            string fullPath = Path.Combine(svnManager.WorkingDir, relativePath);
            if (Directory.Exists(fullPath))
            {
                LogBoth("<color=yellow>Preview for directories is not supported. Select a file.</color>");
                return;
            }

            LogBoth("Loading diff...");

            try
            {
                string diffContent = await SvnRunner.RunAsync($"diff \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    LogBoth("<color=white>No local changes detected (or file is unversioned).</color>");
                    return;
                }

                if (diffContent.Contains("Cannot display: file marked as a binary type"))
                {
                    LogBoth("<color=#FF8080>Binary File:</color> Text preview is not available.");
                    return;
                }

                string formatted = FormatDiffForUnity(diffContent);

                // Ograniczenie dla bezpieczeństwa
                string[] previewLines = formatted.Split(new[] { '\n' }, StringSplitOptions.None);
                if (previewLines.Length > 500)
                {
                    formatted = string.Join("\n", previewLines.Take(500))
                                + "\n\n<color=#FFAA00>... Diff truncated. Open external diff for full content.</color>";
                }

                var targetField = svnUI.DiffConsoleText ?? svnUI.CommitConsoleContent ?? svnUI.LogText;
                if (targetField != null)
                {
                    targetField.text = formatted;
                    var scrollRect = targetField.GetComponentInParent<UnityEngine.UI.ScrollRect>();
                    if (scrollRect != null)
                        scrollRect.verticalNormalizedPosition = 1f;
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=#FFAA00>Preview Error:</color> {ex.Message}");
            }
        }

        private string FormatDiffForUnity(string rawDiff)
        {
            string[] lines = rawDiff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new System.Text.StringBuilder();

            int oldLine = 0, newLine = 0;
            bool hasSection = false;
            int added = 0, removed = 0, unchanged = 0;
            string fileOld = "", fileNew = "";

            string colNum = "#FFFFFF";
            string colRem = "#800020";
            string colAdd = "#6AFF9E";
            string colInfo = "#FFD800";
            int wNum = 8;
            string monoStart = "<mspace=0.6em>";
            string monoEnd = "</mspace>";
            string gapNum = "";
            string gap = "  ";

            foreach (string raw in lines)
            {
                if (raw.StartsWith("--- ")) fileOld = raw.Substring(4).Trim();
                if (raw.StartsWith("+++ ")) fileNew = raw.Substring(4).Trim();
            }

            foreach (string raw in lines)
            {
                string line = raw.Replace("\t", "    ");
                line = line.Replace("<", "<noparse><</noparse>").Replace(">", "<noparse>></noparse>");

                if (line.StartsWith("@@"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+),?\d* \+(\d+),?\d* @@");
                    if (match.Success)
                    {
                        oldLine = int.Parse(match.Groups[1].Value);
                        newLine = int.Parse(match.Groups[2].Value);
                        hasSection = true;
                    }
                    sb.AppendLine($"\n<color={colInfo}>──────── SECTION (line {newLine}) ────────</color>");
                    continue;
                }

                if (!hasSection) continue;

                string sOld = oldLine.ToString().PadLeft(wNum);
                string sNew = newLine.ToString().PadLeft(wNum);

                if (line.StartsWith("-"))
                {
                    removed++;
                    sb.AppendLine(
                        $"{monoStart}<color={colNum}>{sOld}</color>{monoEnd}{gapNum}{monoStart}{new string(' ', wNum)}{monoEnd}{gap}<color={colRem}>-</color>{gap}<color={colRem}>{line.Substring(1)}</color>"
                    );
                    oldLine++;
                }
                else if (line.StartsWith("+"))
                {
                    added++;
                    sb.AppendLine(
                        $"{monoStart}{new string(' ', wNum)}{monoEnd}{gapNum}{monoStart}<color={colNum}>{sNew}</color>{monoEnd}{gap}<color={colAdd}>+</color>{gap}<color={colAdd}>{line.Substring(1)}</color>"
                    );
                    newLine++;
                }
                else
                {
                    unchanged++;
                    sb.AppendLine(
                        $"{monoStart}<color={colNum}>{sOld}</color>{monoEnd}{gapNum}{monoStart}<color={colNum}>{sNew}</color>{monoEnd}{gap}   {gap}{line}"
                    );
                    oldLine++;
                    newLine++;
                }
            }

            var header = new System.Text.StringBuilder();
            header.AppendLine("<color=#00D0FF><b>DIFF SUMMARY</b></color>");
            header.AppendLine("<color=#DDDDDD>Original file:</color> " + fileOld);
            header.AppendLine("<color=#DDDDDD>Modified file:</color> " + fileNew);
            header.AppendLine("");
            header.AppendLine("<color=#6AFF9E>Added lines:</color> " + added);
            header.AppendLine("<color=#800020>Removed lines:</color> " + removed);
            header.AppendLine("<color=#FFFFFF>Unchanged lines:</color> " + unchanged);
            header.AppendLine("<color=#FFD800>Total changes:</color> " + (added + removed));
            header.AppendLine("\n────────────────────────────────────────\n");

            return header + sb.ToString();
        }

        public void OpenExternalDiff(SvnTreeElement element) => _ = ShowDiff(element.FullPath);
        public void ExecuteDiffForElement(SvnTreeElement element) => _ = ShowPreviewInUnity(element.FullPath);
    }
}