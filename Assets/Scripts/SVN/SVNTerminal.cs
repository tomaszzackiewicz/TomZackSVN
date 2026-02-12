using UnityEngine;
using UnityEngine.UI;
using System.Collections;
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
            LogToView($"> svn {cmd}", "#FFFF00");

            IsProcessing = true;

            try
            {
                string result = await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                LogToView(result, "white");

                if (cmd.Contains("update") || cmd.Contains("commit") || cmd.Contains("switch") || cmd.Contains("checkout"))
                {
                    await svnManager.RefreshStatus();
                }
            }
            catch (System.Exception ex)
            {
                LogToView(ex.Message, "red");
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

        private void LogToView(string message, string colorTag)
        {
            if (svnUI.LogText == null) return;

            string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
            svnUI.LogText.text += $"[{timestamp}] <color={colorTag}>{message}</color>\n";

            svnManager.StartCoroutine(ScrollToBottom());
        }

        private IEnumerator ScrollToBottom()
        {
            yield return new WaitForEndOfFrame();
            if (svnUI.LogScrollRect != null)
            {
                svnUI.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        public void ClearLog()
        {
            if (svnUI.LogText != null)
            {
                svnUI.LogText.text = "";
                LogToView("Terminal log cleared.", "#888888");
            }
        }
    }
}