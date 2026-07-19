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

        public void SetInputField(TMP_InputField inputField)
        {
            _terminalInputField = inputField;
        }

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

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                int exitCode = await SvnRunner.RunStreamedAsync(cmd, workDir,
                    line =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            TerminalWriteLine(line);
                    },
                    token);

                if (exitCode != 0)
                    TerminalWriteLine($"<color=#FF0000>Command exited with code {exitCode}</color>");
                else
                    TerminalWriteLine("<color=#00FF00>Command completed successfully.</color>");

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
            int lastNewLine = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineCount++;
                    lastNewLine = i;
                }
            }
            
            if (lineCount > MaxConsoleLines)
            {
                int linesToRemove = lineCount - MaxConsoleLines;
                int cutIndex = 0;
                for (int i = 0; i < linesToRemove; i++)
                {
                    cutIndex = text.IndexOf('\n', cutIndex) + 1;
                    if (cutIndex == 0) break;
                }
                _consoleOutput.text = text.Substring(cutIndex);
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
                keyPath = command.Substring(pathStart, pathEnd - pathStart);
                pathEnd++;
            }
            else
            {
                pathEnd = pathStart;
                while (pathEnd < command.Length && !char.IsWhiteSpace(command[pathEnd])) pathEnd++;
                keyPath = command.Substring(pathStart, pathEnd - pathStart);
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