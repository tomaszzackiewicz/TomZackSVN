using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        [SerializeField] private SVNUI svnUI = null;
        [SerializeField] private GameObject loadingOverlay = null;
        [SerializeField] private PanelHandler panelHandler = null;
        [SerializeField] private GameObject mainUIPanel; // Przeciągnij tutaj swój główny panel UI w Inspektorze
        [SerializeField] private ProjectSelectionPanel projectSelectionPanel; // Przeciągnij tutaj skrypt panelu wyboru

        private (string status, string[] files) currentStatus;

        [Header("State")]
        public HashSet<string> expandedPaths = new HashSet<string>();

        private string workingDir = string.Empty;
        private string currentKey = string.Empty;
        private string mergeToolPath = string.Empty;

        private bool isProcessing = false;

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

        public string RepositoryUrl { get; set; } = string.Empty;

        public GameObject MainUIPanel => mainUIPanel;
        public PanelHandler PanelHandler => panelHandler;
        public ProjectSelectionPanel ProjectSelectionPanel => projectSelectionPanel;

        public string CurrentUserName
        {
            get
            {
                return Environment.UserName;
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

        private void SetLoading(bool isLoading)
        {
            if (loadingOverlay != null)
            {
                loadingOverlay.SetActive(isLoading);
            }
        }

        public async Task SetWorkingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            workingDir = path;
            WorkingDir = path;

            Debug.Log($"[SVN] Working Directory set to: {path}");

            await RefreshRepositoryInfo();
            UpdateBranchInfo();
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

        public void MigrateOldSettingsToJSON()
        {
            // Sprawdzamy, czy w ogóle mamy stare dane w PlayerPrefs
            if (PlayerPrefs.HasKey(KEY_WORKING_DIR))
            {
                string oldDir = PlayerPrefs.GetString(KEY_WORKING_DIR);

                // Jeśli plik JSON już istnieje i ma ten projekt, nie migrujemy ponownie
                List<SVNProject> projects = ProjectSettings.LoadProjects();
                if (projects.Exists(p => p.workingDir == oldDir)) return;

                string oldUrl = PlayerPrefs.GetString(KEY_REPO_URL, "");
                string oldKey = PlayerPrefs.GetString(KEY_SSH_PATH, "");

                SVNProject legacyProject = new SVNProject
                {
                    projectName = "Imported Project",
                    workingDir = oldDir,
                    repoUrl = oldUrl,
                    privateKeyPath = oldKey,
                    lastOpened = System.DateTime.Now
                };

                projects.Add(legacyProject);
                ProjectSettings.SaveProjects(projects);

                Debug.Log("[SVN] Successfully migrated old settings to JSON.");
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
                await RefreshRepositoryInfo();
                UpdateBranchInfo();
                RefreshStatus();
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

            Debug.Log($"[SVN] WorkingDir broadcasted: {this.WorkingDir}");
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

        public async void UpdateBranchInfo()
        {
            string rootPath = WorkingDir;

            if (string.IsNullOrEmpty(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                if (svnUI?.BranchInfoText != null)
                    svnUI.BranchInfoText.text = "<color=grey>No active project</color>";
                return;
            }

            try
            {
                string output = await SvnRunner.RunAsync("info --xml", rootPath);

                if (string.IsNullOrEmpty(output)) return;

                string fullUrl = "Unknown";
                string relativeUrl = "Unknown";
                string revision = "0";

                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.LoadXml(output);

                System.Xml.XmlNode urlNode = doc.SelectSingleNode("//url");
                System.Xml.XmlNode relativeUrlNode = doc.SelectSingleNode("//relative-url");
                System.Xml.XmlNode entryNode = doc.SelectSingleNode("//entry");

                if (urlNode != null) fullUrl = urlNode.InnerText;
                if (relativeUrlNode != null) relativeUrl = relativeUrlNode.InnerText;
                if (entryNode != null) revision = entryNode.Attributes["revision"]?.Value ?? "0";

                this.RepositoryUrl = fullUrl.TrimEnd('/');

                string branchDisplayName = relativeUrl.Replace("^/", "");

                if (string.IsNullOrEmpty(branchDisplayName) || branchDisplayName == "/")
                {
                    branchDisplayName = "trunk (root)";
                }
                else if (branchDisplayName.Contains("/"))
                {
                    string[] parts = branchDisplayName.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        branchDisplayName = parts[parts.Length - 1];
                    }
                }

                if (svnUI?.BranchInfoText != null)
                {
                    svnUI.BranchInfoText.text = $"<color=#00E5FF>Branch:</color> {branchDisplayName} | <color=#FFD700>Rev:</color> {revision}";
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[UpdateBranchInfo] Error: {ex.Message}");
                if (svnUI?.BranchInfoText != null)
                    svnUI.BranchInfoText.text = "<color=red>SVN Info: Metadata Error</color>";
            }
        }

        public async Task RunDiagnostics()
        {
            if (svnUI == null || svnUI.LogText == null)
            {
                Debug.LogError("[SVN Manager] SVNUI or LogText reference is missing. Diagnostics cannot be displayed.");
                return;
            }

            if (!string.IsNullOrEmpty(CurrentKey))
            {
                SvnRunner.KeyPath = CurrentKey;
            }
            else
            {
                Debug.LogWarning("[SVN Manager] No SSH Key path found in Manager. SSH operations may fail.");
            }

            svnUI.LogText.text = "<b>[SYSTEM DIAGNOSTICS]</b>\n";
            svnUI.LogText.text += $"Working Dir: <color=#AAAAAA>{WorkingDir}</color>\n";

            try
            {
                bool svnOk = await SvnRunner.CheckIfSvnInstalled();
                svnUI.LogText.text += svnOk
                    ? "<color=green>SVN CLI:</color> Found\n"
                    : "<color=red>SVN CLI:</color> Missing (Add to PATH!)\n";

                bool sshOk = await SvnRunner.CheckIfSshInstalled();
                svnUI.LogText.text += sshOk
                    ? "<color=green>OpenSSH:</color> Found\n"
                    : "<color=red>OpenSSH:</color> Missing!\n";

                if (!string.IsNullOrEmpty(CurrentKey))
                {
                    bool keyExists = System.IO.File.Exists(CurrentKey);
                    svnUI.LogText.text += keyExists
                        ? "<color=green>SSH Key:</color> File verified\n"
                        : "<color=orange>SSH Key:</color> Path set but file not found!\n";
                }

                svnUI.LogText.text += "-----------------------------------\n";
            }
            catch (System.Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Diagnostics Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Diagnostics failed: {ex}");
            }
        }

        public async void RefreshStatus()
        {
            if (string.IsNullOrEmpty(WorkingDir)) return;

            SVNStatus.ShowOnlyModified();
        }

        public string ParseRevision(string input)
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(input, @"revision\s+(\d+)");

            return match.Success ? match.Groups[1].Value : null;
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            var match = System.Text.RegularExpressions.Regex.Match(infoOutput, @"^Revision:\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; private set; } = new Dictionary<string, (string status, string size)>();

        public void UpdateFilesStatus(Dictionary<string, (string status, string size)> newStatus)
        {
            if (newStatus == null) return;

            CurrentStatusDict = newStatus;
        }

        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            if (svnUI.StatsText != null)
            {
                if (isIgnoredView)
                {
                    svnUI.StatsText.text = $"<color=#AAAAAA><b>VIEW: IGNORED</b></color> | " +
                                           $"Folders: {stats.IgnoredFolderCount} | " +
                                           $"Files: {stats.IgnoredFileCount} | " +
                                           $"Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>";
                }
                else
                {
                    svnUI.StatsText.text = $"<color=#FFD700><b>VIEW: WORKING COPY</b></color> | " +
                                           $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | " +
                                           $"<color=#FFD700>Mod (M): {stats.ModifiedCount}</color> | " +
                                           $"<color=#00FF00>Add (A): {stats.AddedCount}</color> | " +
                                           $"<color=#00E5FF>New (?): {stats.NewFilesCount}</color> | " +
                                           $"<color=#FF4444>Deleted (D/!): {stats.DeletedCount}</color> | " + // Zmieniono etykietę
                                           $"<color=#FF00FF>Conf (C): {stats.ConflictsCount}</color>";
                }
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    svnUI.CommitStatsText.text = "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>";
                }
                else
                {
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount + stats.NewFilesCount + stats.DeletedCount;

                    string conflictPart = "";
                    if (stats.ConflictsCount > 0)
                    {
                        conflictPart = $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>";
                    }

                    svnUI.CommitStatsText.text = $"<b>Pending Changes:</b> " +
                        $"<color=#FFD700>M: {stats.ModifiedCount}</color> | " +
                        $"<color=#00FF00>A: {stats.AddedCount}</color> | " +
                        $"<color=#00E5FF>?: {stats.NewFilesCount}</color> | " +
                        $"<color=#FF4444>D/!: {stats.DeletedCount}</color> | " +
                        $"<color=#FFFFFF><b>Total: {totalToCommit}</b></color>" +
                        conflictPart;
                }
            }
        }

        public string GetRepoRoot()
        {
            if (string.IsNullOrEmpty(RepositoryUrl)) return "";
            if (RepositoryUrl.EndsWith("/trunk"))
                return RepositoryUrl.Replace("/trunk", "");

            if (RepositoryUrl.Contains("/branches/"))
                return RepositoryUrl.Substring(0, RepositoryUrl.IndexOf("/branches/"));

            return RepositoryUrl;
        }
        

        private void OnApplicationFocus(bool focus)
        {
            if (focus && !string.IsNullOrEmpty(workingDir))
            {
                //SVNStatus.ShowOnlyModified();
            }
        }

        
    }
}