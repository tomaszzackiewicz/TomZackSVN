using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

namespace SVN.Core
{
    public class MainWindow : MonoBehaviour
    {
        private SVNUI svnUI = null;
        private SVNManager svnManager = null;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;

            if (svnUI.TerminalInputField != null)
            {
                svnUI.TerminalInputField.onEndEdit.RemoveAllListeners();

                svnUI.TerminalInputField.onEndEdit.AddListener((val) =>
                {
                    if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    {
                        ExecuteLogic();
                    }
                });
            }
        }

        void OnTerminalSubmit()
        {
            ExecuteLogic();
        }


        public void ExecuteCommand()
        {
            ExecuteLogic();
        }

        void Update()
        {
            if (svnUI.TerminalInputField.isFocused && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                ExecuteLogic();
            }
        }

        private void ExecuteLogic()
        {
            string cmd = svnUI.TerminalInputField.text;

            if (string.IsNullOrWhiteSpace(cmd)) return;

            var terminal = svnManager.GetModule<SVNTerminal>();
            if (terminal != null)
            {
                terminal.ExecuteTerminalCommand();
            }

            svnUI.TerminalInputField.text = "";

            svnUI.TerminalInputField.ActivateInputField();
        }

        public void Button_Load() => svnManager.GetModule<SVNLoad>().LoadRepoPathAndRefresh();
        public void Button_Update() => svnManager.GetModule<SVNUpdate>().Update();
        public void Button_Refresh() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
        public void Button_Clean() => svnManager.GetModule<SVNClean>().LightCleanup();
        public void Button_DiscardUntracked() => svnManager.GetModule<SVNClean>().DiscardUnversioned();
        public void Button_Vacuum() => svnManager.GetModule<SVNClean>().VacuumCleanup();
        public void Button_DeepRepair() => svnManager.GetModule<SVNClean>().DeepRepair();
        public void Button_Log() => svnManager.GetModule<SVNLog>().ShowLog();
        public void Button_RevertAllMissing() => svnManager.GetModule<SVNCommit>().ExecuteRevertAllMissing();
        public void Button_ShowOnlyIgnored() => svnManager.GetModule<SVNIgnore>().RefreshIgnoredPanel();
        public void Button_Explore() => svnManager.GetModule<SVNExternal>().OpenInExplorer();
        public void Button_Lock() => svnManager.GetModule<SVNLock>().LockModified();
        public void Button_Unlock() => svnManager.GetModule<SVNLock>().UnlockAll();
        public void Button_ShowToCommit() => svnManager.GetModule<SVNCommit>().ShowWhatWillBeCommitted();
        public void Button_ShowLocks() => svnManager.GetModule<SVNLock>().ShowAllLocks();
        public void Button_BreakLocks() => svnManager.GetModule<SVNLock>().BreakAllLocks();
        public void Button_TerminalSubmit() => ExecuteCommand();

        public void Button_CheckRemoteModifications() => svnManager.GetModule<SVNUpdate>().CheckRemoteModifications();

        public void Button_OpenLogs() => SVNLogger.OpenLogFolder();

        public void Button_ClearTerminalLog()
        {
            var terminal = svnManager.GetModule<SVNTerminal>();
            if (terminal != null)
            {
                terminal.ClearLog();
            }

            if (svnUI.TerminalInputField != null)
            {
                svnUI.TerminalInputField.text = "";
                svnUI.TerminalInputField.ActivateInputField();
            }
        }

        public void Button_ClearLocksView()
        {
            if (svnUI.LocksText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.LocksText, string.Empty, "LOCKS_VIEW", append: false);

                SVNLogBridge.LogLine("<color=#777777>Locks view cleared by user.</color>");
            }
        }
    }
}
