using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        [Header("UI References")]
        [SerializeField] private SVNUI svnUI = null;
        [SerializeField] private GameObject loadingOverlay = null;
        [SerializeField] private PanelHandler panelHandler = null;
        [SerializeField] private GameObject mainUIPanel;
        [SerializeField] private ProjectSelectionPanel projectSelectionPanel;

        public PanelHandler PanelHandler => panelHandler;
        public ProjectSelectionPanel ProjectSelectionPanel => projectSelectionPanel;
        public GameObject MainUIPanel => mainUIPanel;

        private string currentUserName = "Unknown";
        private string workingDir = string.Empty;
        private string currentKey = string.Empty;
        private string mergeToolPath = string.Empty;
        private bool isProcessing = false;

        public HashSet<string> ExpandedPaths { get; set; } = new HashSet<string>();
        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; set; } = new Dictionary<string, (string status, string size)>();
        public string RepositoryUrl { get; set; } = string.Empty;

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

        public string CurrentUserName => currentUserName;
        public bool IsProcessing { get => isProcessing; set => isProcessing = value; }
        public string WorkingDir { get => workingDir; set => workingDir = value; }
        public string CurrentKey { get => currentKey; set => currentKey = value; }
        public string MergeToolPath { get => mergeToolPath; set => mergeToolPath = value; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                DestroyImmediate(gameObject);
                return;
            }
            Instance = this;
            InitializeModules();
        }

        private void InitializeModules()
        {
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

        public string GetRepoRoot() => SVNAssetLocator.GetRepoRoot(RepositoryUrl);
        public string ParseRevision(string input) => SVNAssetLocator.ParseRevision(input);

        public async Task SetWorkingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            WorkingDir = SVNAssetLocator.NormalizePath(path);
            Debug.Log($"[SVN] Working Directory set to: {WorkingDir}");
            await RefreshRepositoryInfo();
        }

        private async void Start()
        {
            if (svnUI == null) return;
            SetupInputListeners();

            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            var lastProject = ProjectSettings.LoadProjects().Find(p => p.workingDir == lastPath);

            if (lastProject != null)
            {
                LoadProject(lastProject);
                projectSelectionPanel?.gameObject.SetActive(false);
            }
            else
            {
                projectSelectionPanel?.gameObject.SetActive(true);
                projectSelectionPanel?.RefreshList();
            }
        }

        public async Task<string> AutoDetectSvnUser()
        {
            currentUserName = "Detecting...";
            if (string.IsNullOrEmpty(WorkingDir)) return currentUserName = "Unknown";

            if (!SVNAssetLocator.IsWorkingCopy(WorkingDir))
                return currentUserName = Environment.UserName.ToLower();

            try
            {
                string xmlOutput = await SvnRunner.RunAsync("info --xml", WorkingDir, false);
                string detected = SVNAssetLocator.ExtractUserFromUrl(xmlOutput);
                if (!string.IsNullOrEmpty(detected)) return currentUserName = detected;

                string authOutput = await SvnRunner.RunAsync("auth", WorkingDir, false);
                var userLine = authOutput.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("Username:"));
                if (userLine != null) return currentUserName = userLine.Replace("Username:", "").Trim();
            }
            catch { }

            return currentUserName = Environment.UserName.ToLower();
        }

        public void LoadProject(SVNProject project)
        {
            WorkingDir = SVNAssetLocator.NormalizePath(project.workingDir);
            RepositoryUrl = project.repoUrl;
            CurrentKey = SVNAssetLocator.NormalizePath(project.privateKeyPath);
            MergeToolPath = SVNAssetLocator.NormalizePath(project.mergeToolPath);
            SvnRunner.KeyPath = CurrentKey;

            SyncUIToCurrentState();
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", WorkingDir);
            PlayerPrefs.Save();

            if (Directory.Exists(WorkingDir)) InitializeActiveProject(project);
        }

        private async void InitializeActiveProject(SVNProject project)
        {
            await AutoDetectSvnUser();
            SVNStatus.ShowProjectInfo(project, WorkingDir);
            await RefreshRepositoryInfo();
            await RefreshStatus();
        }

        private void SyncUIToCurrentState()
        {
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(CurrentKey);
            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(MergeToolPath);
        }

        public async Task RefreshStatus()
        {
            if (string.IsNullOrEmpty(WorkingDir) || isProcessing) return;

            try
            {
                await SVNStatus.ExecuteRefreshWithAutoExpand();
                LogToUI("[SVN] Status refresh finished.", "green");

                var conflicted = CurrentStatusDict.Values.Count(v => v.status.Contains("C"));
                if (conflicted > 0)
                {
                    LogToUI($"[SVN] Found {conflicted} conflicts!", "orange");
                    panelHandler?.Button_OpenResolve();
                }
            }
            catch (Exception e) { LogToUI($"[SVN] Error: {e.Message}", "red"); }
        }

        private void LogToUI(string message, string color)
        {
            if (svnUI?.LogText != null)
                svnUI.LogText.text += $"<color={color}>{message}</color>\n";
        }

        public void BroadcastWorkingDirChange(string path)
        {
            WorkingDir = SVNAssetLocator.NormalizePath(path);
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(WorkingDir);
            svnUI.CheckoutDestFolderInput?.SetTextWithoutNotify(WorkingDir);
            svnUI.LoadDestFolderInput?.SetTextWithoutNotify(WorkingDir);
        }

        public void BroadcastSshKeyChange(string newKeyPath)
        {
            CurrentKey = SVNAssetLocator.NormalizePath(newKeyPath);
            SvnRunner.KeyPath = CurrentKey;
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(CurrentKey);
            svnUI.CheckoutPrivateKeyInput?.SetTextWithoutNotify(CurrentKey);
            svnUI.LoadPrivateKeyInput?.SetTextWithoutNotify(CurrentKey);
        }

        public void BroadcastUrlChange(string newUrl)
        {
            RepositoryUrl = newUrl.Trim();
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            svnUI.CheckoutRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            svnUI.LoadRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
        }

        public async Task RefreshRepositoryInfo()
        {
            if (!SVNAssetLocator.IsWorkingCopy(WorkingDir)) return;
            string url = await SvnRunner.GetRepoUrlAsync(WorkingDir);
            if (!string.IsNullOrEmpty(url))
            {
                RepositoryUrl = url.Trim();
                svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            }
        }

        private void SetupInputListeners()
        {
            svnUI.SettingsRepoUrlInput?.onValueChanged.AddListener(v => { BroadcastUrlChange(v); UpdateCurrentProjectData(); });
            svnUI.SettingsSshKeyPathInput?.onValueChanged.AddListener(v => { BroadcastSshKeyChange(v); UpdateCurrentProjectData(); });
            svnUI.SettingsWorkingDirInput?.onValueChanged.AddListener(v => { BroadcastWorkingDirChange(v); UpdateCurrentProjectData(); });
            svnUI.SettingsMergeToolPathInput?.onValueChanged.AddListener(v => { MergeToolPath = v.Trim(); UpdateCurrentProjectData(); });
        }

        private void UpdateCurrentProjectData()
        {
            var projects = ProjectSettings.LoadProjects();
            var current = projects.Find(p => p.workingDir == WorkingDir);
            if (current != null)
            {
                current.repoUrl = RepositoryUrl;
                current.privateKeyPath = CurrentKey;
                current.mergeToolPath = MergeToolPath;
                ProjectSettings.SaveProjects(projects);
            }
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
                    Debug.LogError($"Focus refresh failed: {e.Message}");
                }
            }
        }
    }

    [Serializable]
    public class SVNProject
    {
        public string projectName;
        public string repoUrl;
        public string workingDir;
        public string privateKeyPath;
        public string mergeToolPath;
        public DateTime lastOpened;
    }

    [Serializable]
    public class SVNProjectList
    {
        public List<SVNProject> projects = new List<SVNProject>();
    }
}