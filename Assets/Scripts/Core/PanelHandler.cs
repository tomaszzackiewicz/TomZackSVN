using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class PanelHandler : MonoBehaviour
    {
        [SerializeField] private GameObject helpPanel = null;
        [SerializeField] private GameObject resolvePanel = null;
        [SerializeField] private GameObject settingsPanel = null;
        [SerializeField] private GameObject branchPanel = null;
        [SerializeField] private GameObject mergePanel = null;
        [SerializeField] private GameObject commitPanel = null;
        [SerializeField] private GameObject checkoutPanel = null;
        [SerializeField] private GameObject loadPanel = null;
        [SerializeField] private GameObject projectSelectionPanel = null;
        [SerializeField] private GameObject ignoredPanel = null;
        [SerializeField] private GameObject shelvePanel = null;
        [SerializeField] private GameObject stealPanel = null;
        [SerializeField] private GameObject diffPanel = null;
        [SerializeField] private GameObject blamePanel = null;
        [SerializeField] private GameObject revGraphPanel = null;
        [SerializeField] private GameObject cleanPanel = null;
        [SerializeField] private GameObject lockPanel = null;
        [SerializeField] private GameObject revisionPanel = null;

        private SVNUI svnUI;
        private SVNManager svnManager;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;

            ResetAllPanels();
            StartCoroutine(PrepareRevGraphOnStart());
        }

        private IEnumerator PrepareRevGraphOnStart()
        {
            yield return null;
            yield return null;

            if (revGraphPanel == null) yield break;

            bool wasActive = revGraphPanel.activeSelf;
            revGraphPanel.SetActive(true);

            Canvas.ForceUpdateCanvases();
            var rt = revGraphPanel.GetComponent<RectTransform>();
            if (rt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            revGraphPanel.SetActive(wasActive);
        }

        private void ResetAllPanels()
        {
            Button_CloseHelp();
            Button_CloseResolve();
            Button_CloseSettings();
            Button_CloseBranch();
            Button_CloseMerge();
            Button_CloseCommit();
            Button_CloseCheckout();
            Button_CloseLoad();
            Button_CloseProjectSelection();
            Button_CloseIgnored();
            Button_CloseShelve();
            Button_CloseSteal();
            Button_CloseDiff();
            Button_CloseBlame();
            Button_CloseRevGraph();
            Button_CloseClean();
            Button_CloseLock();
            Button_CloseRevision();
        }

        // ============================================================
        //  REV GRAPH
        // ============================================================
        public void Button_OpenRevGraph()
        {
            ResetAllPanels();

            if (revGraphPanel == null) return;

            revGraphPanel.SetActive(true);

            Canvas.ForceUpdateCanvases();
            var rt = revGraphPanel.GetComponent<RectTransform>();
            if (rt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        public void Button_CloseRevGraph()
        {
            if (revGraphPanel != null)
                revGraphPanel.SetActive(false);
        }

        // ============================================================
        //  HELP
        // ============================================================
        public void Button_OpenHelp()
        {
            ResetAllPanels();
            if (helpPanel != null) helpPanel.SetActive(true);
        }

        public void Button_CloseHelp()
        {
            if (helpPanel != null) helpPanel.SetActive(false);
        }

        // ============================================================
        //  RESOLVE
        // ============================================================
        public void Button_OpenResolve()
        {
            ResetAllPanels();
            if (resolvePanel != null)
            {
                resolvePanel.SetActive(true);
                SVNManager.Instance?.GetModule<SVNResolve>()?.AutoRefreshConflictList();
            }
        }

        public void Button_CloseResolve()
        {
            if (resolvePanel != null)
                resolvePanel.SetActive(false);
        }

        // ============================================================
        //  SETTINGS
        // ============================================================
        public void Button_OpenSettings()
        {
            ResetAllPanels();
            if (settingsPanel != null) settingsPanel.SetActive(true);
        }

        public void Button_CloseSettings()
        {
            if (settingsPanel != null)
                settingsPanel.SetActive(false);
        }

        // ============================================================
        //  BRANCH
        // ============================================================
        public void Button_OpenBranch()
        {
            ResetAllPanels();
            if (branchPanel != null) branchPanel.SetActive(true);
        }

        public void Button_CloseBranch()
        {
            if (branchPanel != null)
                branchPanel.SetActive(false);
        }

        // ============================================================
        //  MERGE
        // ============================================================
        public void Button_OpenMerge()
        {
            ResetAllPanels();
            if (mergePanel != null) mergePanel.SetActive(true);
        }

        public void Button_CloseMerge()
        {
            if (mergePanel != null)
                mergePanel.SetActive(false);
        }

        // ============================================================
        //  COMMIT
        // ============================================================
        public void Button_OpenCommit()
        {
            ResetAllPanels();
            if (commitPanel != null) commitPanel.SetActive(true);
            svnManager?.GetModule<SVNStatus>()?.ShowOnlyModified();
        }

        public void Button_CloseCommit()
        {
            if (commitPanel != null)
                commitPanel.SetActive(false);

            // === FIX Ś1: zwykłe czyszczenie .text (jesteśmy na main — klik przycisku);
            // UpdateUIField z "" robiło Task.Run(logowania pustego stringu + dispatchera).
            if (svnUI != null && svnUI.CommitConsoleContent != null)
            {
                svnUI.CommitConsoleContent.text = "";
            }
        }

        // ============================================================
        //  CHECKOUT
        // ============================================================
        public void Button_OpenCheckout()
        {
            ResetAllPanels();
            if (checkoutPanel != null) checkoutPanel.SetActive(true);
        }

        public void Button_CloseCheckout()
        {
            if (checkoutPanel != null)
                checkoutPanel.SetActive(false);
        }

        // ============================================================
        //  LOAD
        // ============================================================
        public void Button_OpenLoad()
        {
            ResetAllPanels();
            if (loadPanel != null) loadPanel.SetActive(true);
        }

        public void Button_CloseLoad()
        {
            if (loadPanel != null)
                loadPanel.SetActive(false);
        }

        // ============================================================
        //  PROJECT SELECTION
        // ============================================================
        // === FIX K1: (1) svnManager NULL-safe (pole z Start() — klik może przyjść
        // wcześniej lub obiekt bywa włączany dynamicznie); (2) JEDNO źródło prawdy —
        // lokalne pole [SerializeField]; property managera tylko jako fallback,
        // gdy pole niepodpięte; (3) RefreshList tylko na istniejącym komponencie.
        public void Button_OpenProjectSelection()
        {
            ResetAllPanels();

            var manager = svnManager ?? SVNManager.Instance;
            var panelComponent = manager != null ? manager.ProjectSelectionPanel : null;

            // Priorytet: lokalne pole; fallback: panel z managera.
            GameObject panelObject = projectSelectionPanel;
            if (panelObject == null && panelComponent != null)
                panelObject = panelComponent.gameObject;

            if (panelObject != null)
                panelObject.SetActive(true);

            panelComponent?.RefreshList();
        }

        public void Button_CloseProjectSelection()
        {
            // === FIX K1: zamykamy to samo, co otwieramy (jedno źródło prawdy).
            var manager = svnManager ?? SVNManager.Instance;
            var panelComponent = manager != null ? manager.ProjectSelectionPanel : null;

            if (projectSelectionPanel != null)
                projectSelectionPanel.SetActive(false);
            else if (panelComponent != null)
                panelComponent.gameObject.SetActive(false);
        }

        // ============================================================
        //  IGNORED
        // ============================================================
        public void Button_OpenIgnored()
        {
            ResetAllPanels();
            if (ignoredPanel != null) ignoredPanel.SetActive(true);
        }

        public void Button_CloseIgnored()
        {
            if (ignoredPanel != null)
                ignoredPanel.SetActive(false);
        }

        // ============================================================
        //  SHELVE
        // ============================================================
        public void Button_OpenShelve()
        {
            ResetAllPanels();
            if (shelvePanel != null)
            {
                shelvePanel.SetActive(true);
                (svnManager ?? SVNManager.Instance)?.GetModule<SVNShelve>()?.RefreshShelvesUI();
            }
        }

        public void Button_CloseShelve()
        {
            if (shelvePanel != null)
                shelvePanel.SetActive(false);
        }

        // ============================================================
        //  STEAL
        // ============================================================
        public void Button_OpenSteal()
        {
            ResetAllPanels();
            if (stealPanel != null) stealPanel.SetActive(true);
        }

        public void Button_CloseSteal()
        {
            if (stealPanel != null)
                stealPanel.SetActive(false);
        }

        // ============================================================
        //  DIFF
        // ============================================================
        public void Button_OpenDiff()
        {
            ResetAllPanels();
            if (diffPanel != null) diffPanel.SetActive(true);
        }

        public void Button_CloseDiff()
        {
            if (diffPanel != null)
                diffPanel.SetActive(false);
        }

        // ============================================================
        //  BLAME
        // ============================================================
        public void Button_OpenBlame()
        {
            ResetAllPanels();
            if (blamePanel != null) blamePanel.SetActive(true);
        }

        public void Button_CloseBlame()
        {
            if (blamePanel != null)
                blamePanel.SetActive(false);
        }

        // ============================================================
        //  CLEAN
        // ============================================================
        public void Button_OpenClean()
        {
            ResetAllPanels();
            if (cleanPanel != null) cleanPanel.SetActive(true);
        }

        public void Button_CloseClean()
        {
            if (cleanPanel != null)
                cleanPanel.SetActive(false);
        }

        // ============================================================
        //  LOCK
        // ============================================================
        public void Button_OpenLock()
        {
            ResetAllPanels();
            if (lockPanel != null) lockPanel.SetActive(true);
        }

        public void Button_CloseLock()
        {
            if (lockPanel != null)
                lockPanel.SetActive(false);
        }

        // ============================================================
        //  REVISION
        // ============================================================
        public void Button_OpenRevision()
        {
            ResetAllPanels();
            if (revisionPanel != null) revisionPanel.SetActive(true);
        }

        public void Button_CloseRevision()
        {
            if (revisionPanel != null)
                revisionPanel.SetActive(false);
        }

        // ============================================================
        //  EXIT
        // ============================================================
        public void Button_Exit()
        {
            // === FIX Ś2: Application.Quit() w edytorze nic nie robi (warning w
            // nowszych wersjach) — explicite stop play mode.
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}