using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBlame : SVNBase, IDisposable
    {
        private const int BlameTimeoutSeconds = 60;

        // Limit dotyczy WYŁĄCZNIE podglądu UI — raport do pliku zawiera
        // WSZYSTKIE linie.
        private const int MaxPreviewLines = 500;

        private CancellationTokenSource _cts;
        private int _disposed;

        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // Bazowe LogInfo/LogSuccess trafiają do konsoli blame.
        protected override TMP_Text GetConsole() => svnUI?.BlameConsoleText;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _cts, null));
        }

        public void Cancel()
        {
            try { Volatile.Read(ref _cts)?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Flagę przetwarzania dziedziczymy z SVNBase (TryStart/End) — wcześniej
        // lokalny duplikat _processingFlag synchronizowany ręcznie.
        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            return TryStart();
        }

        private void ExitProcessing() => End();

        // Dispatch: odziedziczone PostToMainThread (jednolity mechanizm).
        private void PostLog(string msg) => PostToMainThread(() => LogBoth(msg));

        private void SafeFireAndForget(Func<Task> operation) => _ = FireAndForget(operation);

        // ODE loguje rdzeń — tu tylko safety-net bez dublowania komunikatów.
        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI?.BlameConsoleText != null)
                SVNLogBridge.UpdateUIField(svnUI.BlameConsoleText, msg, "BLAME", true);
        }

        // Snapshot WSZYSTKICH pól UI na main threadzie (ścieżka + fallback
        // toola — wcześniej czytane po awaitach z puli wątków).
        public void ExecuteBlame()
        {
            string relativePath = svnUI?.BlameTargetFileInput?.text?.Trim();
            string uiTextEditorPath = svnUI?.SettingsMergeToolPathInput?.text;
            SafeFireAndForget(() => RunBlameAsync(relativePath, forceMainConsole: false, uiTextEditorPath));
        }

        // ShowBlame przechodzi przez guard + rejestruje token w _cts (Cancel
        // i Dispose działają; brak równoległych blame; sprawdzany _disposed).
        public Task ShowBlame(string relativePath, CancellationToken token = default)
            => RunBlameAsync(relativePath, forceMainConsole: false, uiTextEditorPath: null, externalToken: token);

        public Task ShowBlameInExternalEditor(string relativePath)
            => RunBlameAsync(relativePath, forceMainConsole: false, uiTextEditorPath: null);

        public Task ShowBlameInMainConsole(string relativePath)
            => RunBlameAsync(relativePath, forceMainConsole: true, uiTextEditorPath: null);

        // JEDEN rdzeń: guard → walidacja (też dla ExecuteBlame — wcześniej jej
        // NIE było na tej ścieżce!) → containment → praca → jeden catch → finally.
        private async Task RunBlameAsync(string relativePath, bool forceMainConsole, string uiTextEditorPath, CancellationToken externalToken = default)
        {
            if (!TryEnterProcessing())
            {
                PostLog("<color=yellow>Blame already in progress – cancelling it. Try again in a moment.</color>");
                Cancel();
                return;
            }

            CancellationTokenSource cts = externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(BlameTimeoutSeconds));
            Interlocked.Exchange(ref _cts, cts);

            try
            {
                if (string.IsNullOrEmpty(relativePath))
                {
                    PostLog("<color=yellow>Please select a file path first.</color>");
                    return;
                }

                bool isServer = SVNShared.IsServerPath(relativePath);
                string workDir = svnManager?.WorkingDir;

                if (!isServer)
                {
                    if (string.IsNullOrWhiteSpace(workDir))
                    {
                        PostLog("<color=#FFAA00>Error:</color> Working Directory not set!");
                        return;
                    }

                    // Containment jak SVNDiff — zakorzeniona ścieżka ("C:\...")
                    // przechodziła przez Path.Combine i wychodziła poza WC;
                    // blokada ".." i znaków kontrolnych pokryta w całości.
                    if (!SVNShared.TryGetRelativePath(workDir, relativePath, out string safeRelative))
                    {
                        PostLog("<color=#FFAA00>Invalid path or outside working directory.</color>");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(safeRelative))
                    {
                        PostLog("<color=yellow>No file selected.</color>");
                        return;
                    }

                    relativePath = safeRelative;
                }

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, isServer, cts.Token, forceMainConsole, uiTextEditorPath).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=#8B0000>Blame cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Blame failed:</color> {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                SVNShared.CancelAndDisposeLater(cts);
                ExitProcessing();
            }
        }

        private async Task ShowBlameInternal(string safePath, bool isServer, CancellationToken token, bool forceMainConsole, string uiTextEditorPath)
        {
            string workDir = svnManager.WorkingDir;
            string fullLocalPath = isServer ? "" : Path.GetFullPath(Path.Combine(workDir, safePath));

            string svnTarget;
            if (isServer)
            {
                if (safePath.Contains("://"))
                {
                    svnTarget = safePath;
                }
                else
                {
                    string repoUrl = svnManager.RepositoryUrl?.TrimEnd('/');
                    if (string.IsNullOrEmpty(repoUrl))
                    {
                        PostLog("<color=#FFAA00>Error:</color> Cannot blame server path, Repository URL is empty.");
                        return;
                    }
                    svnTarget = repoUrl + safePath;
                }
            }
            else
            {
                svnTarget = safePath;
            }

            PostLog($"Fetching Annotations for: <color=green>{safePath}</color>...");

            string raw;
            try
            {
                raw = await SvnRunner.RunAsync(
                    $"blame \"{SVNShared.EscapeSvnArg(svnTarget)}\"",
                    workDir, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostLog($"<color=#FF4444>SVN Error:</color> {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                PostLog("<color=yellow>Blame returned no data. The file may be empty or not versioned.</color>");
                return;
            }

            // tani hardening, gdyby ktoś przestawił throwOnError
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("svn: E") || trimmed.StartsWith("svn: W"))
            {
                PostLog($"<color=#FF4444>SVN Error:</color> {raw.Trim()}");
                return;
            }
            if (raw.Contains("Skipping binary file") || raw.Contains("is a binary file"))
            {
                PostLog("<color=#8B0000>Binary file – blame not available.</color>");
                return;
            }

            var richReport = new StringBuilder(32 * 1024);
            var plainReport = new StringBuilder(32 * 1024);
            int totalLines = 0;

            richReport.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(safePath)}</b></size>");
            if (forceMainConsole)
                richReport.AppendLine("<color=#666666>(forced to main console)</color>");
            richReport.AppendLine("<color=#444444> LINE | REV   | AUTHOR       | CONTENT</color>");
            richReport.AppendLine("--------------------------------------------------");

            plainReport.AppendLine("SVN BLAME REPORT");
            plainReport.AppendLine($"File: {safePath}");
            plainReport.AppendLine($"Generated: {DateTime.Now}");
            plainReport.AppendLine(new string('-', 60));
            plainReport.AppendLine(" LINE |  REV  |   AUTHOR       | CONTENT");
            plainReport.AppendLine(new string('-', 60));

            string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string rawLine in lines)
            {
                token.ThrowIfCancellationRequested();

                if (!ParseBlameLine(rawLine, out string rev, out string author, out string content))
                    continue;

                totalLines++;
                string lineNum = totalLines.ToString();
                string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;
                string authorPlain = author.Length > 14 ? author.Substring(0, 14) : author;

                if (totalLines <= MaxPreviewLines)
                {
                    richReport.AppendLine(
                        $"<color=#666666>{lineNum.PadLeft(4)}</color> | " +
                        $"<color=#FFD700>{rev.PadRight(5)}</color> | " +
                        $"<color=#00E5FF>{authorShort.PadRight(12)}</color> | {content}");
                }

                // plain (plik dla edytora) — NIEOKROJONY.
                plainReport.AppendLine($"{lineNum.PadLeft(4)} | {rev.PadLeft(5)} | {authorPlain.PadRight(14)} | {content}");
            }

            if (totalLines == 0)
            {
                PostLog("<color=#FFAA00>No annotatable lines found. File might be empty.</color>");
                return;
            }

            if (totalLines > MaxPreviewLines)
                richReport.AppendLine($"\n<color=#FFAA00>... Preview truncated ({totalLines - MaxPreviewLines} lines). Full report saved to file.</color>");

            richReport.AppendLine("\n<color=green>Blame displayed in console.</color>");
            string finalReport = richReport.ToString();

            // Błędy appendują się (PostLog); raport ZASTĘPUJE widok (zamierzone).
            PostToMainThread(() => SetBlameReport(finalReport));

            // gałąź 1: dedykowany tool blame
            string blameToolPath = ResolveBlameToolPath();
            if (blameToolPath.Length > 0 && File.Exists(blameToolPath))
            {
                string targetForTool = isServer ? svnTarget : fullLocalPath;
                string processArguments = blameToolPath.IndexOf("TortoiseProc", StringComparison.OrdinalIgnoreCase) >= 0
                    ? $"/command:blame /path:\"{targetForTool}\""
                    : $"\"{targetForTool}\"";

                PostToMainThread(() =>
                {
                    if (SVNShared.TryStartProcess(new ProcessStartInfo
                    {
                        FileName = blameToolPath,
                        Arguments = processArguments,
                        UseShellExecute = true
                    },
                         ex => LogBoth($"<color=#FFAA00>[Blame] Failed to launch tool ({ex.GetType().Name}):</color> {ex.Message}")))
                    {
                        LogBoth($"<color=green>[Blame] Opened in external tool:</color> {Path.GetFileName(safePath)}");
                    }
                });

                return;
            }

            // gałąź 2: pełny raport do pliku + edytor tekstu
            string textEditorPath = ResolveTextEditorPath(uiTextEditorPath);
            if (textEditorPath.Length > 0 && File.Exists(textEditorPath))
            {
                string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Cache");
                Directory.CreateDirectory(cacheFolder);

                // GUID w nazwie — kolizje przy plikach o tej samej nazwie.
                string tempPath = Path.Combine(cacheFolder,
                    $"Blame_{Path.GetFileNameWithoutExtension(safePath)}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.txt");

                await File.WriteAllTextAsync(tempPath, plainReport.ToString(), token).ConfigureAwait(false);

                SVNShared.CleanupOldFiles(cacheFolder, "Blame_*.txt", TimeSpan.FromHours(24));
                string absoluteTempPath = Path.GetFullPath(tempPath);

                PostToMainThread(() =>
                {
                    if (!SVNShared.TryStartProcess(new ProcessStartInfo
                    {
                        FileName = textEditorPath,
                        Arguments = $"\"{absoluteTempPath}\"",
                        UseShellExecute = true
                    },
                         ex => UnityEngine.Debug.LogError($"[SVN Blame] Failed to launch text editor: {ex.Message}")))
                    {
                        LogBoth("<color=#FFAA00>[Blame] Could not launch text editor – report saved at:</color> " + absoluteTempPath);
                    }
                });
            }
        }

        private void SetBlameReport(string report)
        {
            bool updatedAny = false;

            if (svnUI?.BlameConsoleText != null)
            {
                svnUI.BlameConsoleText.text = report;
                updatedAny = true;
            }
            else if (svnUI?.BlameDisplayArea != null)
            {
                svnUI.BlameDisplayArea.text = report;
                updatedAny = true;
            }

            if (svnUI?.LogText != null)
            {
                svnUI.LogText.text = report;
                updatedAny = true;
            }

            if (!updatedAny)
                SVNLogBridge.LogLine(report);
        }

        private string ResolveBlameToolPath()
        {
            string path = svnManager?.BlameToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = SVNPrefs.GetString(SVNManager.KEY_BLAME_TOOL, "");

            path = path?.Trim().Trim('"');
            return path ?? "";
        }

        // Fallback na pole UI TYLKO ze snapshotu z main threadu (wejscie
        // ExecuteBlame). Programatyczne wejścia: prefs/manager.
        private string ResolveTextEditorPath(string uiSnapshot)
        {
            string path = SVNPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");

            if (string.IsNullOrWhiteSpace(path))
            {
                path = svnManager?.MergeToolPath;
                if (!string.IsNullOrEmpty(path) && path.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0)
                    path = "";
            }

            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(uiSnapshot))
                path = uiSnapshot;

            path = path?.Trim().Trim('"');
            return path ?? "";
        }

        private bool ParseBlameLine(string rawLine, out string rev, out string author, out string content)
        {
            rev = "-"; author = ""; content = "";
            if (string.IsNullOrWhiteSpace(rawLine)) return false;

            int i = 0;
            int len = rawLine.Length;

            while (i < len && char.IsWhiteSpace(rawLine[i])) i++;
            if (i >= len) return false;

            int revStart = i;
            while (i < len && !char.IsWhiteSpace(rawLine[i])) i++;
            rev = rawLine.Substring(revStart, i - revStart);

            if (rev != "-" && !int.TryParse(rev, out _)) return false;

            while (i < len && char.IsWhiteSpace(rawLine[i])) i++;
            if (i >= len) return false;

            int authStart = i;
            while (i < len && !char.IsWhiteSpace(rawLine[i])) i++;
            author = rawLine.Substring(authStart, i - authStart);

            if (i < len && rawLine[i] == ' ')
                i++;

            content = rawLine.Substring(i);
            return true;
        }
    }
}