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

                // === FIX K1: JEDNO źródło prawdy dla Enter. Wcześniej onEndEdit
                // (z kruchym Input.GetKeyDown w środku — onEndEdit odpala po Input
                // w tej samej klatce, GetKeyDown często już false) + Update() z
                // isFocused (focus znika dopiero następną klatkę) = komenda
                // wykonywana PODWÓJNIE w jednej klatce (2× svn add/commit!).
                // TMP ma onSubmit — wołane TYLKO przy Enter, bez Input-API.
                svnUI.TerminalInputField.onSubmit.AddListener(_ => ExecuteTerminalCommand());
            }
        }

        public void ExecuteCommand() => ExecuteTerminalCommand();

        // === FIX K3: Update usunięte —jego rolę (Enter) przejął onSubmit;
        // dodatkowo legacy Input.GetKeyDown rzucał InvalidOperationException
        // co klatkę na projektach z nowym Input System.

        // === FIX K2: null-safe routingi — GetModule zwraca null przy częściowej
        // porażce InitializeAllModules; wcześniej NRE na klik.
        private T Module<T>() where T : SVNBase
        {
            if (svnManager == null) svnManager = SVNManager.Instance;
            var m = svnManager?.GetModule<T>();
            if (m == null)
                SVNLogBridge.LogError($"[MainWindow] {typeof(T).Name} module is not available.");
            return m;
        }

        public void Button_ToggleExpand()
        {
            var statusModule = Module<SVNStatus>();
            if (statusModule == null) return;

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

        public void ExecuteTerminalCommand()
        {
            if (terminal == null) terminal = svnManager?.GetModule<SVNTerminal>() ?? SVNManager.Instance?.GetModule<SVNTerminal>();
            terminal?.ExecuteTerminalCommand();
        }

        public void Button_TerminalSubmit() => ExecuteTerminalCommand();

        public void Button_CancelTerminalCommand()
        {
            terminal?.Cancel();
            FocusTerminalInput();
        }

        public void Button_ClearTerminalLog()
        {
            terminal?.ClearLog();
            FocusTerminalInput();
        }

        private void FocusTerminalInput()
        {
            if (svnUI?.TerminalInputField != null)
            {
                svnUI.TerminalInputField.text = "";
                svnUI.TerminalInputField.ActivateInputField();
            }
        }

        public void Button_ClearMainLog()
        {
            SVNLogBridge.ClearConsole();
        }

        public void Button_ClearAllLogs()
        {
            SVNLogBridge.ClearConsole();
            terminal?.ClearLog();

            if (svnUI?.ResolveLogConsole != null)
                SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, "", "RESOLVE", false);

            FocusTerminalInput();
        }

        public void Button_Load() { var m = Module<SVNLoad>(); m?.LoadRepoPathAndRefresh(); }
        public void Button_Update() { var m = Module<SVNUpdate>(); m?.Update(); }
        public void Button_CancelUpdate() { var m = Module<SVNUpdate>(); m?.CancelUpdate(); }
        public void Button_Refresh() { var m = Module<SVNStatus>(); m?.ShowOnlyModified(); }
        public void Button_Log() { var m = Module<SVNLog>(); m?.ShowLog(); }
        public void Button_RevertAllMissing() { var m = Module<SVNCommit>(); m?.ExecuteRevertAllMissing(); }
        public void Button_ShowOnlyIgnored() { var m = Module<SVNIgnore>(); m?.RefreshIgnoredPanel(); }
        public void Button_Explore() { var m = Module<SVNExternal>(); m?.OpenInExplorer(); }
        public void Button_ShowToCommit() { var m = Module<SVNCommit>(); m?.ShowWhatWillBeCommitted(); }
        public void Button_CheckRemoteModifications() { var m = Module<SVNUpdate>(); m?.CheckRemoteModificationsButton(); }
        public void Button_OpenLogs() => SVNLogger.OpenLogFolder();
        public void Button_Revert() { var m = Module<SVNRevert>(); m?.RevertAll(); }
        public void Button_CancelRevert() { var m = Module<SVNRevert>(); m?.CancelRevert(); }
        public void Button_Add() { var m = Module<SVNAdd>(); m?.AddAll(); }
        public void Button_AddSelected() { var m = Module<SVNAdd>(); m?.AddSelected(); }
        public void Button_FixMissing() { var m = Module<SVNMissing>(); m?.FixMissingFiles(); }
        public void Button_DiscardUntracked() { var m = Module<SVNClean>(); m?.DiscardUnversioned(); }
        public void Button_GoUpRepoBrowser() { var m = Module<SVNRepoBrowser>(); m?.GoUp(); }
        public void Button_CollapsAll() { var m = Module<SVNRepoBrowser>(); m?.CollapseAllToRoot(); }
        public void Button_TestConnection() { var m = Module<SVNExternal>(); m?.TestConnection(); }
        public void Button_TakeSnapshot() { var m = Module<SVNSnapshot>(); m?.ExecuteCreateSnapshot(); }
        public void Button_RestoreSnapshot() { var m = Module<SVNSnapshot>(); m?.ExecuteRestoreSnapshot(); }
        public void Button_DeleteSnapshot() { var m = Module<SVNSnapshot>(); m?.ExecuteDeleteSnapshot(); }
    }
}