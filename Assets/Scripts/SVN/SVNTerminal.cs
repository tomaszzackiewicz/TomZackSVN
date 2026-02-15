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

            // 1. Obsługa czyszczenia
            if (rawInput.Equals("cls", System.StringComparison.OrdinalIgnoreCase) ||
                rawInput.Equals("clear", System.StringComparison.OrdinalIgnoreCase))
            {
                ClearLog();
                svnUI.TerminalInputField.text = "";
                return;
            }

            // 2. Historia
            if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != rawInput)
                commandHistory.Add(rawInput);
            historyIndex = -1;

            // 3. Wycinanie "svn " - bezpieczniejszy sposób
            string cmdToExecute = rawInput;
            if (rawInput.StartsWith("svn ", System.StringComparison.OrdinalIgnoreCase))
            {
                cmdToExecute = rawInput.Substring(4).Trim();
            }

            // 4. Dodaj wymuszenie braku interakcji dla bezpieczeństwa
            if (!cmdToExecute.Contains("--non-interactive"))
            {
                cmdToExecute += " --non-interactive";
            }

            svnUI.TerminalInputField.text = "";
            SVNLogBridge.LogLine($"<color=#FFFF00>> svn {cmdToExecute}</color>", append: true);

            IsProcessing = true;
            try
            {
                // Wykonanie
                string result = await SvnRunner.RunAsync(cmdToExecute, svnManager.WorkingDir);

                if (!string.IsNullOrEmpty(result))
                    SVNLogBridge.LogLine(result, append: true);
                else
                    SVNLogBridge.LogLine("<color=#777777>Command completed with no output.</color>", append: true);

                // Odświeżanie UI po zmianach
                if (cmdToExecute.Contains("update") || cmdToExecute.Contains("commit") ||
                    cmdToExecute.Contains("revert") || cmdToExecute.Contains("cleanup"))
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
            SVNLogBridge.LogLine("", append: false);
        }
    }
}