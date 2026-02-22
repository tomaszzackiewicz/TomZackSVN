using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private string currentUserName = "Unknown";
        private string workingDir = string.Empty;
        private string currentKey = string.Empty;
        private string mergeToolPath = string.Empty;
        private bool isProcessing = false;

        public HashSet<string> ExpandedPaths { get; set; } = new HashSet<string>();
        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; set; } = new Dictionary<string, (string status, string size)>();
        public string RepositoryUrl { get; set; } = string.Empty;
        public PanelHandler PanelHandler => panelHandler;
        public ProjectSelectionPanel ProjectSelectionPanel => projectSelectionPanel;
        public GameObject MainUIPanel => mainUIPanel;
        public string CurrentUserName => currentUserName;

        public string WorkingDir
        {
            get => workingDir;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    workingDir = value;
                    return;
                }

                string cleaned = "";
                foreach (char c in value)
                {
                    if (char.IsLetterOrDigit(c) || ":\\/._- ".Contains(c.ToString()))
                    {
                        cleaned += c;
                    }
                }

                workingDir = cleaned.Trim();

                UnityEngine.Debug.Log($"[SVN Manager] WorkingDir sanitized to: '{workingDir}'");
            }
        }
        public string CurrentKey { get => currentKey; set => currentKey = value; }
        public string MergeToolPath { get => mergeToolPath; set => mergeToolPath = value; }
        private readonly Dictionary<Type, SVNBase> _modules = new Dictionary<Type, SVNBase>();

        public event Action<bool> OnProcessingStateChanged;

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing == value) return;
                _isProcessing = value;
                OnProcessingStateChanged?.Invoke(_isProcessing);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                DestroyImmediate(gameObject);
                return;
            }
            Instance = this;

            SVNLogger.Initialize();

            InitializeAllModules();
        }

        private void InitializeAllModules()
        {
            _modules.Clear();

            try
            {
                RegisterModule(new SVNAdd(svnUI, this));
                RegisterModule(new SVNBranchTag(svnUI, this));
                RegisterModule(new SVNCheckout(svnUI, this));
                RegisterModule(new SVNClean(svnUI, this));
                RegisterModule(new SVNCommit(svnUI, this));
                RegisterModule(new SVNExternal(svnUI, this));
                RegisterModule(new SVNLoad(svnUI, this));
                RegisterModule(new SVNLock(svnUI, this));
                RegisterModule(new SVNLog(svnUI, this));
                RegisterModule(new SVNMerge(svnUI, this));
                RegisterModule(new SVNMissing(svnUI, this));
                RegisterModule(new SVNResolve(svnUI, this));
                RegisterModule(new SVNRevert(svnUI, this));
                RegisterModule(new SVNSettings(svnUI, this));
                RegisterModule(new SVNShelve(svnUI, this));
                RegisterModule(new SVNStatus(svnUI, this));
                RegisterModule(new SVNTerminal(svnUI, this));
                RegisterModule(new SVNUpdate(svnUI, this));

                Debug.Log($"<color=green>[SVN]</color> Successfully initialized {_modules.Count} modules manually.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SVN] Manual initialization failed: {e.Message}");
            }
        }

        private void RegisterModule<T>(T module) where T : SVNBase
        {
            _modules[typeof(T)] = module;
        }

        public T GetModule<T>() where T : SVNBase
        {
            if (_modules.TryGetValue(typeof(T), out var module))
            {
                return (T)module;
            }
            return null;
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
            var statusModule = GetModule<SVNStatus>();
            statusModule.ClearCurrentData();

            WorkingDir = CleanPath(SVNAssetLocator.NormalizePath(project.workingDir));
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
            var statusModule = GetModule<SVNStatus>();
            if (statusModule != null)
            {
                statusModule.ShowProjectInfo(project, WorkingDir);
            }

            await RefreshRepositoryInfo();
            await RefreshStatus();

            var poller = GetComponent<SVNPollingService>();
            if (poller != null && statusModule != null)
            {
                poller.StartPolling(statusModule);
            }
        }

        private void SyncUIToCurrentState()
        {
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(RepositoryUrl);
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(CurrentKey);
            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(MergeToolPath);
        }

        private string CleanPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            System.Text.StringBuilder debugInfo = new System.Text.StringBuilder();
            debugInfo.AppendLine($"[SVN Path Debug] Original Path: '{path}'");
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                int code = (int)c;
                debugInfo.AppendLine($"[{i}] '{(char.IsControl(c) ? '?' : c)}' (Code: {code})");
            }
            UnityEngine.Debug.Log(debugInfo.ToString());

            return new string(path.Where(c => !char.IsControl(c) && (int)c != 160 && (int)c != 8203).ToArray()).Trim();
        }

        public async Task RefreshStatus(bool force = false)
        {
            if (string.IsNullOrEmpty(WorkingDir))
            {
                Debug.LogWarning("[SVN] Refresh aborted: WorkingDir is not set.");
                return;
            }

            if (isProcessing && !force) return;

            try
            {
#if UNITY_EDITOR
                SVNLogBridge.LogLine("<i>Synchronizing Unity AssetDatabase...</i>", append: true);
                UnityEditor.AssetDatabase.Refresh();
#endif

                var statusModule = GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    SVNLogBridge.LogLine("<b>[Refresh]</b> Fetching SVN status...", append: true);
                    await statusModule.ExecuteRefreshWithAutoExpand();
                }

                var statusDict = CurrentStatusDict;
                if (statusDict != null)
                {
                    bool hasConflicts = statusDict.Values.Any(v => v.status != null && v.status.Contains("C"));

                    if (hasConflicts)
                    {
                        LogToUI("[SVN] Conflicts detected! Opening Resolve panel.", "orange");
                        if (panelHandler != null)
                        {
                            _ = panelHandler.Button_OpenResolve();
                        }
                    }
                }

                UpdateStatus();

                SVNLogBridge.LogLine("<color=green>Status updated successfully.</color>", append: true);
            }
            catch (Exception e)
            {
                LogToUI($"[SVN] Refresh Error: {e.Message}", "red");
                Debug.LogError($"[SVN] Refresh Exception: {e}");
            }
        }

        public void UpdateStatus()
        {
            var statusModule = GetModule<SVNStatus>();
            if (statusModule != null)
            {
                string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
                var lastProject = ProjectSettings.LoadProjects().Find(p => p.workingDir == lastPath);
                statusModule.ShowProjectInfo(lastProject, WorkingDir);
            }
        }

        private void LogToUI(string message, string color, bool append = true)
        {
            SVNLogBridge.LogLine($"<color={color}>{message}</color>", append);
        }

        public void BroadcastWorkingDirChange(string path)
        {
            WorkingDir = CleanPath(SVNAssetLocator.NormalizePath(path));
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