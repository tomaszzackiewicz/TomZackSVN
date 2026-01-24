using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

namespace SVN.Core
{
    public class MainWindow : MonoBehaviour
    {
        private SVNUI svnUI = null;
        private SVNManager svnManager = null;

        private SVNCheckout checkout;
        private SVNLoad load;
        private SVNUpdate update;
        private SVNStatus status;
        private SVNCommit commit;
        private SVNSettings settings;
        private SVNRevert revert;
        private SVNClean clean;
        private SVNLog log;
        private SVNExternal external;
        private SVNLock svnLock;
        private SVNTerminal terminal;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;

            checkout = new SVNCheckout(svnUI, svnManager);
            load = new SVNLoad(svnUI, svnManager);
            update = new SVNUpdate(svnUI, svnManager);
            status = new SVNStatus(svnUI, svnManager);
            commit = new SVNCommit(svnUI, svnManager);
            revert = new SVNRevert(svnUI, svnManager);
            clean = new SVNClean(svnUI, svnManager);
            log = new SVNLog(svnUI, svnManager);
            external = new SVNExternal(svnUI, svnManager);
            svnLock = new SVNLock(svnUI, svnManager);
            settings = new SVNSettings(svnUI, svnManager);
            terminal = new SVNTerminal(svnUI, svnManager);

            if (svnUI.TerminalInputField != null)
            {
                svnUI.TerminalInputField.onEndEdit.AddListener(delegate { OnTerminalSubmit(); });
            }
        }

        void OnTerminalSubmit()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                terminal.ExecuteTerminalCommand();
            }
        }

        //public void Button_Checkout() => checkout.Checkout();
        public void Button_Load() => load.LoadRepoPathAndRefresh();
        public void Button_Update() => update.Update();
        public void Button_Refresh() => status.RefreshLocal();
        public void Button_Commit() => commit.CommitAll();
        public void Button_Revert() => revert.RevertAll();
        public void Button_Clean() => clean.LightCleanup();
        public void Button_Vacuum() => clean.VacuumCleanup();
        public void Button_Log() => log.ShowLog();
        public void Button_ShowModified() => status.ShowOnlyModified();
        public void Button_ShowOnlyIgnored() => status.ShowOnlyIgnored();
        public void Button_CollapseAll() => status.CollapseAll();
        public void Button_Explore() => external.OpenInExplorer();
        public void Button_Lock() => svnLock.LockModified();
        public void Button_Unlock() => svnLock.UnlockAll();
        public void Button_ShowToCommit() => commit.ShowWhatWillBeCommitted();
        public void Button_ShowLocks() => svnLock.ShowAllLocks();
        public void Button_BreakLocks() => svnLock.BreakAllLocks();
        public void Button_TerminalSubmit() => OnTerminalSubmit();

        public void Button_ClearTerminalLog()
        {
            terminal.ClearLog();
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
