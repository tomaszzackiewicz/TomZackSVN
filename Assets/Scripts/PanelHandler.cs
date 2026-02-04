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

        private SVNUI svnUI;
        private SVNManager svnManager;

        private void Start()
        {
            svnUI = SVNUI.Instance;
            svnManager = SVNManager.Instance;

            Button_CloseHelp();
        }

        public void Button_OpenHelp()
        {
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

        public void Button_Exit()
        {
            Application.Quit();
        }
    }

    [System.Serializable]
    public class SVNProject
    {
        public string projectName;
        public string repoUrl;
        public string workingDir;
        public string privateKeyPath;
        public System.DateTime lastOpened;
    }

    [System.Serializable]
    public class SVNProjectList
    {
        public List<SVNProject> projects = new List<SVNProject>();
    }
}