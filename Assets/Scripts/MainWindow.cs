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
                svnManager.SVNTerminal.ExecuteTerminalCommand();
            }
        }

        public void Button_Load() => svnManager.SVNLoad.LoadRepoPathAndRefresh();
        public void Button_Update() => svnManager.SVNUpdate.Update();
        public void Button_Refresh() => svnManager.SVNStatus.RefreshLocal();
        public void Button_Clean() => svnManager.SVNClean.LightCleanup();
        public void Button_Vacuum() => svnManager.SVNClean.VacuumCleanup();
        public void Button_Log() => svnManager.SVNLog.ShowLog();
        public void Button_ShowModified() => svnManager.SVNStatus.ShowOnlyModified();
        public void Button_ShowOnlyIgnored() => svnManager.SVNStatus.ShowOnlyIgnored();
        public void Button_CollapseAll() => svnManager.SVNStatus.CollapseAll();
        public void Button_Explore() => svnManager.SVNExternal.OpenInExplorer();
        public void Button_Lock() => svnManager.SVNLock.LockModified();
        public void Button_Unlock() => svnManager.SVNLock.UnlockAll();
        public void Button_ShowToCommit() => svnManager.SVNCommit.ShowWhatWillBeCommitted();
        public void Button_ShowLocks() => svnManager.SVNLock.ShowAllLocks();
        public void Button_BreakLocks() => svnManager.SVNLock.BreakAllLocks();
        public void Button_TerminalSubmit() => OnTerminalSubmit();

        public void Button_ClearTerminalLog()
        {
            svnManager.SVNTerminal.ClearLog();
            svnUI.TerminalInputField.ActivateInputField();
        }

        public void Button_ClearLocksView()
        {
            if (svnUI.LocksText != null)
            {
                svnUI.LocksText.text = "";
            }
        }
    }
}
