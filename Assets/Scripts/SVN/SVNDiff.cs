using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    // === FIX: IDisposable — manager dysponuje tylko moduły IDisposable;
    // _operationCts przeciekał przy zamknięciu aplikacji.
    public class SVNDiff : SVNBase, IDisposable
    {
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;
        private static readonly Regex DiffSectionRegex = new Regex(@"@@ -(\d+),?\d* \+(\d+),?\d* @@", RegexOptions.Compiled);

        private CancellationTokenSource _operationCts;
        private int _disposed;

        public SVNDiff(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            var localCts = Interlocked.Exchange(ref _operationCts, null);
            if (localCts != null)
            {
                try { localCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
            }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI?.DiffConsoleText ?? svnUI?.CommitConsoleContent;
            if (console != null)
                SVNLogBridge.UpdateUIField(console, msg, "DIFF", true);
        }

        private void PostLog(string msg)
        {
            UnityMainThreadDispatcher.Enqueue(() => LogBoth(msg));
        }

        private void PostUI(Action action)
        {
            UnityMainThreadDispatcher.Enqueue(action);
        }

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = FireAndForget(operation);
        }

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) { PostLog("<color=orange>Operation cancelled.</color>"); }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        // === FIX K2: delayed dispose starego CTS (natychmiastowy Cancel+Dispose
        // rzucał ObjectDisposedException w umierającym diffie → łapany jako
        // "Exception" → fałszywy fallback tekstowy zamiast toola).
        private CancellationToken RefreshToken()
        {
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _operationCts, newCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }
            return newCts.Token;
        }

        public void CancelOperation()
        {
            try
            {
                var cts = Volatile.Read(ref _operationCts);
                cts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        private bool TryGetRelativePath(string root, string input, out string safeRelative)
        {
            safeRelative = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(input)) return false;
            try
            {
                // Path.GetFullPath zablokuje nielegalne znaki (np. |, ?, *) rzucając wyjątkiem,
                // co uchroni nas przed command injection.
                string fullRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
                string fullInput = Path.GetFullPath(Path.Combine(fullRoot, input)).Replace('\\', '/');

                if (fullInput.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    safeRelative = "";
                    return true;
                }

                if (fullInput.StartsWith(fullRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    safeRelative = fullInput.Substring(fullRoot.Length + 1);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // Button_BrowseDiffFilePath — wywoływane z UI (main thread) — bez zmian,
        // tylko fallback LogBoth przez dispatcher dla pewności.
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

            PostUI(() =>
            {
                if (svnUI?.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    LogBoth($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
            });
        }

        // === FIX Ś1: odczyt pola UI na main thread (tu jesteśmy — przycisk),
        // snapshot przekazany do async rdzenia.
        public void ExecuteDiff()
        {
            string relativePath = svnUI?.DiffTargetFileInput?.text?.Trim();
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;

                try
                {
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        PostLog("<color=yellow>Please select or enter a file path first.</color>");
                        return;
                    }

                    await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                    var token = RefreshToken();
                    await ShowDiffInternal(relativePath, openExternal: true, token).ConfigureAwait(false);
                }
                finally { ExitProcessing(); }
            });
        }

        public async Task ShowDiff(string relativePath)
        {
            var token = RefreshToken();
            await ShowDiffInternal(relativePath, openExternal: true, token).ConfigureAwait(false);
        }

        public async Task ShowPreviewInUnity(string relativePath)
        {
            var token = RefreshToken();
            await ShowDiffInternal(relativePath, openExternal: false, token).ConfigureAwait(false);
        }

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

        private async Task ShowDiffInternal(string relativePath, bool openExternal, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir))
                {
                    PostLog("<color=#FFAA00>Error:</color> Working Directory is not set!");
                    return;
                }

                if (!TryGetRelativePath(svnManager.WorkingDir, relativePath, out string safePath))
                {
                    PostLog("<color=#FFAA00>Error:</color> Invalid path or outside working directory.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(safePath))
                {
                    PostLog("<color=yellow>No file selected.</color>");
                    return;
                }

                string fullPath = Path.Combine(svnManager.WorkingDir, safePath);
                if (Directory.Exists(fullPath))
                {
                    PostLog("<color=yellow>Preview for directories is not supported. Select a file.</color>");
                    return;
                }

                PostLog($"Comparing: <color=green>{safePath}</color>...");

                string diffContent = await SvnRunner.RunAsync(
                    $"diff \"{EscapeSvnArg(safePath)}\"",
                    svnManager.WorkingDir,
                    false,
                    token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    PostLog("<color=white>No local changes detected.</color>");
                    return;
                }

                if (diffContent.Contains("Cannot display: file marked as a binary type"))
                {
                    PostLog("<color=orange>Binary File:</color> Opening Explorer...");
                    string explorerPath = fullPath.Replace("/", "\\");
                    PostUI(() =>
                    {
                        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{explorerPath}\"") { UseShellExecute = true });
                    });
                    return;
                }

                (int added, int removed) = CountDiffStats(diffContent);
                PostLog($"<color=#00D0FF><b>Diff Summary:</b></color> <color=#6AFF9E>+{added} lines added</color>, <color=#800020>-{removed} lines removed</color>");

                if (openExternal)
                {
                    string diffToolPath = GetDiffToolPath();

                    if (!string.IsNullOrEmpty(diffToolPath) && File.Exists(diffToolPath))
                    {
                        PostLog("<color=yellow>Launching external visual diff tool...</color>");
                        try
                        {
                            string fileName = Path.GetFileName(safePath);
                            string tempBasePath = Path.Combine(SVNPrefs.TemporaryCachePath, $"svn_base_{Guid.NewGuid():N}_{fileName}");

                            // === FIX K1: BASE przez strumień BAJTÓW (RunToFileAsync).
                            // Stara wersja: 'svn cat' → string → WriteAllText — kodowanie
                            // dekodowane/re-enkodowane i końce linii przepisane →
                            // TortoiseMerge pokazywał fałszywe różnice KAŻDEJ linii
                            // pliku CRLF (i korumpowałby binaria, gdyby tu trafiły).
                            var (catExit, catError) = await SvnRunner.RunToFileAsync(
                                $"cat \"{EscapeSvnArg(safePath)}\"",
                                svnManager.WorkingDir,
                                tempBasePath,
                                token).ConfigureAwait(false);

                            if (catExit == 0 && new FileInfo(tempBasePath).Length > 0)
                            {
                                string workingCopyPath = Path.GetFullPath(fullPath);

                                string processArgs;

                                if (diffToolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    processArgs = $"/base:\"{tempBasePath}\" /mine:\"{workingCopyPath}\"";
                                }
                                else
                                {
                                    processArgs = $"\"{tempBasePath}\" \"{workingCopyPath}\"";
                                }

                                PostUI(() =>
                                {
                                    using var process = Process.Start(new ProcessStartInfo
                                    {
                                        FileName = diffToolPath,
                                        Arguments = processArgs,
                                        UseShellExecute = true
                                    });
                                    if (process == null)
                                    {
                                        PostLog("<color=orange>Warning:</color> Could not launch the external diff tool.");
                                    }
                                });

                                CleanupOldDiffFiles();
                                return;
                            }
                            else
                            {
                                // cat != 0 (np. plik nowy — brak BASE) lub pusty
                                if (catExit != 0)
                                    PostLog("<color=yellow>Notice:</color> Newly added file (no BASE). Falling back to text preview.");
                                // pusty BASE = plik początkowo pusty — tool pokaże vs. pustego; lecimy z tool-em
                                else
                                {
                                    PostUI(() =>
                                    {
                                        using var process = Process.Start(new ProcessStartInfo
                                        {
                                            FileName = diffToolPath,
                                            Arguments = diffToolPath.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0
                                                ? $"/base:\"{tempBasePath}\" /mine:\"{Path.GetFullPath(fullPath)}\""
                                                : $"\"{tempBasePath}\" \"{Path.GetFullPath(fullPath)}\"",
                                            UseShellExecute = true
                                        });
                                    });
                                    CleanupOldDiffFiles();
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            PostLog($"<color=#FFAA00>External tool error:</color> {ex.Message}. Falling back to text.");
                        }
                    }
                }

                string formatted = FormatDiffForUnity(diffContent);

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

                if (openExternal)
                {
                    string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    string tempDiffPath = Path.Combine(SVNPrefs.TemporaryCachePath, $"svn_diff_preview_{uniqueId}.diff");
                    string enrichedContent = FormatDiffForExternalEditor(diffContent);
                    await File.WriteAllTextAsync(tempDiffPath, enrichedContent, token).ConfigureAwait(false);

                    CleanupOldDiffFiles();   // === FIX: sprzątanie też po ścieżce .diff-preview

                    PostUI(() =>
                    {
                        using var process = Process.Start(new ProcessStartInfo(tempDiffPath) { UseShellExecute = true });
                        if (process == null)
                        {
                            PostLog("<color=orange>Warning:</color> Could not open the diff file. No default application associated with .diff.");
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=orange>Diff operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Exception:</color> {ex.Message}");
            }
        }

        private void CleanupOldDiffFiles()
        {
            try
            {
                string cache = SVNPrefs.TemporaryCachePath;
                if (!Directory.Exists(cache)) return;

                string[] patterns = { "svn_diff_preview*.diff", "svn_base_*" };

                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.EnumerateFiles(cache, pattern))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24))
                                File.Delete(file);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // === FIX: PlayerPrefs przez SVNPrefs (metoda wołana po awaitach → pula).
        private string GetDiffToolPath()
        {
            string path = svnManager?.DiffToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = svnUI?.SettingsDiffToolPathInput?.text;
            if (string.IsNullOrWhiteSpace(path))
                path = SVNPrefs.GetString(SVNManager.KEY_DIFF_TOOL, "");

            return path?.Trim().Trim('"');
        }

        private string FormatDiffForExternalEditor(string rawDiff)
        {
            string[] lines = SplitLines(rawDiff);
            var sb = new StringBuilder(rawDiff.Length * 2);
            int oldLine = 0, newLine = 0;
            string indexFile = "Unknown", rev1 = "Original", rev2 = "Working copy";
            int added = 0, removed = 0;

            string ExtractVersion(string line) { int idx = line.LastIndexOf('('); return idx >= 0 ? line.Substring(idx).Trim() : line; }

            foreach (string raw in lines)
            {
                if (raw.StartsWith("Index: ")) indexFile = raw.Substring(7);
                else if (raw.StartsWith("--- ")) rev1 = ExtractVersion(raw);
                else if (raw.StartsWith("+++ ")) rev2 = ExtractVersion(raw);
                else if (raw.StartsWith("+") && !raw.StartsWith("+++")) added++;
                else if (raw.StartsWith("-") && !raw.StartsWith("---")) removed++;
            }

            int boxWidth = 72; string hLine = new string('═', boxWidth);
            string PadCenter(string text, int width) { if (text.Length >= width) return text.Substring(0, width); int leftPad = (width - text.Length) / 2; return text.PadLeft(leftPad + text.Length).PadRight(width); }

            // === FIX kompilacji: string.IsNullOrEmpty to metoda STATYCZNA —
            // wywołanie przez klasę, nie na instancji (było: text.IsNullOrEmpty()).
            string Truncate(string text, int maxLen) { if (string.IsNullOrEmpty(text)) return ""; if (text.Length <= maxLen) return text; return text.Substring(0, maxLen - 3) + "..."; }

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

            foreach (string raw in lines)
            {
                if (raw.StartsWith("Index:") || raw.StartsWith("===") || raw.StartsWith("---") || raw.StartsWith("+++")) continue;
                if (raw.StartsWith(@"\ No newline")) { sb.AppendLine($"  {"",-7} {colSep} {"",-7} {colSep} {"\\",-3} {colSep} \\ No newline at end of file"); continue; }
                if (raw.StartsWith("@@"))
                {
                    var match = DiffSectionRegex.Match(raw);
                    if (match.Success) { int.TryParse(match.Groups[1].Value, out oldLine); int.TryParse(match.Groups[2].Value, out newLine); }
                    sb.AppendLine($"\n  ◄ BLOCK START: Changes starting near line {newLine} ►\n{rowEdge}");
                    continue;
                }
                string sOld = oldLine.ToString().PadLeft(7); string sNew = newLine.ToString().PadLeft(7);
                if (raw.StartsWith("-")) { sb.AppendLine($"  {sOld} {colSep} {"",-7} {colSep} {"-",3} {colSep} {raw.Substring(1)}"); oldLine++; }
                else if (raw.StartsWith("+")) { sb.AppendLine($"  {"",-7} {colSep} {sNew} {colSep} {"+",3} {colSep} {raw.Substring(1)}"); newLine++; }
                else { sb.AppendLine($"  {sOld} {colSep} {sNew} {colSep} {"",-3} {colSep} {(raw.Length > 0 ? raw.Substring(1) : "")}"); if (raw.Length > 0) { oldLine++; newLine++; } }
            }
            sb.AppendLine(rowEdge);
            return sb.ToString();
        }

        private string FormatDiffForUnity(string rawDiff)
        {
            string[] lines = SplitLines(rawDiff);
            var sb = new StringBuilder(rawDiff.Length * 2);
            int oldLine = 0, newLine = 0; bool hasSection = false; int added = 0, removed = 0, unchanged = 0;
            string fileOld = "", fileNew = "";
            const string colNum = "#FFFFFF", colRem = "#800020", colAdd = "#6AFF9E", colInfo = "#FFD800"; const int wNum = 8;
            const string monoStart = "<mspace=0.6em>", monoEnd = "</mspace>", gap = "  ";

            foreach (string raw in lines) { if (raw.StartsWith("--- ")) fileOld = raw.Substring(4).Trim(); if (raw.StartsWith("+++ ")) fileNew = raw.Substring(4).Trim(); }

            foreach (string raw in lines)
            {
                string line = raw.Replace("\t", "    ").Replace("<", "<noparse><</noparse>").Replace(">", "<noparse>></noparse>");
                if (line.StartsWith("@@")) { var match = DiffSectionRegex.Match(line); if (match.Success) { oldLine = int.Parse(match.Groups[1].Value); newLine = int.Parse(match.Groups[2].Value); hasSection = true; } sb.AppendLine($"\n<color={colInfo}>──────── SECTION (line {newLine}) ────────</color>"); continue; }
                if (!hasSection) continue;
                string sOld = oldLine.ToString().PadLeft(wNum); string sNew = newLine.ToString().PadLeft(wNum);
                if (line.StartsWith("-")) { removed++; sb.Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sOld).Append("</color>").Append(monoEnd).Append(gap).Append(monoStart).Append(new string(' ', wNum)).Append(monoEnd).Append(gap).Append("<color=").Append(colRem).Append(">-</color>").Append(gap).Append("<color=").Append(colRem).Append('>').Append(line.Substring(1)).AppendLine("</color>"); oldLine++; }
                else if (line.StartsWith("+")) { added++; sb.Append(monoStart).Append(new string(' ', wNum)).Append(monoEnd).Append(gap).Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sNew).Append("</color>").Append(monoEnd).Append(gap).Append("<color=").Append(colAdd).Append(">+</color>").Append(gap).Append("<color=").Append(colAdd).Append('>').Append(line.Substring(1)).AppendLine("</color>"); newLine++; }
                else { unchanged++; sb.Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sOld).Append("</color>").Append(monoEnd).Append(gap).Append(monoStart).Append("<color=").Append(colNum).Append('>').Append(sNew).Append("</color>").Append(monoEnd).Append(gap).Append("   ").Append(gap).AppendLine(line.Length > 0 ? line.Substring(1) : ""); oldLine++; newLine++; }
            }

            var header = new StringBuilder(512);
            header.AppendLine("<color=#00D0FF><b>DIFF SUMMARY</b></color>").AppendLine("<color=#DDDDDD>Original file:</color> " + fileOld).AppendLine("<color=#DDDDDD>Modified file:</color> " + fileNew).AppendLine().AppendLine("<color=#6AFF9E>Added lines:</color> " + added).AppendLine("<color=#800020>Removed lines:</color> " + removed).AppendLine("<color=#FFFFFF>Unchanged lines:</color> " + unchanged).AppendLine("<color=#FFD800>Total changes:</color> " + (added + removed)).AppendLine("\n────────────────────────────────────────\n");
            return header.ToString() + sb.ToString();
        }

        private static (int added, int removed) CountDiffStats(string diffContent)
        {
            if (string.IsNullOrEmpty(diffContent)) return (0, 0);
            int added = 0, removed = 0;
            foreach (string line in SplitLines(diffContent)) { if (line.Length == 0) continue; char c = line[0]; if (c == '+' && !line.StartsWith("+++")) added++; else if (c == '-' && !line.StartsWith("---")) removed++; }
            return (added, removed);
        }

        private static string[] SplitLines(string text) { if (string.IsNullOrEmpty(text)) return Array.Empty<string>(); return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }

        private static string EscapeSvnArg(string arg) { if (string.IsNullOrWhiteSpace(arg)) return arg; return arg.Replace("\"", "\\\""); }
    }
}