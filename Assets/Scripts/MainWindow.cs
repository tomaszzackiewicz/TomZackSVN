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
                svnUI.TerminalInputField.onEndEdit.AddListener(delegate { OnTerminalSubmit(); });
            }
        }

        void OnTerminalSubmit()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                svnManager.GetModule<SVNTerminal>().ExecuteTerminalCommand();
            }
        }

        public void Button_Load() => svnManager.GetModule<SVNLoad>().LoadRepoPathAndRefresh();
        public void Button_Update() => svnManager.GetModule<SVNUpdate>().Update();
        public void Button_Refresh() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
        public void Button_Clean() => svnManager.GetModule<SVNClean>().LightCleanup();
        public void Button_DiscardUntracked() => svnManager.GetModule<SVNClean>().DiscardUnversioned();
        public void Button_Vacuum() => svnManager.GetModule<SVNClean>().VacuumCleanup();
        public void Button_Log() => svnManager.GetModule<SVNLog>().ShowLog();
        public void Button_RevertAllMissing() => svnManager.GetModule<SVNCommit>().ExecuteRevertAllMissing();
        public void Button_ShowOnlyIgnored() => svnManager.GetModule<SVNStatus>().RefreshIgnoredPanel();
        public void Button_Explore() => svnManager.GetModule<SVNExternal>().OpenInExplorer();
        public void Button_Lock() => svnManager.GetModule<SVNLock>().LockModified();
        public void Button_Unlock() => svnManager.GetModule<SVNLock>().UnlockAll();
        public void Button_ShowToCommit() => svnManager.GetModule<SVNCommit>().ShowWhatWillBeCommitted();
        public void Button_ShowLocks() => svnManager.GetModule<SVNLock>().ShowAllLocks();
        public void Button_BreakLocks() => svnManager.GetModule<SVNLock>().BreakAllLocks();
        public void Button_TerminalSubmit() => OnTerminalSubmit();

        public void Button_ClearTerminalLog()
        {
            svnManager.GetModule<SVNTerminal>().ClearLog();
            svnUI.TerminalInputField.ActivateInputField();
        }

        public void Button_ClearLocksView()
        {
            if (svnUI.LocksText != null)
            {
                svnUI.LocksText.text = string.Empty;
            }
        }
    }
}
