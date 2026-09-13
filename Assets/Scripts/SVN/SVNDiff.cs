using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    public class SVNDiff : SVNBase, IDisposable
    {
        private const int DiffTimeoutSeconds = 120;
        private const int MaxPreviewLines = 500;

        private static readonly Regex DiffSectionRegex = new Regex(@"@@ -(\d+),?\d* \+(\d+),?\d* @@", RegexOptions.Compiled);

        private CancellationTokenSource _operationCts;
        private int _disposed;

        public SVNDiff(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        protected override TMP_Text GetConsole() => svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _operationCts, null));
        }

        public void CancelOperation()
        {
            try { Volatile.Read(ref _operationCts)?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            return TryStart();
        }

        private void ExitProcessing() => End();

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent;
            if (console != null)
                SVNLogBridge.UpdateUIField(console, msg, "DIFF", true);
        }

        private void PostLog(string msg) => PostToMainThread(() => LogBoth(msg));

        private void SafeFireAndForget(Func<Task> operation) => _ = FireAndForget(operation);

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        // === FIX (clear): czyści dedykowany panel diffa przed nowym runem.
        // Nie dotyka LogText (główny log) — tylko DiffConsoleText i
        // CommitConsoleContent, które są zarezerwowane na podgląd diffa.
        // Bez tego kolejny diff appendował się do poprzedniego raportu.
        private void ClearDiffPanel()
        {
            PostToMainThread(() =>
            {
                try
                {
                    if (svnUI?.DiffConsoleText != null)
                        svnUI.DiffConsoleText.text = string.Empty;

                    if (svnUI?.CommitConsoleContent != null)
                        svnUI.CommitConsoleContent.text = string.Empty;
                }
                catch { }
            });
        }

        public void ExecuteDiff()
        {
            string relativePath = svnUI?.DiffTargetFileInput?.text?.Trim();
            string uiDiffToolPath = svnUI?.SettingsDiffToolPathInput?.text;
            SafeFireAndForget(() => RunDiffAsync(relativePath, openExternal: true, uiDiffToolPath));
        }

        public Task ShowDiff(string relativePath)
            => RunDiffAsync(relativePath, openExternal: true, uiDiffToolPath: null);

        public Task ShowPreviewInUnity(string relativePath)
            => RunDiffAsync(relativePath, openExternal: false, uiDiffToolPath: null);

        public void OpenExternalDiff(SvnTreeElement element)
        {
            if (element == null) return;
            string pathSnapshot = element.FullPath;
            SafeFireAndForget(() => ShowDiff(pathSnapshot));
        }

        public void ExecuteDiffForElement(SvnTreeElement element)
        {
            if (element == null) return;
            string pathSnapshot = element.FullPath;
            SafeFireAndForget(() => ShowPreviewInUnity(pathSnapshot));
        }

        public void Button_BrowseDiffFilePath()
        {
            string root = svnManager?.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                PostLog("<color=#FFAA00>Error:</color> Working Directory is not set!");
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
                PostLog("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");

            PostToMainThread(() =>
            {
                if (svnUI?.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    LogBoth($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
            });
        }

        private async Task RunDiffAsync(string relativePath, bool openExternal, string uiDiffToolPath)
        {
            if (!TryEnterProcessing())
            {
                PostLog("<color=yellow>Diff already in progress – cancelling it. Try again in a moment.</color>");
                CancelOperation();
                return;
            }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DiffTimeoutSeconds));
            Interlocked.Exchange(ref _operationCts, cts);

            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    PostLog("<color=yellow>Please select or enter a file path first.</color>");
                    return;
                }

                if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir))
                {
                    PostLog("<color=#FFAA00>Error:</color> Working Directory is not set!");
                    return;
                }

                if (!SVNShared.TryGetRelativePath(svnManager.WorkingDir, relativePath, out string safePath))
                {
                    PostLog("<color=#FFAA00>Error:</color> Invalid path or outside working directory.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(safePath))
                {
                    PostLog("<color=yellow>No file selected.</color>");
                    return;
                }

                // === FIX (clear): panel czyszczony PO walidacji ścieżki, ale
                // PRZED komunikatem "Comparing:". Dzięki temu błędne wywołania
                // (pusta ścieżka, zła ścieżka) nie kasują poprzedniego raportu.
                ClearDiffPanel();

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowDiffInternal(safePath, openExternal, cts.Token, uiDiffToolPath).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=#8B0000>Diff operation cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Exception:</color> {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _operationCts, null, cts);
                SVNShared.CancelAndDisposeLater(cts);
                ExitProcessing();
            }
        }

        private async Task ShowDiffInternal(string safePath, bool openExternal, CancellationToken token, string uiDiffToolPath)
        {
            string root = svnManager.WorkingDir;
            string fullPath = Path.Combine(root, safePath);

            if (Directory.Exists(fullPath))
            {
                PostLog("<color=yellow>Preview for directories is not supported. Select a file.</color>");
                return;
            }

            PostLog($"Comparing: <color=green>{safePath}</color>...");

            string diffContent;
            try
            {
                diffContent = await SvnRunner.RunAsync(
                    $"diff \"{SVNShared.EscapeSvnArg(safePath)}\"",
                    root, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostLog($"<color=#FF4444>SVN Error:</color> {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(diffContent))
            {
                PostLog("<color=white>No local changes detected.</color>");
                return;
            }

            if (diffContent.Contains("Cannot display: file marked as a binary type"))
            {
                PostLog("<color=#8B0000>Binary File:</color> Opening Explorer...");
                string explorerPath = fullPath.Replace("/", "\\");
                PostToMainThread(() => SVNShared.TryStartProcess(
                    new ProcessStartInfo("explorer.exe", $"/select,\"{explorerPath}\"") { UseShellExecute = true },
                    ex => LogBoth($"<color=#FFAA00>Explorer failed ({ex.GetType().Name}):</color> {ex.Message}")));
                return;
            }

            string[] lines = SplitLines(diffContent);

            (int added, int removed) = CountDiffStats(lines);
            PostLog($"<color=#00D0FF><b>Diff Summary:</b></color> <color=#6AFF9E>+{added} lines added</color>, <color=#800020>-{removed} lines removed</color>");

            if (openExternal)
            {
                string diffToolPath = ResolveDiffToolPath(uiDiffToolPath);

                if (diffToolPath.Length > 0 && File.Exists(diffToolPath))
                {
                    try
                    {
                        string fileName = Path.GetFileName(safePath);
                        string tempBasePath = Path.Combine(SVNPrefs.TemporaryCachePath, $"svn_base_{Guid.NewGuid():N}_{fileName}");

                        var (catExit, catError) = await SvnRunner.RunToFileAsync(
                            $"cat -r BASE \"{SVNShared.EscapeSvnArg(safePath)}\"",
                            root, tempBasePath, token).ConfigureAwait(false);

                        if (catExit == 0)
                        {
                            string workingCopyPath = Path.GetFullPath(fullPath);
                            bool isTortoise = diffToolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0;

                            string processArgs = isTortoise
                                ? $"/base:\"{tempBasePath}\" /mine:\"{workingCopyPath}\" " +
                                  $"/basename:\"{fileName} (BASE)\" /minename:\"{fileName} (Working)\""
                                : $"\"{tempBasePath}\" \"{workingCopyPath}\"";

                            PostToMainThread(() =>
                            {
                                if (SVNShared.TryStartProcess(new ProcessStartInfo
                                {
                                    FileName = diffToolPath,
                                    Arguments = processArgs,
                                    UseShellExecute = true
                                },
                                     ex => LogBoth($"<color=#FFAA00>Diff tool failed ({ex.GetType().Name}):</color> {ex.Message}")))
                                {
                                    LogBoth($"<color=green>[Diff] Opened in external tool:</color> {fileName}");
                                }
                            });

                            SVNShared.CleanupOldFiles(SVNPrefs.TemporaryCachePath, "svn_base_*", TimeSpan.FromHours(24));
                            return;
                        }

                        string reason = Truncate(catError, 120);
                        PostLog($"<color=yellow>Notice:</color> Newly added file (svn cat exit {catExit}{(reason.Length > 0 ? ": " + reason : string.Empty)}). Falling back to text preview.");
                    }
                    catch (Exception ex)
                    {
                        PostLog($"<color=#FFAA00>External tool error:</color> {ex.Message}. Falling back to text.");
                    }
                }
            }

            string formatted = FormatDiffForUnity(diffContent, lines, MaxPreviewLines);
            string previewSnapshot = formatted;

            PostToMainThread(() =>
            {
                var targetField = svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent ?? svnUI?.LogText;
                if (targetField != null)
                {
                    targetField.text = previewSnapshot;
                    if (targetField.GetComponentInParent<UnityEngine.UI.ScrollRect>() is { } scrollRect)
                        scrollRect.verticalNormalizedPosition = 1f;
                }
            });

            if (openExternal)
            {
                string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempDiffPath = Path.Combine(SVNPrefs.TemporaryCachePath, $"svn_diff_preview_{uniqueId}.diff");
                string enrichedContent = FormatDiffForExternalEditor(diffContent, lines);
                await File.WriteAllTextAsync(tempDiffPath, enrichedContent, token).ConfigureAwait(false);

                SVNShared.CleanupOldFiles(SVNPrefs.TemporaryCachePath, "svn_diff_preview*.diff", TimeSpan.FromHours(24));

                PostToMainThread(() =>
                {
                    if (!SVNShared.TryStartProcess(new ProcessStartInfo(tempDiffPath) { UseShellExecute = true },
                        ex => LogBoth($"<color=#8B0000>Could not open the .diff file ({ex.GetType().Name}). Preview remains in console.</color>")))
                    {
                        LogBoth("<color=#8B0000>Warning:</color> No default application associated with .diff? Preview remains in console.");
                    }
                });
            }
        }

        private string ResolveDiffToolPath(string uiSnapshot)
        {
            string path = svnManager?.DiffToolPath;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(uiSnapshot))
                path = uiSnapshot;
            if (string.IsNullOrWhiteSpace(path))
                path = SVNPrefs.GetString(SVNManager.KEY_DIFF_TOOL, "");

            path = path?.Trim().Trim('"');
            return path ?? "";
        }

        // ===================================================================
        //  FormatDiffForUnity — podgląd w TMP (layout z separami │)
        // ===================================================================
        private string FormatDiffForUnity(string rawDiff, string[] lines, int maxLines)
        {
            var sb = new StringBuilder(Math.Max(512, rawDiff.Length));
            int oldLine = 0, newLine = 0;
            bool hasSection = false;
            int added = 0, removed = 0, unchanged = 0;
            string fileOld = "", fileNew = "";
            bool truncated = false;
            int emitted = 0;

            const string colNum = "#FFFFFF", colRem = "#800020", colAdd = "#6AFF9E",
                         colInfo = "#FFD800", colLegend = "#7A8A9E", colSep = "#4A5A6E";
            const int wNum = 8;
            const string monoStart = "<mspace=0.6em>", monoEnd = "</mspace>";
            const string sepText = "  │  ";

            bool Emit()
            {
                if (emitted >= maxLines) { truncated = true; return false; }
                emitted++;
                return true;
            }

            string BuildPrefix(string oldText, string newText, string opText, string opColor)
            {
                return monoStart
                    + "<color=" + colNum + ">" + oldText.PadLeft(wNum) + "</color>"
                    + "<color=" + colSep + ">" + sepText + "</color>"
                    + "<color=" + colNum + ">" + newText.PadLeft(wNum) + "</color>"
                    + "<color=" + colSep + ">" + sepText + "</color>"
                    + "<color=" + opColor + ">" + opText.PadRight(3) + "</color>"
                    + "<color=" + colSep + ">" + sepText + "</color>"
                    + monoEnd;
            }

            string columnLegend = BuildPrefix("OLD", "NEW", "OP", colLegend)
                + $"<color={colLegend}>CONTENT</color>";

            foreach (string raw in lines)
            {
                if (!hasSection)
                {
                    if (raw.StartsWith("--- ")) { fileOld = raw.Substring(4).Trim(); continue; }
                    if (raw.StartsWith("+++ ")) { fileNew = raw.Substring(4).Trim(); continue; }
                }

                string line = raw.Replace("\t", "    ");
                if (line.IndexOf('<') >= 0 || line.IndexOf('>') >= 0)
                    line = line.Replace("<", "<noparse><</noparse>").Replace(">", "<noparse>></noparse>");

                if (line.StartsWith("@@"))
                {
                    var match = DiffSectionRegex.Match(line);
                    if (match.Success) { oldLine = int.Parse(match.Groups[1].Value); newLine = int.Parse(match.Groups[2].Value); hasSection = true; }
                    if (Emit()) sb.AppendLine($"\n<color={colInfo}>──────── SECTION (line {newLine}) ────────</color>");
                    continue;
                }

                if (line.StartsWith("\\"))
                {
                    if (Emit())
                        sb.Append(BuildPrefix("", "", "", colInfo))
                          .Append("<color=").Append(colInfo).Append(">\\ No newline at end of file</color>")
                          .AppendLine();
                    continue;
                }

                if (!hasSection) continue;

                if (line.StartsWith("-"))
                {
                    removed++;
                    if (Emit())
                        sb.Append(BuildPrefix(oldLine.ToString(), "", "-", colRem))
                          .Append("<color=").Append(colRem).Append('>').Append(line.Substring(1)).AppendLine("</color>");
                    oldLine++;
                }
                else if (line.StartsWith("+"))
                {
                    added++;
                    if (Emit())
                        sb.Append(BuildPrefix("", newLine.ToString(), "+", colAdd))
                          .Append("<color=").Append(colAdd).Append('>').Append(line.Substring(1)).AppendLine("</color>");
                    newLine++;
                }
                else
                {
                    unchanged++;
                    if (Emit())
                        sb.Append(BuildPrefix(oldLine.ToString(), newLine.ToString(), "", colNum))
                          .AppendLine(line.Length > 0 ? line.Substring(1) : "");
                    oldLine++;
                    newLine++;
                }
            }

            if (truncated)
                sb.AppendLine($"\n<color=#FFAA00>... Diff truncated in preview ({maxLines} lines).</color>");

            var header = new StringBuilder(512);
            header.AppendLine("<color=#00D0FF><b>DIFF SUMMARY</b></color>")
                .AppendLine("<color=#DDDDDD>Original file:</color> " + EscapeForTMP(fileOld))
                .AppendLine("<color=#DDDDDD>Modified file:</color> " + EscapeForTMP(fileNew))
                .AppendLine()
                .AppendLine("<color=#6AFF9E>Added lines:</color> " + added)
                .AppendLine("<color=#800020>Removed lines:</color> " + removed)
                .AppendLine("<color=#FFFFFF>Unchanged lines:</color> " + unchanged)
                .AppendLine("<color=#FFD800>Total changes:</color> " + (added + removed))
                .AppendLine("\n────────────────────────────────────────")
                .AppendLine(columnLegend)
                .AppendLine("────────────────────────────────────────\n");

            return header.ToString() + sb.ToString();
        }

        // ===================================================================
        //  FormatDiffForExternalEditor — raport .diff
        // ===================================================================
        private string FormatDiffForExternalEditor(string rawDiff, string[] lines)
        {
            var sb = new StringBuilder(rawDiff.Length * 2);
            int oldLine = 0, newLine = 0;
            string indexFile = "Unknown", rev1 = "Original", rev2 = "Working copy";
            int added = 0, removed = 0;

            string ExtractVersion(string line) { int idx = line.LastIndexOf('('); return idx >= 0 ? line.Substring(idx).Trim() : line; }

            bool inHunk = false;
            foreach (string raw in lines)
            {
                if (raw.Length == 0) continue;
                if (raw.StartsWith("Index: ")) { indexFile = raw.Substring(7); inHunk = false; continue; }
                if (raw.StartsWith("===")) continue;
                if (raw.StartsWith("@@") || raw.StartsWith("##")) { inHunk = true; continue; }
                if (raw.StartsWith(@"\ No newline")) continue;
                if (!inHunk)
                {
                    if (raw.StartsWith("--- ")) { rev1 = ExtractVersion(raw); continue; }
                    if (raw.StartsWith("+++ ")) { rev2 = ExtractVersion(raw); continue; }
                }
                char c = raw[0];
                if (c == '+') added++;
                else if (c == '-') removed++;
            }

            int boxWidth = 72; string hLine = new string('═', boxWidth);
            string PadCenter(string text, int width) { if (text.Length >= width) return text.Substring(0, width); int leftPad = (width - text.Length) / 2; return text.PadLeft(leftPad + text.Length).PadRight(width); }

            sb.AppendLine($"╔{hLine}╗");
            sb.AppendLine($"║{PadCenter("SVN FILE COMPARISON REPORT", boxWidth)}║");
            sb.AppendLine($"╠{hLine}╣");
            sb.AppendLine($"║ FILE : {Truncate(indexFile, boxWidth - 8).PadRight(boxWidth - 8)}║");
            sb.AppendLine($"║ BASE : {Truncate(rev1, boxWidth - 8).PadRight(boxWidth - 8)}║");
            sb.AppendLine($"║ MOD  : {Truncate(rev2, boxWidth - 8).PadRight(boxWidth - 8)}║");
            sb.AppendLine($"╠{hLine}╣");
            string statsText = $"+{added} additions | -{removed} deletions";
            sb.AppendLine($"║ STATS: {statsText.PadRight(boxWidth - 8)}║");
            sb.AppendLine($"╚{hLine}╝\n");

            string colSep = " │ ";
            string rowLine = new string('─', 7) + "─┼─" + new string('─', 7) + "─┼─" + new string('─', 5) + "─┼─" + new string('─', 60);
            string rowEdge = "┈" + rowLine + "┈";
            sb.AppendLine($"  {"OLD LINE",-7} {colSep} {"NEW LINE",-7} {colSep} {"OP",-3} {colSep} CODE CONTENT");
            sb.AppendLine(rowEdge);

            inHunk = false;
            foreach (string raw in lines)
            {
                if (raw.Length == 0) continue;
                if (raw.StartsWith("Index:")) { inHunk = false; continue; }
                if (raw.StartsWith("===")) continue;

                if (raw.StartsWith("@@"))
                {
                    inHunk = true;
                    var match = DiffSectionRegex.Match(raw);
                    if (match.Success) { int.TryParse(match.Groups[1].Value, out oldLine); int.TryParse(match.Groups[2].Value, out newLine); }
                    sb.AppendLine($"\n  ◄ BLOCK START: Changes starting near line {newLine} ►\n{rowEdge}");
                    continue;
                }

                if (raw.StartsWith(@"\ No newline"))
                {
                    sb.AppendLine($"  {"",-7} {colSep} {"",-7} {colSep} {"",-3} {colSep} {raw}");
                    continue;
                }

                if (!inHunk)
                {
                    if (raw.StartsWith("--- ")) continue;
                    if (raw.StartsWith("+++ ")) continue;
                }

                string sOld = oldLine.ToString().PadLeft(7);
                string sNew = newLine.ToString().PadLeft(7);
                if (raw.StartsWith("-")) { sb.AppendLine($"  {sOld} {colSep} {"",-7} {colSep} {"-",3} {colSep} {raw.Substring(1)}"); oldLine++; }
                else if (raw.StartsWith("+")) { sb.AppendLine($"  {"",-7} {colSep} {sNew} {colSep} {"+",3} {colSep} {raw.Substring(1)}"); newLine++; }
                else
                {
                    sb.AppendLine($"  {sOld} {colSep} {sNew} {colSep} {"",-3} {colSep} {(raw.Length > 0 ? raw.Substring(1) : "")}");
                    oldLine++;
                    newLine++;
                }
            }
            sb.AppendLine(rowEdge);
            return sb.ToString();
        }

        private static (int added, int removed) CountDiffStats(string[] lines)
        {
            int added = 0, removed = 0;
            bool inHunk = false;
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("Index:")) { inHunk = false; continue; }
                if (line.StartsWith("===")) continue;
                if (line.StartsWith("@@") || line.StartsWith("##")) { inHunk = true; continue; }
                if (line.StartsWith(@"\ No newline")) continue;
                if (!inHunk) continue;
                char c = line[0];
                if (c == '+') added++;
                else if (c == '-') removed++;
            }
            return (added, removed);
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Trim();
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen - 3) + "...";
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string EscapeForTMP(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('<') < 0 && s.IndexOf('>') < 0) return s;
            return s.Replace("<", "<noparse><</noparse>").Replace(">", "<noparse>></noparse>");
        }
    }
}