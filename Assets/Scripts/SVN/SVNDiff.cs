using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    public class SVNDiff : SVNBase
    {
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNDiff(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        #region Logging & Thread Safety

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent;
            if (console != null)
                SVNLogBridge.UpdateUIField(console, msg, "DIFF", true);
        }

        /// <summary>
        /// Wykonuje log na głównym wątku Unity (bezpieczne dla UI).
        /// </summary>
        private void PostLog(string msg)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => LogBoth(msg), null);
            else
                LogBoth(msg);
        }

        /// <summary>
        /// Wykonuje akcję modyfikującą UI na głównym wątku Unity.
        /// </summary>
        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

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

        /// <summary>
        /// Bezpieczny fire-and-forget bez Task.Run – zachowuje SynchronizationContext.
        /// </summary>
        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = FireAndForget(operation);
        }

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        #endregion

        #region Public API

        public void Button_BrowseDiffFilePath()
        {
            string root = svnManager?.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                LogBoth("<color=#FFAA00>Error:</color> Working Directory is not set!");
                return;
            }

            var extensions = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                return;

            string selectedPath = paths[0].Replace('\\', '/');
            string normalizedRoot = root.Replace('\\', '/').TrimEnd('/');

            if (selectedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                selectedPath = selectedPath.Substring(normalizedRoot.Length + 1);
            else if (selectedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                selectedPath = "";
            else
                LogBoth("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");

            if (svnUI?.DiffTargetFileInput != null)
            {
                svnUI.DiffTargetFileInput.text = selectedPath;
                LogBoth($"<color=green>Diff:</color> Selected file: {selectedPath}");
            }
        }

        public void ExecuteDiff()
        {
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;

                try
                {
                    string relativePath = svnUI?.DiffTargetFileInput?.text?.Trim();
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        PostLog("<color=yellow>Please select or enter a file path first.</color>");
                        return;
                    }

                    await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                    await ShowDiffInternal(relativePath, openExternal: true).ConfigureAwait(false);
                }
                finally { ExitProcessing(); }
            });
        }

        public async Task ShowDiff(string relativePath)
        {
            await ShowDiffInternal(relativePath, openExternal: true).ConfigureAwait(false);
        }

        public async Task ShowPreviewInUnity(string relativePath)
        {
            await ShowDiffInternal(relativePath, openExternal: false).ConfigureAwait(false);
        }

        public void OpenExternalDiff(SvnTreeElement element)
        {
            if (element == null) return;
            SafeFireAndForget(() => ShowDiff(element.FullPath));
        }

        public void ExecuteDiffForElement(SvnTreeElement element)
        {
            if (element == null) return;
            SafeFireAndForget(() => ShowPreviewInUnity(element.FullPath));
        }

        #endregion

        #region Core Diff Logic

        private async Task ShowDiffInternal(string relativePath, bool openExternal)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir))
                {
                    PostLog("<color=#FFAA00>Error:</color> Working Directory is not set!");
                    return;
                }

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    PostLog("<color=yellow>No file selected.</color>");
                    return;
                }

                string fullPath = Path.Combine(svnManager.WorkingDir, relativePath);
                if (Directory.Exists(fullPath))
                {
                    PostLog("<color=yellow>Preview for directories is not supported. Select a file.</color>");
                    return;
                }

                PostLog($"Comparing: <color=green>{relativePath}</color>...");

                // ─── Operacje SVN na wątku tła ───
                string diffContent = await SvnRunner.RunAsync(
                    $"diff \"{EscapeSvnArg(relativePath)}\"",
                    svnManager.WorkingDir,
                    false,
                    CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    PostLog("<color=white>No local changes detected.</color>");
                    return;
                }

                if (diffContent.Contains("Cannot display: file marked as a binary type"))
                {
                    PostLog("<color=orange>Binary File:</color> Opening Explorer...");
                    string explorerPath = Path.Combine(svnManager.WorkingDir, relativePath).Replace("/", "\\");

                    PostUI(() =>
                    {
                        using (Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{explorerPath}\"") { UseShellExecute = true })) { }
                    });
                    return;
                }

                // ─── Przetwarzanie danych (bez UI) ───
                string formatted = FormatDiffForUnity(diffContent);
                (int added, int removed) = CountDiffStats(diffContent);

                if (openExternal)
                {
                    string[] previewLines = SplitLines(formatted);
                    if (previewLines.Length > 500)
                    {
                        var sb = new StringBuilder(20000);
                        for (int i = 0; i < 500; i++) sb.AppendLine(previewLines[i]);
                        sb.AppendLine("\n<color=#FFAA00>... Diff truncated. Full diff opened in external editor.</color>");
                        formatted = sb.ToString();
                    }
                }

                // ─── Aktualizacja UI na głównym wątku ───
                PostUI(() =>
                {
                    var targetField = svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent ?? svnUI?.LogText;
                    if (targetField != null)
                    {
                        targetField.text = formatted;
                        if (targetField.GetComponentInParent<UnityEngine.UI.ScrollRect>() is { } scrollRect)
                            scrollRect.verticalNormalizedPosition = 1f;
                    }
                });

                PostLog($"<color=#00D0FF><b>Diff Summary:</b></color> <color=#6AFF9E>+{added} lines added</color>, <color=#800020>-{removed} lines removed</color>");

                // ─── Zewnętrzny edytor (opcjonalnie) ───
                if (openExternal)
                {
                    string editorPath = GetMergeToolPath();
                    if (string.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
                    {
                        PostLog($"<color=#FFAA00>Error:</color> Invalid Diff Tool path! (Found: '{editorPath}')");
                        return;
                    }

                    string tempDiffPath = Path.Combine(Application.temporaryCachePath, "svn_diff_preview.diff");
                    string enrichedContent = FormatDiffForExternalEditor(diffContent);
                    await File.WriteAllTextAsync(tempDiffPath, enrichedContent).ConfigureAwait(false);

                    PostUI(() =>
                    {
                        using (Process.Start(new ProcessStartInfo(editorPath, $"\"{tempDiffPath}\"") { UseShellExecute = true })) { }
                    });
                }
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Exception:</color> {ex.Message}");
            }
        }

        #endregion

        #region Formatting

        private string FormatDiffForExternalEditor(string rawDiff)
        {
            string[] lines = SplitLines(rawDiff);
            var sb = new StringBuilder(rawDiff.Length + lines.Length * 20);

            int oldLine = 0;
            int newLine = 0;

            foreach (string raw in lines)
            {
                if (raw.StartsWith("@@"))
                {
                    int space1 = raw.IndexOf(' ');
                    int space2 = raw.IndexOf(' ', space1 + 1);
                    if (space1 > 0 && space2 > space1)
                    {
                        int endOld = raw.IndexOf(',', space1);
                        if (endOld < 0) endOld = space2;
                        if (int.TryParse(raw.AsSpan(space1 + 2, endOld - space1 - 2), out int o)) oldLine = o;

                        int endNew = raw.IndexOf(',', space2);
                        if (endNew < 0) endNew = raw.IndexOf(" @@", space2);
                        if (endNew < 0) endNew = raw.Length - 3;
                        if (int.TryParse(raw.AsSpan(space2 + 2, endNew - space2 - 2), out int n)) newLine = n;
                    }

                    sb.AppendLine();
                    sb.Append("[ SEKCJA: Linia ").Append(newLine).AppendLine(" ]");
                    sb.AppendLine(raw);
                    continue;
                }

                if (raw.StartsWith("-"))
                {
                    sb.Append(oldLine.ToString().PadLeft(5)).Append(" |       | ").AppendLine(raw);
                    oldLine++;
                }
                else if (raw.StartsWith("+"))
                {
                    sb.Append("      | ").Append(newLine.ToString().PadLeft(5)).Append(" | ").AppendLine(raw);
                    newLine++;
                }
                else
                {
                    sb.Append(oldLine.ToString().PadLeft(5)).Append(" | ")
                      .Append(newLine.ToString().PadLeft(5)).Append(" | ").AppendLine(raw);
                    oldLine++;
                    newLine++;
                }
            }

            return sb.ToString();
        }

        private string FormatDiffForUnity(string rawDiff)
        {
            string[] lines = SplitLines(rawDiff);
            var sb = new StringBuilder(rawDiff.Length * 2);

            int oldLine = 0, newLine = 0;
            bool hasSection = false;
            int added = 0, removed = 0, unchanged = 0;
            string fileOld = "", fileNew = "";

            const string colNum = "#FFFFFF";
            const string colRem = "#800020";
            const string colAdd = "#6AFF9E";
            const string colInfo = "#FFD800";
            const int wNum = 8;
            const string monoStart = "<mspace=0.6em>";
            const string monoEnd = "</mspace>";
            const string gap = "  ";

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
                    sb.Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sOld)
                      .Append("</color>").Append(monoEnd).Append(gap).Append(monoStart)
                      .Append(new string(' ', wNum)).Append(monoEnd).Append(gap)
                      .Append("<color=").Append(colRem).Append(">-</color>").Append(gap)
                      .Append("<color=").Append(colRem).Append('>').Append(line.Substring(1))
                      .AppendLine("</color>");
                    oldLine++;
                }
                else if (line.StartsWith("+"))
                {
                    added++;
                    sb.Append(monoStart).Append(new string(' ', wNum)).Append(monoEnd).Append(gap)
                      .Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sNew)
                      .Append("</color>").Append(monoEnd).Append(gap)
                      .Append("<color=").Append(colAdd).Append(">+</color>").Append(gap)
                      .Append("<color=").Append(colAdd).Append('>').Append(line.Substring(1))
                      .AppendLine("</color>");
                    newLine++;
                }
                else
                {
                    unchanged++;
                    sb.Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sOld)
                      .Append("</color>").Append(monoEnd).Append(gap).Append(monoStart)
                      .Append("<color=").Append(colNum).Append('>').Append(sNew)
                      .Append("</color>").Append(monoEnd).Append(gap).Append("   ").Append(gap)
                      .AppendLine(line);
                    oldLine++;
                    newLine++;
                }
            }

            var header = new StringBuilder(512);
            header.AppendLine("<color=#00D0FF><b>DIFF SUMMARY</b></color>");
            header.AppendLine("<color=#DDDDDD>Original file:</color> " + fileOld);
            header.AppendLine("<color=#DDDDDD>Modified file:</color> " + fileNew);
            header.AppendLine();
            header.AppendLine("<color=#6AFF9E>Added lines:</color> " + added);
            header.AppendLine("<color=#800020>Removed lines:</color> " + removed);
            header.AppendLine("<color=#FFFFFF>Unchanged lines:</color> " + unchanged);
            header.AppendLine("<color=#FFD800>Total changes:</color> " + (added + removed));
            header.AppendLine("\n────────────────────────────────────────\n");

            return header.ToString() + sb.ToString();
        }

        #endregion

        #region Stats & Helpers

        private static (int added, int removed) CountDiffStats(string diffContent)
        {
            if (string.IsNullOrEmpty(diffContent)) return (0, 0);
            int added = 0, removed = 0;
            foreach (string line in SplitLines(diffContent))
            {
                if (line.Length == 0) continue;
                char c = line[0];
                if (c == '+' && !line.StartsWith("+++")) added++;
                else if (c == '-' && !line.StartsWith("---")) removed++;
            }
            return (added, removed);
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

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