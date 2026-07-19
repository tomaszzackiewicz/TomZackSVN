using System;
using UnityEngine;

namespace SVN.Core
{
    public class MainWindow : MonoBehaviour
    {
        private SVNUI svnUI;
        private SVNManager svnManager;
        private SVNTerminal terminal;

        private float _lastUpdateToRevClickTime;
        private const float DoubleClickThreshold = 5.0f;
        private string _pendingRevision = null;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;
            terminal = svnManager?.GetModule<SVNTerminal>();
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

        public void ExecuteTerminalCommand()
        {
            terminal?.ExecuteTerminalCommand();
        }

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
        public void Button_Lock() => svnManager.GetModule<SVNLock>().LockModifiedButton();
        public void Button_Unlock() => svnManager.GetModule<SVNLock>().UnlockAllButton();
        public void Button_ShowToCommit() => svnManager.GetModule<SVNCommit>().ShowWhatWillBeCommitted();
        public void Button_ShowLocks() => svnManager.GetModule<SVNLock>().ShowAllLocksButton();
        public void Button_BreakLocks() => svnManager.GetModule<SVNLock>().BreakAllLocksButton();
        public void Button_CheckRemoteModifications() => svnManager.GetModule<SVNUpdate>().CheckRemoteModificationsButton();
        public void Button_OpenLogs() => SVNLogger.OpenLogFolder();
        public void Button_ClearLocksView()
        {
            if (svnUI.LocksText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.LocksText, string.Empty, "LOCKS_VIEW", append: false);
                SVNLogBridge.LogLine("<color=#777777>Locks view cleared.</color>");
            }
        }
        public void Button_TestConnection() => svnManager.GetModule<SVNExternal>().TestConnection();

        public async void Button_UpdateToRevision()
        {
            string rev = svnUI.UpdateRevisionInput?.text?.Trim();

            if (string.IsNullOrWhiteSpace(rev))
            {
                svnManager.GetModule<SVNUpdate>().Update();
                return;
            }

            var updateModule = svnManager.GetModule<SVNUpdate>();

            bool hasModifications = await updateModule.HasLocalModificationsAsync(svnManager.WorkingDir);
            if (hasModifications)
            {
                SVNLogBridge.LogLine(
                    "<color=#FFAA00>Cannot update to a specific revision while you have uncommitted local changes. " +
                    "Please commit or revert them first.</color>",
                    append: true);
                return;
            }

            float timeSinceLastClick = Time.time - _lastUpdateToRevClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevision == rev)
            {
                _pendingRevision = null;
                updateModule.UpdateToRevision(rev);
            }
            else
            {
                _lastUpdateToRevClickTime = Time.time;
                _pendingRevision = rev;
                SVNLogBridge.LogLine(
                    $"<color=#FFAA00>Click again within 5 seconds to confirm update to revision {rev}. " +
                    "This will overwrite local files.</color>",
                    append: true);
            }
        }
    }
}