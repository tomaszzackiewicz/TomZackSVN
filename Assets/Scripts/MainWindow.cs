using System.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class MainWindow : MonoBehaviour
    {
        private SVNUI svnUI;
        private SVNManager svnManager;
        private SVNTerminal terminal;

        private bool isExpanded = false;

        private void OnEnable()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;
            terminal = svnManager?.GetModule<SVNTerminal>();
        }

        private void Start()
        {
            terminal?.SetInputField(svnUI?.TerminalInputField);

            if (svnUI?.TerminalInputField != null)
            {
                svnUI.TerminalInputField.onEndEdit.RemoveAllListeners();
                svnUI.TerminalInputField.onEndEdit.AddListener(_ =>
                {
                    if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                        ExecuteTerminalCommand();
                });
            }
        }

        public void ExecuteCommand() => ExecuteTerminalCommand();

        private void Update()
        {
            if (svnUI?.TerminalInputField != null &&
                svnUI.TerminalInputField.isFocused &&
                (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                ExecuteTerminalCommand();
            }
        }

        public void Button_ToggleExpand()
        {
            var statusModule = svnManager.GetModule<SVNStatus>();
            var data = statusModule.GetCurrentData();

            bool anyExpanded = data != null && data.Any(e => e.IsFolder && e.IsExpanded);

            if (anyExpanded)
            {
                statusModule.CollapseAll();
                isExpanded = false;
            }
            else
            {
                statusModule.ExpandAll();
                isExpanded = true;
            }
        }

        public void ExecuteTerminalCommand() => terminal?.ExecuteTerminalCommand();

        public void Button_TerminalSubmit() => ExecuteTerminalCommand();

        public void Button_CancelTerminalCommand()
        {
            terminal?.Cancel();
            if (svnUI?.TerminalInputField != null)
            {
                svnUI.TerminalInputField.text = "";
                svnUI.TerminalInputField.ActivateInputField();
            }
        }

        public void Button_ClearTerminalLog()
        {
            terminal?.ClearLog();
            if (svnUI?.TerminalInputField != null)
            {
                svnUI.TerminalInputField.text = "";
                svnUI.TerminalInputField.ActivateInputField();
            }
        }

        public void Button_Load() => svnManager.GetModule<SVNLoad>().LoadRepoPathAndRefresh();
        public void Button_Update() => svnManager.GetModule<SVNUpdate>().Update();
        public void Button_CancelUpdate() => svnManager.GetModule<SVNUpdate>().CancelUpdate();
        public void Button_Refresh() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
        public void Button_Log() => svnManager.GetModule<SVNLog>().ShowLog();
        public void Button_RevertAllMissing() => svnManager.GetModule<SVNCommit>().ExecuteRevertAllMissing();
        public void Button_ShowOnlyIgnored() => svnManager.GetModule<SVNIgnore>().RefreshIgnoredPanel();
        public void Button_Explore() => svnManager.GetModule<SVNExternal>().OpenInExplorer();
        public void Button_ShowToCommit() => svnManager.GetModule<SVNCommit>().ShowWhatWillBeCommitted();
        public void Button_CheckRemoteModifications() => svnManager.GetModule<SVNUpdate>().CheckRemoteModificationsButton();
        public void Button_OpenLogs() => SVNLogger.OpenLogFolder();
        public void Button_Revert() => svnManager.GetModule<SVNRevert>().RevertAll();
        public void Button_CancelRevert() => svnManager.GetModule<SVNRevert>().CancelRevert();
        public void Button_Add() => svnManager.GetModule<SVNAdd>().AddAll();
        public void Button_AddSelected() => svnManager.GetModule<SVNAdd>().AddSelected();
        public void Button_FixMissing() => svnManager.GetModule<SVNMissing>().FixMissingFiles();
        public void Button_DiscardUntracked() => svnManager.GetModule<SVNClean>().DiscardUnversioned();
        public void Button_GoUpRepoBrowser() => svnManager.GetModule<SVNRepoBrowser>().GoUp();
        public void Button_CollapsAll() => svnManager.GetModule<SVNRepoBrowser>().CollapseAllToRoot();
        public void Button_TestConnection() => svnManager.GetModule<SVNExternal>().TestConnection();
    }
}