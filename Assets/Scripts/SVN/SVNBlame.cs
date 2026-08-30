using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBlame : SVNBase, IDisposable
    {
        private const int BlameTimeoutSeconds = 60;

        private CancellationTokenSource _cts;
        private int _processingFlag;
        private int _disposed;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }

        public void Cancel()
        {
            try
            {
                var cts = Volatile.Read(ref _cts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
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

        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

        private void PostLog(string msg) => PostUI(() => LogBoth(msg));

        private void SafeFireAndForget(Func<Task> operation) => _ = FireAndForget(operation);

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {ex.Message}"); }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI?.BlameConsoleText != null)
                SVNLogBridge.UpdateUIField(svnUI.BlameConsoleText, msg, "BLAME", true);
        }

        // === FIX K2: snapshot pola UI NA MAIN THREAD (wejście z przycisku),
        // przekazany do rdzenia. Wcześniej odczyt TMP mógł nastąpić z puli wątków.
        public void ExecuteBlame()
        {
            string relativePath = svnUI?.BlameTargetFileInput?.text?.Trim();
            SafeFireAndForget(() => RunBlameAsync(relativePath, forceMainConsole: false));
        }

        // === FIX K1+Ś4: wszystkie wejścia przechodzą przez jeden rdzeń z pełnym
        // guard→try natychmiast→catch OCE→finally. ShowBlameInExternalEditor
        // zachowuje sygnaturę (możliwi callerzy), semantyka = ShowBlame.
        public async Task ShowBlame(string relativePath, CancellationToken token = default)
        {
            using var externalCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            externalCts.CancelAfter(TimeSpan.FromSeconds(BlameTimeoutSeconds));
            await RunBlameCoreAsync(relativePath, externalCts.Token, forceMainConsole: false,
                acquireGuard: false).ConfigureAwait(false);
        }

        public async Task ShowBlameInExternalEditor(string relativePath)
        {
            await RunBlameAsync(relativePath, forceMainConsole: false).ConfigureAwait(false);
        }

        public async Task ShowBlameInMainConsole(string relativePath)
        {
            await RunBlameAsync(relativePath, forceMainConsole: true).ConfigureAwait(false);
        }

        // === FIX K1: jedyny punkt zajmowania guardu; try NATYCHMIAST po nim —
        // zero pracy (walidacji, odczytów UI, awaitów) między guardem a try.
        private async Task RunBlameAsync(string relativePath, bool forceMainConsole)
        {
            if (!TryEnterProcessing()) return;

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(BlameTimeoutSeconds));
            Interlocked.Exchange(ref _cts, cts);
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, cts.Token, forceMainConsole).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=orange>Blame cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Blame failed:</color> {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });   // === FIX Ś5
                ExitProcessing();
            }
        }

        // === FIX K1: rdzeń bez własnego guardu (guard należy do wywołującego);
        // walidacja ścieżki + wszystkie wczesne returny bez ryzyka wycieku flagi.
        private async Task RunBlameCoreAsync(string relativePath, CancellationToken token, bool forceMainConsole, bool acquireGuard)
        {
            if (acquireGuard)
            {
                if (!TryEnterProcessing()) return;
            }

            try
            {
                if (string.IsNullOrEmpty(relativePath))
                {
                    PostLog("<color=yellow>Please select a file path first.</color>");
                    return;
                }

                if (!IsValidPath(relativePath))
                {
                    PostLog("<color=#FFAA00>Invalid path. Path cannot contain '..' or control characters.</color>");
                    return;
                }

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, token, forceMainConsole).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=orange>Blame cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Blame failed:</color> {ex.Message}");
            }
            finally
            {
                if (acquireGuard) ExitProcessing();
            }
        }

        private static bool IsValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            return !path.Contains("..") &&
                   path.IndexOf('\0') < 0 &&
                   !path.Any(char.IsControl);
        }

        private async Task ShowBlameInternal(string relativePath, CancellationToken token, bool forceMainConsole = false)
        {
            bool isServerPath = relativePath.StartsWith("/") || relativePath.StartsWith("http") || relativePath.StartsWith("svn");

            if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir) && !isServerPath)
            {
                PostLog("<color=#FFAA00>Error:</color> Working Directory not set!");
                return;
            }

            string fullPath = "";
            try
            {
                fullPath = !isServerPath
                    ? Path.GetFullPath(Path.Combine(svnManager.WorkingDir ?? "", relativePath))
                    : "";
            }
            catch (Exception ex)
            {
                // === FIX: GetFullPath na ścieżce z inputu mógł rzucić (illegal chars)
                // i przedostać się do catch(OperationCanceled) — brak, czyli caller
                // dostawał surowy wyjątek.
                PostUI(() => DisplayBlameMessage($"<color=#FF4444>Invalid path:</color> {ex.Message}"));
                return;
            }

            PostLog($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            string commandArgs;
            string workDir = svnManager.WorkingDir;

            if (isServerPath)
            {
                string fullUrl;

                if (relativePath.Contains("://"))
                {
                    fullUrl = relativePath;
                }
                else
                {
                    string repoUrl = svnManager.RepositoryUrl?.TrimEnd('/');
                    if (string.IsNullOrEmpty(repoUrl))
                    {
                        PostUI(() => DisplayBlameMessage("<color=#FFAA00>Error:</color> Cannot blame server path, Repository URL is empty."));
                        return;
                    }
                    fullUrl = repoUrl + relativePath;
                }

                commandArgs = $"blame \"{EscapeSvnArg(fullUrl)}\"";
            }
            else
            {
                commandArgs = $"blame \"{EscapeSvnArg(relativePath)}\"";
            }

            string raw;
            try
            {
                raw = await SvnRunner.RunAsync(commandArgs, workDir, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostUI(() => DisplayBlameMessage($"<color=#FF4444>SVN Error:</color> {ex.Message}"));
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                PostUI(() => DisplayBlameMessage("<color=yellow>Blame returned no data. The file may be empty or not versioned.</color>"));
                return;
            }

            // Ś2: te checki są martwe przy throwOnError=true (RunAsync rzuci), ale
            // zostają jako tani hardening gdyby kiedyś ktoś przestawił flagę.
            if (raw.TrimStart().StartsWith("svn: E") || raw.TrimStart().StartsWith("svn: W"))
            {
                PostUI(() => DisplayBlameMessage($"<color=#FF4444>SVN Error:</color> {raw.Trim()}"));
                return;
            }

            if (raw.Contains("Skipping binary file") || raw.Contains("is a binary file"))
            {
                PostUI(() => DisplayBlameMessage("<color=orange>Binary file – blame not available.</color>"));
                return;
            }

            var richReport = new StringBuilder();
            var plainReport = new StringBuilder();
            const int maxDisplayLines = 500;
            int totalLines = 0;

            richReport.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
            if (forceMainConsole)
                richReport.AppendLine("<color=#666666>(forced to main console)</color>");
            richReport.AppendLine("<color=#444444> LINE | REV   | AUTHOR       | CONTENT</color>");
            richReport.AppendLine("--------------------------------------------------");

            plainReport.AppendLine("SVN BLAME REPORT");
            plainReport.AppendLine($"File: {relativePath}");
            plainReport.AppendLine($"Generated: {DateTime.Now}");
            plainReport.AppendLine(new string('-', 60));
            plainReport.AppendLine(" LINE |  REV  |   AUTHOR       | CONTENT");
            plainReport.AppendLine(new string('-', 60));

            string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                token.ThrowIfCancellationRequested();

                if (ParseBlameLine(rawLine, out string rev, out string author, out string content))
                {
                    totalLines++;
                    string lineNum = totalLines.ToString();

                    string authorShort = author.Length > 12 ? author.Substring(0, 12) : author;
                    string authorPlain = author.Length > 14 ? author.Substring(0, 14) : author;

                    if (totalLines <= maxDisplayLines)
                    {
                        richReport.AppendLine(
                            $"<color=#666666>{lineNum.PadLeft(4)}</color> | " +
                            $"<color=#FFD700>{rev.PadRight(5)}</color> | " +
                            $"<color=#00E5FF>{authorShort.PadRight(12)}</color> | {content}");

                        plainReport.AppendLine($"{lineNum.PadLeft(4)} | {rev.PadLeft(5)} | {authorPlain.PadRight(14)} | {content}");
                    }
                }
            }

            if (totalLines == 0)
            {
                PostUI(() => DisplayBlameMessage("<color=#FFAA00>No annotatable lines found. File might be empty.</color>"));
                return;
            }

            if (totalLines > maxDisplayLines)
            {
                richReport.AppendLine("\n<color=#FFAA00>... Blame truncated in preview.</color>");
                plainReport.AppendLine($"\n... (truncated {totalLines - maxDisplayLines} lines)");
            }

            richReport.AppendLine("\n<color=green>Blame displayed in console.</color>");
            string finalReportString = richReport.ToString();

            PostUI(() =>
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text = finalReportString;

                if (svnUI?.BlameConsoleText != null)
                    svnUI.BlameConsoleText.text = finalReportString;
                else if (svnUI?.BlameDisplayArea != null)
                    svnUI.BlameDisplayArea.text = finalReportString;

                Canvas.ForceUpdateCanvases();
            });

            string blameToolPath = GetBlameToolPath();
            bool fileExists = !string.IsNullOrEmpty(blameToolPath) && File.Exists(blameToolPath);

            // === FIX Ś1: diag-log krótszy i NIE sklejany w nieskończoność do głównego
            // logu (LogText.text += przy każdym blame rozpychało konsolę bez limitu).
            if (fileExists)
            {
                string targetPathForBlame = fullPath;
                string processArguments = "";

                if (isServerPath)
                {
                    if (relativePath.Contains("://")) targetPathForBlame = relativePath;
                    else targetPathForBlame = svnManager.RepositoryUrl?.TrimEnd('/') + relativePath;
                }

                if (blameToolPath.IndexOf("TortoiseProc", StringComparison.OrdinalIgnoreCase) >= 0)
                    processArguments = $"/command:blame /path:\"{targetPathForBlame}\"";
                else
                    processArguments = $"\"{targetPathForBlame}\"";

                PostUI(() =>
                {
                    try
                    {
                        using var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = blameToolPath,
                            Arguments = processArguments,
                            UseShellExecute = true
                        });
                        LogBoth($"<color=green>[Blame] Opened in external tool:</color> {Path.GetFileName(relativePath)}");
                    }
                    catch (Exception ex)
                    {
                        LogBoth($"<color=#FFAA00>[Blame] Failed to launch tool ({ex.GetType().Name}):</color> {ex.Message}");
                    }
                });

                return;
            }

            string textEditorPath = GetTextEditorPath();

            if (!string.IsNullOrEmpty(textEditorPath) && File.Exists(textEditorPath))
            {
                string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Cache");
                Directory.CreateDirectory(cacheFolder);

                string fileName = $"Blame_{Path.GetFileNameWithoutExtension(relativePath)}.txt";
                string tempPath = Path.Combine(cacheFolder, fileName);
                await File.WriteAllTextAsync(tempPath, plainReport.ToString(), token).ConfigureAwait(false);

                CleanupOldBlameFiles();
                string absoluteTempPath = Path.GetFullPath(tempPath);

                PostUI(() =>
                {
                    try
                    {
                        using var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = textEditorPath,
                            Arguments = $"\"{absoluteTempPath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[SVN Blame] Failed to launch text editor: {ex.Message}");
                    }
                });
            }
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            return arg.Replace("\"", "\\\"");
        }

        // === FIX: PlayerPrefs przez SVNPrefs (wołane po awaitach → pula).
        private string GetBlameToolPath()
        {
            string path = svnManager?.BlameToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = SVNPrefs.GetString(SVNManager.KEY_BLAME_TOOL, "");

            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Trim().Trim('"');
            }

            return path;
        }

        private string GetTextEditorPath()
        {
            string path = SVNPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");

            if (string.IsNullOrWhiteSpace(path))
            {
                path = svnManager?.MergeToolPath;
                if (!string.IsNullOrEmpty(path) && path.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    path = "";
                }
            }

            if (string.IsNullOrWhiteSpace(path))
                path = svnUI?.SettingsMergeToolPathInput?.text;

            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Trim().Trim('"');
            }

            return path;
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
            {
                i++;
            }

            content = rawLine.Substring(i);
            return true;
        }

        private void CleanupOldBlameFiles()
        {
            try
            {
                string cache = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Cache");
                if (!Directory.Exists(cache)) return;

                foreach (var file in Directory.EnumerateFiles(cache, "Blame_*.txt"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.CreationTimeUtc < DateTime.UtcNow.AddHours(-24))
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void DisplayBlameMessage(string message)
        {
            bool updatedAny = false;

            if (svnUI?.LogText != null)
            {
                svnUI.LogText.text = message;
                updatedAny = true;
            }

            if (svnUI?.BlameConsoleText != null)
            {
                svnUI.BlameConsoleText.text = message;
                updatedAny = true;
            }
            else if (svnUI?.BlameDisplayArea != null)
            {
                svnUI.BlameDisplayArea.text = message;
                updatedAny = true;
            }

            if (!updatedAny)
            {
                SVNLogBridge.LogLine(message);
            }
            else
            {
                Canvas.ForceUpdateCanvases();
            }
        }
    }
}