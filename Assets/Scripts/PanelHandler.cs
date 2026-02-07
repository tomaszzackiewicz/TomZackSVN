using System.Collections.Generic;
using UnityEngine;

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

        private SVNUI svnUI;
        private SVNManager svnManager;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;

            ResetAllPanels();
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
        }

        public void Button_OpenHelp()
        {
            ResetAllPanels();

            if (helpPanel != null)
            {
                helpPanel.SetActive(true);
            }
        }

        public void Button_CloseHelp()
        {
            if (helpPanel != null)
            {
                helpPanel.SetActive(false);
            }
        }

        public void Button_OpenResolve()
        {
            ResetAllPanels();

            if (resolvePanel != null)
            {
                resolvePanel.SetActive(true);
            }
        }

        public void Button_CloseResolve()
        {
            if (resolvePanel != null)
            {
                resolvePanel.SetActive(false);
            }
        }

        public void Button_OpenSettings()
        {
            ResetAllPanels();

            if (settingsPanel != null)
            {
                settingsPanel.SetActive(true);
            }
        }

        public void Button_CloseSettings()
        {
            if (settingsPanel != null)
            {
                settingsPanel.SetActive(false);
            }
        }

        public void Button_OpenBranch()
        {
            ResetAllPanels();

            if (branchPanel != null)
            {
                branchPanel.SetActive(true);
            }
        }

        public void Button_CloseBranch()
        {
            if (branchPanel != null)
            {
                branchPanel.SetActive(false);
            }
        }

        public void Button_OpenMerge()
        {
            ResetAllPanels();

            if (mergePanel != null)
            {
                mergePanel.SetActive(true);
            }
        }

        public void Button_CloseMerge()
        {
            if (mergePanel != null)
            {
                mergePanel.SetActive(false);
            }
        }

        public void Button_OpenCommit()
        {
            ResetAllPanels();

            if (commitPanel != null)
            {
                commitPanel.SetActive(true);
            }

            svnManager.SVNStatus.ShowOnlyModified();
        }

        public void Button_CloseCommit()
        {
            if (commitPanel != null)
            {
                commitPanel.SetActive(false);
            }
        }

        public void Button_OpenCheckout()
        {
            ResetAllPanels();

            if (checkoutPanel != null)
            {
                checkoutPanel.SetActive(true);
            }
        }

        public void Button_CloseCheckout()
        {
            if (checkoutPanel != null)
            {
                checkoutPanel.SetActive(false);
            }
        }

        public void Button_OpenLoad()
        {
            ResetAllPanels();

            if (loadPanel != null)
            {
                loadPanel.SetActive(true);
            }
        }

        public void Button_CloseLoad()
        {
            if (loadPanel != null)
            {
                loadPanel.SetActive(false);
            }
        }

        public void Button_OpenProjectSelection()
        {
            ResetAllPanels();

            if (projectSelectionPanel != null)
            {
                //svnManager.MainUIPanel.SetActive(true);
                svnManager.ProjectSelectionPanel.gameObject.SetActive(true);
                svnManager.ProjectSelectionPanel.RefreshList();
                projectSelectionPanel.SetActive(true);
            }
        }

        public void Button_CloseProjectSelection()
        {
            if (projectSelectionPanel != null)
            {
                projectSelectionPanel.SetActive(false);
            }
        }

        public void Button_OpenIgnored()
        {
            ResetAllPanels();

            if (ignoredPanel != null)
            {
                ignoredPanel.SetActive(true);
            }
        }

        public void Button_CloseIgnored()
        {
            if (ignoredPanel != null)
            {
                ignoredPanel.SetActive(false);
            }
        }

        public void Button_Exit()
        {
            Application.Quit();
        }
    }

    
}