using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public class SVNManager : MonoBehaviour
    {
        public static SVNManager Instance { get; private set; }

        public const string KEY_WORKING_DIR = "SVN_Persisted_WorkingDir";
        public const string KEY_SSH_PATH = "SVN_Persisted_SSHKeyPath";
        public const string KEY_MERGE_TOOL = "SVN_Persisted_MergeTool";
        public const string KEY_REPO_URL = "SVN_Persisted_RepositoryURL";

        [SerializeField] private SVNUI svnUI = null;
        [SerializeField] private GameObject loadingOverlay = null;
        [SerializeField] private PanelHandler panelHandler = null;
        [SerializeField] private GameObject mainUIPanel;
        [SerializeField] private ProjectSelectionPanel projectSelectionPanel;

        private string currentUserName = "Unknown";
        public HashSet<string> expandedPaths = new HashSet<string>();
        private string workingDir = string.Empty;
        private string currentKey = string.Empty;
        private string mergeToolPath = string.Empty;
        private bool isProcessing = false;
        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; set; } = new Dictionary<string, (string status, string size)>();

        public SVNStatus SVNStatus { get; private set; }
        public SVNCommit SVNCommit { get; private set; }
        public SVNAdd SVNAdd { get; private set; }
        public SVNMissing SVNMissing { get; private set; }
        public SVNUpdate SVNUpdate { get; private set; }
        public SVNBranchTag SVNBranchTag { get; private set; }
        public SVNExternal SVNExternal { get; private set; }
        public SVNCheckout SVNCheckout { get; private set; }
        public SVNLoad SVNLoad { get; private set; }
        public SVNMerge SVNMerge { get; private set; }
        public SVNSettings SVNSettings { get; private set; }
        public SVNResolve SVNResolve { get; private set; }
        public SVNRevert SVNRevert { get; private set; }
        public SVNTerminal SVNTerminal { get; private set; }
        public SVNClean SVNClean { get; private set; }
        public SVNLog SVNLog { get; private set; }
        public SVNLock SVNLock { get; private set; }
        public SVNShelve SVNShelve { get; private set; }

        public string RepositoryUrl { get; set; } = string.Empty;
        public GameObject MainUIPanel => mainUIPanel;
        public PanelHandler PanelHandler => panelHandler;
        public ProjectSelectionPanel ProjectSelectionPanel => projectSelectionPanel;

        public string CurrentUserName
        {
            get
            {
                return currentUserName;
            }
        }

        public bool IsProcessing
        {
            get => isProcessing;
            set => isProcessing = value;
        }

        public string WorkingDir
        {
            get => workingDir;
            set
            {
                workingDir = value;
            }
        }

        public string CurrentKey
        {
            get => currentKey;
            set => currentKey = value;
        }

        public string MergeToolPath
        {
            get => mergeToolPath;
            set => mergeToolPath = value;
        }

        public HashSet<string> ExpandedPaths
        {
            get => expandedPaths;
            set => expandedPaths = value;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                gameObject.hideFlags = HideFlags.HideAndDontSave;
                this.enabled = false;
                DestroyImmediate(gameObject);
                return;
            }
            Instance = this;

            SVNStatus = new SVNStatus(svnUI, this);
            SVNCommit = new SVNCommit(svnUI, this);
            SVNAdd = new SVNAdd(svnUI, this);
            SVNMissing = new SVNMissing(svnUI, this);
            SVNUpdate = new SVNUpdate(svnUI, this);
            SVNBranchTag = new SVNBranchTag(svnUI, this);
            SVNExternal = new SVNExternal(svnUI, this);
            SVNCheckout = new SVNCheckout(svnUI, this);
            SVNLoad = new SVNLoad(svnUI, this);
            SVNMerge = new SVNMerge(svnUI, this);
            SVNSettings = new SVNSettings(svnUI, this);
            SVNResolve = new SVNResolve(svnUI, this);
            SVNRevert = new SVNRevert(svnUI, this);
            SVNTerminal = new SVNTerminal(svnUI, this);
            SVNClean = new SVNClean(svnUI, this);
            SVNLog = new SVNLog(svnUI, this);
            SVNLock = new SVNLock(svnUI, this);
            SVNShelve = new SVNShelve(svnUI, this);
        }

        private async void Start()
        {
            if (svnUI == null) return;

            SetupInputListeners();

            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            var allProjects = ProjectSettings.LoadProjects();
            var lastProject = allProjects.Find(p => p.workingDir == lastPath);

            if (lastProject != null)
            {
                SVNStatus.ShowProjectInfo(lastProject, lastPath);
                LoadProject(lastProject);

                if (projectSelectionPanel != null) projectSelectionPanel.gameObject.SetActive(false);
            }
            else
            {
                if (projectSelectionPanel != null)
                {
                    projectSelectionPanel.gameObject.SetActive(true);
                    projectSelectionPanel.RefreshList();
                }
            }
        }

        public async Task<string> AutoDetectSvnUser()
        {
            currentUserName = "Detecting...";

            if (string.IsNullOrEmpty(WorkingDir))
            {
                currentUserName = "Unknown";
                return currentUserName;
            }

            if (!Directory.Exists(Path.Combine(WorkingDir, ".svn")))
            {
                currentUserName = Environment.UserName.ToLower();
                return currentUserName;
            }

            try
            {
                string xmlOutput = await SvnRunner.RunAsync("info --xml", WorkingDir, false);
                if (!string.IsNullOrEmpty(xmlOutput))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(xmlOutput);

                    XmlNode urlNode = doc.SelectSingleNode("//url");
                    if (urlNode != null)
                    {
                        string fullUrl = urlNode.InnerText;
                        var match = System.Text.RegularExpressions.Regex.Match(fullUrl, @"://([^@/]+)@");
                        if (match.Success)
                        {
                            currentUserName = match.Groups[1].Value.Trim();
                            return currentUserName;
                        }
                    }
                }

                string authOutput = await SvnRunner.RunAsync("auth", WorkingDir, false);
                string[] lines = authOutput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Username:"))
                    {
                        currentUserName = line.Replace("Username:", "").Trim();
                        return currentUserName;
                    }
                }

                currentUserName = Environment.UserName.ToLower();
                return currentUserName;
            }
            catch (Exception ex)
            {
                currentUserName = Environment.UserName.ToLower();
                return currentUserName;
            }
        }

        public async Task SetWorkingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            workingDir = path;
            WorkingDir = path;

            Debug.Log($"[SVN] Working Directory set to: {path}");

            await RefreshRepositoryInfo();
        }

        public async Task RefreshRepositoryInfo()
        {
            if (string.IsNullOrEmpty(WorkingDir) || !Directory.Exists(WorkingDir)) return;

            try
            {
                string url = await SvnRunner.GetRepoUrlAsync(WorkingDir);

                if (!string.IsNullOrWhiteSpace(url) && !url.Contains("not a working copy"))
                {
                    this.RepositoryUrl = url.Trim();
                    Debug.Log($"[SVN] URL metadata synced: {RepositoryUrl}");

                    if (svnUI != null && svnUI.SettingsRepoUrlInput != null)
                    {
                        svnUI.SettingsRepoUrlInput.SetTextWithoutNotify(this.RepositoryUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SVN] Could not fetch URL from disk: " + ex.Message);
            }
        }

        private void SetupInputListeners()
        {
            svnUI.SettingsRepoUrlInput?.onValueChanged.AddListener(val =>
            {
                string cleaned = val.Trim();
                BroadcastUrlChange(cleaned);
                UpdateCurrentProjectData();
            });

            svnUI.SettingsSshKeyPathInput?.onValueChanged.AddListener(val =>
            {
                string cleaned = val.Trim();
                BroadcastSshKeyChange(cleaned);
                UpdateCurrentProjectData();
            });

            svnUI.SettingsWorkingDirInput?.onValueChanged.AddListener(val =>
            {
                string cleaned = val.Trim();
                BroadcastWorkingDirChange(cleaned);
                UpdateCurrentProjectData();
            });

            svnUI.SettingsMergeToolPathInput?.onValueChanged.AddListener(val =>
            {
                this.MergeToolPath = val.Trim();
                UpdateCurrentProjectData();
            });
        }

        private void UpdateCurrentProjectData()
        {
            List<SVNProject> projects = ProjectSettings.LoadProjects();
            var current = projects.Find(p => p.workingDir == this.WorkingDir);

            if (current != null)
            {
                current.repoUrl = this.RepositoryUrl;
                current.privateKeyPath = this.CurrentKey;
                current.mergeToolPath = this.MergeToolPath;

                ProjectSettings.SaveProjects(projects);
            }
        }

        public async void LoadProject(SVNProject project)
        {
            this.WorkingDir = project.workingDir;
            this.RepositoryUrl = project.repoUrl;
            this.CurrentKey = project.privateKeyPath;
            this.MergeToolPath = project.mergeToolPath;
            SvnRunner.KeyPath = this.CurrentKey;

            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(CurrentKey);
            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(MergeToolPath);

            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", project.workingDir);
            PlayerPrefs.Save();

            if (Directory.Exists(WorkingDir))
            {
                await AutoDetectSvnUser();
                SVNStatus.ShowProjectInfo(project, WorkingDir);
                await RefreshRepositoryInfo();
                await RefreshStatus();
            }
        }

        public void BroadcastWorkingDirChange(string newPath)
        {
            this.WorkingDir = newPath.Replace("\\", "/");

            if (svnUI.SettingsWorkingDirInput != null)
                svnUI.SettingsWorkingDirInput.SetTextWithoutNotify(this.WorkingDir);

            if (svnUI.CheckoutDestFolderInput != null)
                svnUI.CheckoutDestFolderInput.SetTextWithoutNotify(this.WorkingDir);

            if (svnUI.LoadDestFolderInput != null)
                svnUI.LoadDestFolderInput.SetTextWithoutNotify(this.WorkingDir);
        }

        public void BroadcastSshKeyChange(string newKeyPath)
        {
            this.CurrentKey = newKeyPath.Replace("\\", "/");
            SvnRunner.KeyPath = this.CurrentKey;

            if (svnUI.SettingsSshKeyPathInput != null)
                svnUI.SettingsSshKeyPathInput.SetTextWithoutNotify(this.CurrentKey);

            if (svnUI.CheckoutPrivateKeyInput != null)
                svnUI.CheckoutPrivateKeyInput.SetTextWithoutNotify(this.CurrentKey);

            if (svnUI.LoadPrivateKeyInput != null)
                svnUI.LoadPrivateKeyInput.SetTextWithoutNotify(this.CurrentKey);
        }

        public void BroadcastUrlChange(string newUrl)
        {
            this.RepositoryUrl = newUrl.Trim();

            if (svnUI.SettingsRepoUrlInput != null)
                svnUI.SettingsRepoUrlInput.SetTextWithoutNotify(this.RepositoryUrl);

            if (svnUI.CheckoutRepoUrlInput != null)
                svnUI.CheckoutRepoUrlInput.SetTextWithoutNotify(this.RepositoryUrl);

            if (svnUI.LoadRepoUrlInput != null)
                svnUI.LoadRepoUrlInput.SetTextWithoutNotify(this.RepositoryUrl);
        }

        public async Task RefreshStatus()
        {
            if (string.IsNullOrEmpty(WorkingDir))
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += "<color=red>[SVN] Refresh aborted: WorkingDir is null or empty!</color>\n";
                return;
            }

            try
            {
                await SVNStatus.ExecuteRefreshWithAutoExpand();
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += "<color=green>[SVN] Status refresh finished.</color>\n";
            }
            catch (Exception e)
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += $"<color=red>[SVN] CRITICAL: ExecuteRefresh error: {e.Message}</color>\n";
                return;
            }

            if (CurrentStatusDict == null)
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += "<color=red>[SVN] Error: CurrentStatusDict is null!</color>\n";
                return;
            }

            var conflictedFiles = CurrentStatusDict.Where(x => x.Value.status.Contains("C")).ToList();

            if (conflictedFiles.Count > 0)
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += $"<color=orange>[SVN] Found {conflictedFiles.Count} conflicts. Switching to Resolve Panel...</color>\n";

                OnConflictDetected();
            }
            else
            {
                if (svnUI?.LogText != null)
                    svnUI.LogText.text += "<color=green>[SVN] No conflicts found.</color>\n";
            }
        }

        private void OnConflictDetected()
        {
            Debug.Log("<color=red>[SVN] Conflicts detected! Switching to Resolve Panel.</color>");

            if (panelHandler != null)
            {
                panelHandler.Button_OpenResolve();

                if (svnUI != null && svnUI.LogText != null)
                {
                    svnUI.LogText.text += "<color=red><b>[!] Conflict detected.</b> Redirecting to Resolve Tool...</color>\n";
                }
            }
        }

        public string ParseRevision(string input)
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(input, @"revision\s+(\d+)");

            return match.Success ? match.Groups[1].Value : null;
        }

        public string GetRepoRoot()
        {
            string url = RepositoryUrl;
            if (string.IsNullOrEmpty(url)) return "";

            url = url.TrimEnd('/');

            string[] markers = { "/trunk", "/branches", "/tags" };
            foreach (var marker in markers)
            {
                if (url.Contains(marker))
                {
                    return url.Substring(0, url.IndexOf(marker));
                }
            }

            return url;
        }

        private async void OnApplicationFocus(bool focus)
        {
            if (focus && !string.IsNullOrEmpty(workingDir) && !isProcessing)
            {
                try
                {
                    await RefreshStatus();
                }
                catch (Exception e)
                {
                    svnUI.LogText.text = $"Focus refresh failed: {e.Message}";
                }
            }
        }
    }
}

[System.Serializable]
public class SVNProject
{
    public string projectName;
    public string repoUrl;
    public string workingDir;
    public string privateKeyPath;
    public string mergeToolPath;
    public System.DateTime lastOpened;
}

[System.Serializable]
public class SVNProjectList
{
    public List<SVNProject> projects = new List<SVNProject>();
}