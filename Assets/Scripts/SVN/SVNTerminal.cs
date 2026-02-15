using UnityEngine;
using System.Collections.Generic;

namespace SVN.Core
{
    public class SVNTerminal : SVNBase
    {
        private List<string> commandHistory = new List<string>();
        private int historyIndex = -1;

        public SVNTerminal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void ExecuteTerminalCommand()
        {
            if (IsProcessing) return;

            string rawInput = svnUI.TerminalInputField.text.Trim();
            if (string.IsNullOrEmpty(rawInput)) return;

            if (rawInput.ToLower() == "cls" || rawInput.ToLower() == "clear")
            {
                svnUI.TerminalInputField.text = "";
                ClearLog();
                return;
            }

            if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != rawInput)
            {
                commandHistory.Add(rawInput);
            }
            historyIndex = -1;

            string cmd = rawInput.ToLower().StartsWith("svn ") ? rawInput.Substring(4).Trim() : rawInput;

            svnUI.TerminalInputField.text = "";

            SVNLogBridge.LogLine($"<color=#FFFF00>> svn {cmd}</color>", append: true);

            IsProcessing = true;

            try
            {
                string result = await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                if (!string.IsNullOrEmpty(result))
                {
                    SVNLogBridge.LogLine(result, append: true);
                }

                if (cmd.Contains("update") || cmd.Contains("commit") || cmd.Contains("switch") || cmd.Contains("checkout") || cmd.Contains("cleanup"))
                {
                    await svnManager.RefreshStatus();
                }
            }
            catch (System.Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Terminal Error:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
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
            SVNLogBridge.LogLine("<color=#888888>Terminal log cleared.</color>", append: true);
        }
    }
}