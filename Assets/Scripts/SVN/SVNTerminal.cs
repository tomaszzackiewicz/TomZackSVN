using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public class SVNTerminal : SVNBase
    {
        private const int MaxHistory = 50;
        private List<string> commandHistory = new List<string>();
        private int historyIndex = -1;
        private CancellationTokenSource _cts;

        public SVNTerminal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        public async void ExecuteTerminalCommand()
        {
            if (IsProcessing) return;

            string rawInput = svnUI.TerminalInputField.text.Trim();
            if (string.IsNullOrEmpty(rawInput)) return;

            if (rawInput.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                rawInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearLog();
                commandHistory.Clear();
                historyIndex = -1;
                svnUI.TerminalInputField.text = "";
                return;
            }

            if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != rawInput)
            {
                if (commandHistory.Count >= MaxHistory)
                    commandHistory.RemoveAt(0);
                commandHistory.Add(rawInput);
            }
            historyIndex = -1;

            string cmdToExecute = rawInput;
            if (rawInput.StartsWith("svn ", StringComparison.OrdinalIgnoreCase))
                cmdToExecute = rawInput.Substring(4).Trim();

            if (string.IsNullOrEmpty(cmdToExecute))
            {
                SVNLogBridge.LogLine("<color=yellow>Usage: svn <command></color>", append: true);
                return;
            }

            if (!cmdToExecute.Contains("--non-interactive"))
                cmdToExecute += " --non-interactive";

            svnUI.TerminalInputField.text = "";
            SVNLogBridge.LogLine($"<color=#FFFF00>> svn {cmdToExecute}</color>", append: true);

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string result = await SvnRunner.RunAsync(cmdToExecute, svnManager.WorkingDir, token: token);

                if (!string.IsNullOrEmpty(result))
                    SVNLogBridge.LogLine(result, append: true);
                else
                    SVNLogBridge.LogLine("<color=green>Command completed with no output.</color>", append: true);

                if (cmdToExecute.Contains("update") || cmdToExecute.Contains("commit") ||
                    cmdToExecute.Contains("revert") || cmdToExecute.Contains("cleanup") ||
                    cmdToExecute.Contains("switch") || cmdToExecute.Contains("merge"))
                {
                    await svnManager.RefreshStatus();
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Command cancelled.</color>", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Terminal Error:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
                svnUI.TerminalInputField.ActivateInputField();
            }
        }

        public void HandleHistoryNavigation()
        {
            if (commandHistory.Count == 0 || !svnUI.TerminalInputField.isFocused) return;

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                if (historyIndex == -1) historyIndex = commandHistory.Count - 1;
                else if (historyIndex > 0) historyIndex--;
                UpdateInputFieldFromHistory();
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                if (historyIndex != -1)
                {
                    if (historyIndex < commandHistory.Count - 1)
                    {
                        historyIndex++;
                        UpdateInputFieldFromHistory();
                    }
                    else
                    {
                        historyIndex = -1;
                        svnUI.TerminalInputField.text = "";
                    }
                }
            }
        }

        private void UpdateInputFieldFromHistory()
        {
            if (historyIndex >= 0 && historyIndex < commandHistory.Count)
            {
                svnUI.TerminalInputField.text = commandHistory[historyIndex];
                svnUI.TerminalInputField.caretPosition = svnUI.TerminalInputField.text.Length;
            }
        }

        public void ClearLog()
        {
            SVNLogBridge.LogLine("", append: false);
        }
    }
}