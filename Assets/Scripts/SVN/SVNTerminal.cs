using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNTerminal : SVNBase
    {
        private const int MaxHistory = 50;
        private const int MaxConsoleLines = 10;
        private readonly List<string> commandHistory = new();
        private int historyIndex = -1;
        private CancellationTokenSource _cts;
        private TMP_InputField _terminalInputField;
        private TMP_Text _consoleOutput;

        public SVNTerminal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void SetInputField(TMP_InputField inputField) => _terminalInputField = inputField;

        public void SetConsoleOutput(TMP_Text consoleOutput)
        {
            _consoleOutput = consoleOutput;
            if (_consoleOutput == null)
                SVNLogBridge.LogLine("<color=#FFCC00>[TERMINAL] Console output not set – fallback to main log.</color>", append: true);
        }

        public void Cancel()
        {
            if (_cts == null) return;
            SVNLogBridge.LogLine("<color=#FFD700>[TERMINAL] Cancelling…</color>", append: true);
            _cts.Cancel();
        }

        public async void ExecuteTerminalCommand()
        {
            if (IsProcessing) return;
            if (_terminalInputField == null) return;

            string rawInput = _terminalInputField.text?.Trim();
            if (string.IsNullOrWhiteSpace(rawInput)) return;

            if (rawInput.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                rawInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearLog();
                commandHistory.Clear();
                historyIndex = -1;
                _terminalInputField.text = "";
                _terminalInputField.ActivateInputField();
                return;
            }

            if (commandHistory.Count == 0 || !string.Equals(commandHistory[^1], rawInput, StringComparison.Ordinal))
            {
                if (commandHistory.Count >= MaxHistory) commandHistory.RemoveAt(0);
                commandHistory.Add(rawInput);
            }
            historyIndex = -1;

            string cmd = rawInput;
            if (cmd.StartsWith("svn ", StringComparison.OrdinalIgnoreCase))
                cmd = cmd[4..].Trim();
            if (string.IsNullOrWhiteSpace(cmd))
            {
                TerminalWriteLine("<color=#FFCC00>Usage: svn <command></color>");
                return;
            }

            // Zapamiętaj oryginalną komendę (przed dodaniem flag) do późniejszego parsowania checkout args
            string originalCmd = cmd;

            if (!TryExtractKeyPath(ref cmd, out string keyPath)) return;
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                SvnRunner.KeyPath = keyPath;
                TerminalWriteLine($"<color=#00E5FF>[SSH] Using key: {keyPath}</color>");
            }

            cmd = AddIfMissing(cmd, "--non-interactive");
            cmd = AddIfMissing(cmd, "--trust-server-cert");

            _terminalInputField.text = "";
            TerminalWriteLine($"<color=#FFFF00>> svn {cmd}</color>");

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            string workDir = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                workDir = Path.GetTempPath();

            // Jeśli to checkout, spróbuj sparsować URL i ścieżkę lokalną (do rejestracji po sukcesie)
            string checkoutUrl = null, checkoutLocalPath = null;
            string firstWord = GetFirstWord(originalCmd);
            if (firstWord.Equals("checkout", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("co", StringComparison.OrdinalIgnoreCase))
            {
                TryParseCheckoutArgs(originalCmd, out checkoutUrl, out checkoutLocalPath);
            }

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                int exitCode = await SvnRunner.RunStreamedAsync(cmd, workDir,
                    line => { if (!string.IsNullOrWhiteSpace(line)) TerminalWriteLine(line); },
                    token);

                if (exitCode != 0)
                {
                    TerminalWriteLine($"<color=#FF0000>Command exited with code {exitCode}</color>");
                }
                else
                {
                    TerminalWriteLine("<color=#00FF00>Command completed successfully.</color>");

                    // Rejestracja projektu po udanym checkoucie
                    if (!string.IsNullOrEmpty(checkoutUrl) && !string.IsNullOrEmpty(checkoutLocalPath) &&
                        Directory.Exists(Path.Combine(checkoutLocalPath, ".svn")))
                    {
                        RegisterProjectAfterCheckout(checkoutUrl, checkoutLocalPath, keyPath);
                    }
                }

                if (ShouldRefresh(GetCommandName(cmd)))
                    await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                TerminalWriteLine("<color=#FF9900>Command cancelled.</color>");
            }
            catch (Exception ex)
            {
                TerminalWriteLine($"<color=#FF0000>Terminal Error: {ex.Message}</color>");
                Debug.LogException(ex);
            }
            finally
            {
                IsProcessing = false;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                _terminalInputField?.ActivateInputField();
            }
        }

        private void TryParseCheckoutArgs(string cmd, out string url, out string localPath)
        {
            url = null;
            localPath = null;
            string[] tokens = cmd.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) return;

            int urlIdx = -1;
            for (int i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].Contains("://"))
                {
                    urlIdx = i;
                    break;
                }
            }
            if (urlIdx < 0) return;
            url = tokens[urlIdx].Trim('"');

            if (urlIdx + 1 < tokens.Length)
            {
                localPath = tokens[urlIdx + 1].Trim('"');
                if (!Path.IsPathRooted(localPath))
                    localPath = Path.GetFullPath(Path.Combine(svnManager.WorkingDir ?? "", localPath));
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

                if (SVNManager.Instance != null)
                {
                    var pollingService = SVNManager.Instance.GetComponent<SVNPollingService>();
                    pollingService?.ResetRevisionTracking();
                }

                RegisterProjectInSettings(localPath, url, keyPath);
                SVNLogBridge.LogLine($"<color=green>Project '{projectName}' loaded successfully.</color>", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Failed to load project after checkout: {ex.Message}</color>", append: true);
            }
        }

        private void RegisterProjectInSettings(string path, string url, string keyPath)
        {
            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            var projects = ProjectSettings.LoadProjects();
            int idx = projects.FindIndex(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'), normalizedPath, StringComparison.OrdinalIgnoreCase));

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

        private string GetRepoNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Repository";
            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase)) url = url[..^"/trunk".Length];
            if (url.EndsWith("/branches", StringComparison.OrdinalIgnoreCase)) url = url[..^"/branches".Length];
            if (url.EndsWith("/tags", StringComparison.OrdinalIgnoreCase)) url = url[..^"/tags".Length];
            int slash = url.LastIndexOf('/');
            return slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : url;
        }

        private string GetFirstWord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            int idx = text.IndexOf(' ');
            return idx < 0 ? text : text[..idx];
        }

        private string AddIfMissing(string cmd, string arg)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return arg;
            return cmd.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 ? cmd : $"{cmd} {arg}";
        }

        private string GetCommandName(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return "";
            string[] tokens = cmd.TrimStart().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[0].Trim().ToLowerInvariant() : "";
        }

        private bool ShouldRefresh(string cmdName)
            => cmdName is "checkout" or "update" or "commit" or "revert" or "cleanup" or "switch" or "merge";

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
                if (text[i] == '\n') lineCount++;

            if (lineCount > MaxConsoleLines)
            {
                int linesToRemove = lineCount - MaxConsoleLines;
                int cutIndex = 0;
                for (int i = 0; i < linesToRemove; i++)
                {
                    cutIndex = text.IndexOf('\n', cutIndex) + 1;
                    if (cutIndex == 0) break;
                }
                _consoleOutput.text = text[cutIndex..];
            }
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
            while (pathStart < command.Length && char.IsWhiteSpace(command[pathStart])) pathStart++;
            if (pathStart >= command.Length)
            {
                TerminalWriteLine("<color=#FF0000>Missing path after --key.</color>");
                return false;
            }

            int pathEnd;
            if (command[pathStart] == '"')
            {
                pathStart++;
                pathEnd = command.IndexOf('"', pathStart);
                if (pathEnd < 0)
                {
                    TerminalWriteLine("<color=#FF0000>Missing closing quote for --key path.</color>");
                    return false;
                }
                keyPath = command[pathStart..pathEnd];
                pathEnd++;
            }
            else
            {
                pathEnd = pathStart;
                while (pathEnd < command.Length && !char.IsWhiteSpace(command[pathEnd])) pathEnd++;
                keyPath = command[pathStart..pathEnd];
            }

            keyPath = keyPath.Trim();
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                TerminalWriteLine("<color=#FF0000>SSH key path is empty.</color>");
                return false;
            }

            try { keyPath = Path.GetFullPath(keyPath); }
            catch (Exception ex)
            {
                TerminalWriteLine($"<color=#FF0000>Invalid path: {ex.Message}</color>");
                return false;
            }
            if (!File.Exists(keyPath))
            {
                TerminalWriteLine($"<color=#FF0000>Key not found: {keyPath}</color>");
                return false;
            }

            int removeLen = pathEnd - idx;
            command = command.Remove(idx, removeLen).Trim();
            return true;
        }

        public void HandleHistoryNavigation()
        {
            if (_terminalInputField == null || commandHistory.Count == 0 || !_terminalInputField.isFocused) return;

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                if (historyIndex == -1) historyIndex = commandHistory.Count - 1;
                else if (historyIndex > 0) historyIndex--;
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
            if (_terminalInputField == null || historyIndex < 0 || historyIndex >= commandHistory.Count) return;
            string cmd = commandHistory[historyIndex];
            _terminalInputField.text = cmd;
            _terminalInputField.caretPosition = cmd.Length;
        }

        public void ClearLog()
        {
            SVNLogBridge.LogLine("", append: false);
            if (_consoleOutput != null)
            {
                _consoleOutput.text = "";
                Canvas.ForceUpdateCanvases();
            }
        }
    }
}