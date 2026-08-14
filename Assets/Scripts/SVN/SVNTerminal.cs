using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNTerminal : SVNBase, IDisposable
    {
        private const int MaxHistory = 50;
        private const int MaxConsoleLines = 300;

        private readonly List<string> commandHistory = new();
        private int historyIndex = -1;

        private CancellationTokenSource _cts;
        private readonly object _ctsLock = new object();
        private int _isBusy; // 0 = free, 1 = busy
        private bool _disposed;

        private TMP_InputField _terminalInputField;
        private TMP_Text _consoleOutput;

        public SVNTerminal(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            if (ui != null)
            {
                _terminalInputField = ui.TerminalInputField;
                _consoleOutput = ui.TerminalConsoleOutput;

                if (_consoleOutput == null)
                {
                    Debug.LogWarning("[SVNTerminal] Console output UI element is not assigned in SVNUI.");
                }
            }
        }

        public void SetInputField(TMP_InputField inputField) => _terminalInputField = inputField;

        public void SetConsoleOutput(TMP_Text consoleOutput)
        {
            _consoleOutput = consoleOutput;
            if (_consoleOutput == null)
            {
                SVNLogBridge.LogLine(
                    "<color=#FFCC00>[TERMINAL] Console output not set – fallback to main log.</color>",
                    append: true);
            }
        }

        public void Cancel()
        {
            lock (_ctsLock)
            {
                if (_cts == null) return;
                SVNLogBridge.LogLine("<color=#FFD700>[TERMINAL] Cancelling…</color>", append: true);
                try { _cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        public async void ExecuteTerminalCommand()
        {
            try
            {
                await ExecuteTerminalCommandAsync();
            }
            catch (Exception ex)
            {
                TerminalWriteLineSafe($"<color=#FF0000>Critical Terminal Error: {ex.Message}</color>");
                Debug.LogException(ex);
            }
        }

        private async Task ExecuteTerminalCommandAsync()
        {
            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 1)
                return;

            if (_terminalInputField == null)
            {
                Interlocked.Exchange(ref _isBusy, 0);
                return;
            }

            string rawInput = _terminalInputField.text?.Trim();
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                Interlocked.Exchange(ref _isBusy, 0);
                return;
            }

            if (rawInput.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                rawInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearLog();
                _terminalInputField.text = "";
                _terminalInputField.ActivateInputField();
                Interlocked.Exchange(ref _isBusy, 0);
                return;
            }

            if (commandHistory.Count == 0 ||
                !string.Equals(commandHistory[^1], rawInput, StringComparison.Ordinal))
            {
                if (commandHistory.Count >= MaxHistory)
                    commandHistory.RemoveAt(0);
                commandHistory.Add(rawInput);
            }
            historyIndex = -1;

            string cmd = rawInput;
            if (cmd.StartsWith("svn ", StringComparison.OrdinalIgnoreCase))
                cmd = cmd[4..].Trim();

            if (string.IsNullOrWhiteSpace(cmd))
            {
                TerminalWriteLineSafe("<color=#FFCC00>Usage: svn <command></color>");
                Interlocked.Exchange(ref _isBusy, 0);
                return;
            }

            string originalCmd = cmd;

            if (!TryExtractKeyPath(ref cmd, out string keyPath))
            {
                Interlocked.Exchange(ref _isBusy, 0);
                return;
            }

            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                SvnRunner.KeyPath = keyPath;
                TerminalWriteLineSafe($"<color=#00E5FF>[SSH] Using key: {keyPath}</color>");
            }

            cmd = AddIfMissing(cmd, "--non-interactive");
            cmd = AddIfMissing(cmd, "--trust-server-cert");

            _terminalInputField.text = "";
            TerminalWriteLineSafe($"<color=#FFFF00>> svn {cmd}</color>");

            IsProcessing = true;

            CancellationTokenSource cts;
            lock (_ctsLock)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                try { _cts?.Dispose(); } catch (ObjectDisposedException) { }

                cts = new CancellationTokenSource();
                _cts = cts;
            }

            CancellationToken token = cts.Token;

            string workDir = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            {
                TerminalWriteLineSafe("<color=#FFAA00>No valid working directory. Command aborted.</color>");
                CleanupAfterCommand(cts);
                return;
            }

            string checkoutUrl = null;
            string checkoutLocalPath = null;
            string firstWord = GetFirstWord(originalCmd);

            if (firstWord.Equals("checkout", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("co", StringComparison.OrdinalIgnoreCase))
            {
                TryParseCheckoutArgs(originalCmd, out checkoutUrl, out checkoutLocalPath);
            }

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                int exitCode = await SvnRunner.RunStreamedAsync(
                    cmd,
                    workDir,
                    line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        UnityMainThreadDispatcher.Enqueue(() => TerminalWriteLine(line));
                    },
                    token);

                if (exitCode != 0)
                {
                    TerminalWriteLineSafe($"<color=#FF0000>Command exited with code {exitCode}</color>");
                }
                else
                {
                    TerminalWriteLineSafe("<color=#00FF00>Command completed successfully.</color>");

                    if (!string.IsNullOrEmpty(checkoutUrl) &&
                        !string.IsNullOrEmpty(checkoutLocalPath) &&
                        Directory.Exists(Path.Combine(checkoutLocalPath, ".svn")))
                    {
                        string urlSnap = checkoutUrl;
                        string pathSnap = checkoutLocalPath;
                        string keySnap = keyPath;

                        UnityMainThreadDispatcher.Enqueue(() =>
                            RegisterProjectAfterCheckout(urlSnap, pathSnap, keySnap));
                    }
                }

                if (ShouldRefresh(GetCommandName(cmd)))
                    await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                TerminalWriteLineSafe("<color=#FF9900>Command cancelled.</color>");
            }
            catch (Exception ex)
            {
                TerminalWriteLineSafe($"<color=#FF0000>Terminal Error: {ex.Message}</color>");
                Debug.LogException(ex);
            }
            finally
            {
                CleanupAfterCommand(cts);
            }
        }

        private void CleanupAfterCommand(CancellationTokenSource cts)
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _isBusy, 0);

            lock (_ctsLock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    try { cts.Dispose(); } catch (ObjectDisposedException) { }
                }
                else
                {
                    try { cts.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            UnityMainThreadDispatcher.Enqueue(() =>
                _terminalInputField?.ActivateInputField());
        }

        private void TryParseCheckoutArgs(string cmd, out string url, out string localPath)
        {
            url = null;
            localPath = null;

            var tokens = new List<string>();
            int i = 0;
            while (i < cmd.Length)
            {
                while (i < cmd.Length && char.IsWhiteSpace(cmd[i])) i++;
                if (i >= cmd.Length) break;

                if (cmd[i] == '"')
                {
                    i++;
                    int start = i;
                    while (i < cmd.Length && cmd[i] != '"') i++;
                    tokens.Add(cmd[start..i]);
                    if (i < cmd.Length) i++; // skip closing "
                }
                else
                {
                    int start = i;
                    while (i < cmd.Length && !char.IsWhiteSpace(cmd[i])) i++;
                    tokens.Add(cmd[start..i]);
                }
            }

            if (tokens.Count < 2) return;

            int urlIdx = -1;
            for (int t = 1; t < tokens.Count; t++)
            {
                if (tokens[t].Contains("://", StringComparison.Ordinal))
                {
                    urlIdx = t;
                    break;
                }
            }

            if (urlIdx < 0) return;

            url = tokens[urlIdx];

            if (urlIdx + 1 < tokens.Count)
            {
                localPath = tokens[urlIdx + 1];
                if (!Path.IsPathRooted(localPath))
                {
                    string baseDir = svnManager.WorkingDir ?? "";
                    localPath = Path.GetFullPath(Path.Combine(baseDir, localPath));
                }
            }
        }

        private void RegisterProjectAfterCheckout(string url, string localPath, string keyPath)
        {
            try
            {
                string projectName = Path.GetFileName(localPath.TrimEnd('/', '\\'));
                var project = new SVNProject
                {
                    projectName = projectName,
                    repoUrl = url,
                    workingDir = localPath,
                    privateKeyPath = keyPath ?? SvnRunner.KeyPath,
                    lastOpened = DateTime.Now
                };

                SVNManager.Instance?.SetActiveProject(project);
                SVNManager.Instance?.ProjectSelectionPanel?.RefreshList();

                var pollingService = SVNManager.Instance?.GetComponent<SVNPollingService>();
                pollingService?.ResetRevisionTracking();

                RegisterProjectInSettings(localPath, url, keyPath);
                SVNLogBridge.LogLine(
                    $"<color=green>Project '{projectName}' loaded successfully.</color>",
                    append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine(
                    $"<color=#FFAA00>Failed to load project after checkout: {ex.Message}</color>",
                    append: true);
            }
        }

        private void RegisterProjectInSettings(string path, string url, string keyPath)
        {
            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            var projects = ProjectSettings.LoadProjects();

            int idx = projects.FindIndex(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(
                    p.workingDir.Replace("\\", "/").TrimEnd('/'),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));

            string projectName = GetRepoNameFromUrl(url);

            if (idx >= 0)
            {
                projects[idx].repoUrl = url;
                projects[idx].lastOpened = DateTime.Now;
                projects[idx].privateKeyPath = keyPath;
            }
            else
            {
                projects.Add(new SVNProject
                {
                    projectName = projectName,
                    repoUrl = url,
                    workingDir = normalizedPath,
                    privateKeyPath = keyPath,
                    lastOpened = DateTime.Now
                });
            }

            ProjectSettings.SaveProjects(projects);
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        private static string GetRepoNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Repository";

            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/trunk".Length];
            if (url.EndsWith("/branches", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/branches".Length];
            if (url.EndsWith("/tags", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/tags".Length];

            int slash = url.LastIndexOf('/');
            return slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : url;
        }

        private static string GetFirstWord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            int idx = text.IndexOf(' ');
            return idx < 0 ? text : text[..idx];
        }

        private static string AddIfMissing(string cmd, string arg)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return arg;

            int idx = cmd.IndexOf(arg, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                bool leftOk = idx == 0 || char.IsWhiteSpace(cmd[idx - 1]);
                bool rightOk = idx + arg.Length >= cmd.Length ||
                               char.IsWhiteSpace(cmd[idx + arg.Length]);

                if (leftOk && rightOk)
                    return cmd;

                idx = cmd.IndexOf(arg, idx + 1, StringComparison.OrdinalIgnoreCase);
            }

            return $"{cmd} {arg}";
        }

        private static string GetCommandName(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return "";
            string[] tokens = cmd.TrimStart().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[0].Trim().ToLowerInvariant() : "";
        }

        private static bool ShouldRefresh(string cmdName)
        {
            return cmdName is
                "checkout" or "co" or
                "update" or "up" or
                "commit" or "ci" or
                "revert" or
                "cleanup" or
                "switch" or "sw" or
                "merge" or
                "add" or
                "delete" or "del" or "rm" or
                "mkdir" or
                "copy" or "cp" or
                "move" or "mv" or "rename" or
                "resolve" or
                "relocate";
        }

        private void TerminalWriteLineSafe(string message)
        {
            UnityMainThreadDispatcher.Enqueue(() => TerminalWriteLine(message));
        }

        private void TerminalWriteLine(string message)
        {
            if (_consoleOutput != null)
            {
                _consoleOutput.text += message + "\n";
                TrimConsoleLines();
                Canvas.ForceUpdateCanvases();
            }
            else
            {
                SVNLogBridge.LogLine(message, append: true);
            }
        }

        private void TrimConsoleLines()
        {
            if (_consoleOutput == null) return;

            string text = _consoleOutput.text;
            int lineCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') lineCount++;
            }

            if (text.Length > 0 && text[^1] != '\n')
                lineCount++;

            if (lineCount <= MaxConsoleLines) return;

            int linesToRemove = lineCount - MaxConsoleLines;
            int cutIndex = 0;
            for (int i = 0; i < linesToRemove; i++)
            {
                int next = text.IndexOf('\n', cutIndex);
                if (next < 0)
                {
                    cutIndex = text.Length;
                    break;
                }
                cutIndex = next + 1;
            }

            if (cutIndex > 0 && cutIndex <= text.Length)
                _consoleOutput.text = text[cutIndex..];
        }

        private bool TryExtractKeyPath(ref string command, out string keyPath)
        {
            keyPath = null;
            const string keyArg = "--key";

            int idx = command.IndexOf(keyArg, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return true;

            bool validStart = idx == 0 || char.IsWhiteSpace(command[idx - 1]);
            int endIdx = idx + keyArg.Length;
            bool validEnd = endIdx >= command.Length || char.IsWhiteSpace(command[endIdx]);
            if (!validStart || !validEnd) return true;

            int pathStart = endIdx;
            while (pathStart < command.Length && char.IsWhiteSpace(command[pathStart]))
                pathStart++;

            if (pathStart >= command.Length)
            {
                TerminalWriteLineSafe("<color=#FF0000>Missing path after --key.</color>");
                return false;
            }

            int pathEnd;
            if (command[pathStart] == '"')
            {
                pathStart++;
                pathEnd = command.IndexOf('"', pathStart);
                if (pathEnd < 0)
                {
                    TerminalWriteLineSafe("<color=#FF0000>Missing closing quote for --key path.</color>");
                    return false;
                }
                keyPath = command[pathStart..pathEnd];
                pathEnd++;
            }
            else
            {
                pathEnd = pathStart;
                while (pathEnd < command.Length && !char.IsWhiteSpace(command[pathEnd]))
                    pathEnd++;
                keyPath = command[pathStart..pathEnd];
            }

            keyPath = keyPath.Trim();
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                TerminalWriteLineSafe("<color=#FF0000>SSH key path is empty.</color>");
                return false;
            }

            try
            {
                keyPath = Path.GetFullPath(keyPath);
            }
            catch (Exception ex)
            {
                TerminalWriteLineSafe($"<color=#FF0000>Invalid path: {ex.Message}</color>");
                return false;
            }

            if (!File.Exists(keyPath))
            {
                TerminalWriteLineSafe($"<color=#FF0000>Key not found: {keyPath}</color>");
                return false;
            }

            int removeLen = pathEnd - idx;
            command = command.Remove(idx, removeLen).Trim();
            return true;
        }

        public void HandleHistoryNavigation()
        {
            if (_terminalInputField == null ||
                commandHistory.Count == 0 ||
                !_terminalInputField.isFocused)
                return;

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                if (historyIndex == -1)
                    historyIndex = commandHistory.Count - 1;
                else if (historyIndex > 0)
                    historyIndex--;

                UpdateHistoryField();
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                if (historyIndex == -1) return;

                if (historyIndex < commandHistory.Count - 1)
                {
                    historyIndex++;
                    UpdateHistoryField();
                }
                else
                {
                    historyIndex = -1;
                    _terminalInputField.text = "";
                }
            }
        }

        private void UpdateHistoryField()
        {
            if (_terminalInputField == null ||
                historyIndex < 0 ||
                historyIndex >= commandHistory.Count)
                return;

            string cmd = commandHistory[historyIndex];
            _terminalInputField.text = cmd;
            _terminalInputField.caretPosition = cmd.Length;
        }

        public void ClearLog()
        {
            if (_consoleOutput != null)
            {
                _consoleOutput.text = "";
                Canvas.ForceUpdateCanvases();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Cancel();
            lock (_ctsLock)
            {
                try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
                _cts = null;
            }
        }
    }
}