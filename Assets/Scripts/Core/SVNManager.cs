using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SVN.Core
{
    public class SVNManager : MonoBehaviour
    {
        public static SVNManager Instance { get; private set; }

        public event Action<SVNProject> OnProjectChanged;
        public event Action<SVNProjectInfoSnapshot> OnSnapshotChanged;

        public const string KEY_REPO_URL = "SVN_Persisted_RepositoryURL";
        public const string KEY_WORKING_DIR = "SVN_Persisted_WorkingDir";
        public const string KEY_SSH_PATH = "SVN_Persisted_SSHKeyPath";
        public const string KEY_TEXTEDITOR_TOOL = "SVN_Persisted_MergeTool";
        public const string KEY_RESOLVE_TOOL = "SVN_Persisted_ResolveTool";
        public const string KEY_DIFF_TOOL = "SVN_Persisted_DiffTool";
        public const string KEY_BLAME_TOOL = "SVN_Persisted_BlameTool";
        public const string KEY_SSH_OPTIONS = "SVN_Persisted_SshOptions";

        [Header("UI References")]
        [SerializeField] private SVNUI svnUI = null;
        [SerializeField] private GameObject loadingOverlay = null;
        [SerializeField] private PanelHandler panelHandler = null;
        [SerializeField] private GameObject mainUIPanel;
        [SerializeField] private ProjectSelectionPanel projectSelectionPanel;
        [Header("Project Load Safety")]
        [SerializeField] private bool autoSwitchRootCheckoutToTrunk = false;

        [Header("Focus Refreash")]
        [SerializeField] private float focusRefreshCooldownSeconds = 10f;

        public event Action OnRepositoryStateChanged;

		private static readonly AsyncReaderWriterLock _managerLock = new();

        private bool _ignoreSync;
        private float _lastFocusRefreshTime;
        private string currentUserName = "Unknown";
        private string workingDir = string.Empty;
        private string currentKey = string.Empty;
        private string mergeToolPath = string.Empty;
        private bool _focusRefreshRunning;
        public SVNOperationInfo OperationInfo;
        public static string MainThreadWorkingDir;
        public static string CachedUserName;
        private bool _isApplyingSnapshot;
        public SVNLockCache LockCache = new SVNLockCache();
        private FileSystemWatcher _folderWatcher;
        private int _isUpdatingSize = 0;
        private int _diskChangesDetectedFlag = 0;
        private SVNBar _cachedBar;
        private string sshOptions = "-o ServerAliveInterval=15 -o ServerAliveCountMax=10 -o IPQoS=throughput";

        private long _lastDiskEventTimestamp = 0;
        private const int DiskDebounceMs = 1500;
        private int _restartingWatcher;   // 0/1

        private SVNPollingService _cachedPoller;
        /// <summary>RepoBrowser: preload drzewa przy starcie (svn list na
        /// SERWERZE). Default false — lazy przy pierwszym otwarciu panelu.</summary>
        public bool preloadRepoBrowserAtStartup = false;

        private CancellationTokenSource _refreshStatusCts;
        private CancellationTokenSource _projectSwitchDebounceCts;
        private CancellationTokenSource _watcherRestartCts;
        private CancellationTokenSource _lifetimeCts;
        private CancellationTokenSource _saveProjectDebounceCts;

        public string SessionToken { get; private set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
        public SVNProject CurrentProject { get; private set; }
        public bool WasUpdateCanceled { get; set; }
        public SVNProjectInfoSnapshot CurrentSnapshot { get; set; }
        public bool IsUpdateRunning { get; set; }
        public bool LastUpdateSucceeded { get; set; }
        public HashSet<string> ExpandedPaths { get; set; } = new HashSet<string>();
        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; set; } = new Dictionary<string, (string status, string size)>();
        public string RepositoryUrl { get; set; } = string.Empty;
        public PanelHandler PanelHandler => panelHandler;
        public ProjectSelectionPanel ProjectSelectionPanel => projectSelectionPanel;
        public GameObject MainUIPanel => mainUIPanel;
        public string CurrentUserName => currentUserName;
        public bool SvnClientAvailable { get; private set; } = true;
        public string DiffToolPath { get; set; } = string.Empty;
        public string ResolveToolPath { get; set; } = string.Empty;
        public string BlameToolPath { get; set; } = string.Empty;
        public string SshOptions { get => sshOptions; set => sshOptions = value; }

        public void SetCurrentStatus(Dictionary<string, (string status, string size)> data)
        {
            CurrentStatusDict = data;
        }

		public void RaiseRepositoryStateChanged()
		{
			UnityMainThreadDispatcher.Enqueue(() =>
			{
				try { OnRepositoryStateChanged?.Invoke(); }
				catch (Exception ex) { SVNLogBridge.LogError($"[SVNManager] OnRepositoryStateChanged handler: {ex.Message}"); }
			});
		}

        public string WorkingDir
        {
            get => workingDir;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    workingDir = value;
                    return;
                }

                // === FIX: była 2. kopia tego samego filtra (inline StringBuilder)
                workingDir = FilterPathChars(value);
            }
        }

        public bool DiskChangesDetected
        {
            get => Interlocked.CompareExchange(ref _diskChangesDetectedFlag, 0, 0) == 1;
            set
            {
                if (value)
                    Interlocked.Exchange(ref _diskChangesDetectedFlag, 1);
                else
                    Interlocked.Exchange(ref _diskChangesDetectedFlag, 0);
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

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    OnProcessingStateChanged?.Invoke(_isProcessing);
                });
            }
        }

        private void Awake()
        {
            Application.runInBackground = true;

            if (Instance != null && Instance != this)
            {
                DestroyImmediate(gameObject);
                return;
            }
            Instance = this;

            _lifetimeCts = new CancellationTokenSource();

            MainThreadWorkingDir = this.WorkingDir;
            CachedUserName = this.CurrentUserName;

            SVNLogger.Initialize();

            if (svnUI == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] CRITICAL: svnUI reference is not assigned in SVNManager Inspector!");
                return;
            }

            svnUI.SvnManager = this;

            InitializeAllModules();

            SVN.Core.SvnRunner.OnProcessingStateChanged += OnSvnProcessingChanged;

            _cachedPoller = GetComponent<SVNPollingService>();

            _cachedBar = GetModule<SVNBar>();
        }

        private void Update()
        {
            // FIX zasobów: lookup co klatkę → cache z Awake:
            _cachedBar?.Tick();

            if (DiskChangesDetected)
            {
                long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                long elapsedTicks = currentTimestamp - Interlocked.Read(ref _lastDiskEventTimestamp);
                double elapsedMs = (double)elapsedTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0;

                if (elapsedMs >= DiskDebounceMs)
                {
                    DiskChangesDetected = false;

                    if (!IsUpdateRunning && !IsProcessing &&
                        !string.IsNullOrEmpty(WorkingDir) &&
                        gameObject.activeInHierarchy)
                    {
                        _ = RefreshStatus(force: false);
                    }
                }
            }
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
                RegisterModule(new SVNDiff(svnUI, this));
                RegisterModule(new SVNBlame(svnUI, this));
                RegisterModule(new SVNRevGraph(svnUI, this));
                RegisterModule(new SVNBar(svnUI, this));
                RegisterModule(new SVNIgnore(svnUI, this));
                RegisterModule(new SVNRepoBrowser(svnUI, this));
                RegisterModule(new SVNRevision(svnUI, this));
                RegisterModule(new SVNRepoRepair(svnUI, this));
                RegisterModule(new SVNSnapshot(svnUI, this));
                RegisterModule(new SVNFileProtection(svnUI, this));
                RegisterModule(new SVNAutoLock(svnUI, this));

                SVNLogBridge.LogToOutput($"<color=green>[SVN] Successfully initialized {_modules.Count} modules manually.</color>");
            }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Manual initialization failed: {e.Message}");
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
            await _managerLock.EnterWriteAsync(CancellationToken.None);
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    CurrentProject = null;
                    return;
                }
                WorkingDir = SVNAssetLocator.NormalizePath(path);
                MainThreadWorkingDir = WorkingDir;   // === FIX: statyka nadąża za zmianą
                SVNLogBridge.LogToOutput($"[SVN] Working Directory set to: {WorkingDir}");
                await RefreshRepositoryInfo();
            }
            finally
            {
                _managerLock.ExitWrite();
            }
        }

        private void Start()
        {
            QualitySettings.vSyncCount = 0;
            //Application.targetFrameRate = 4;
            Application.runInBackground = true;
            OnDemandRendering.renderFrameInterval = 60;

            _ = StartAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogException(t.Exception);
            }, TaskScheduler.Default);
        }

        private async Task StartAsync()
        {
            if (svnUI == null) return;

            SetupInputListeners();

            // Snapshot main-thread API PRZED pierwszym await (ConfigureAwait w Verify
            // przenosi dalszy kod na pulę — PlayerPrefs jest main-only).
            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            var projects = ProjectSettings.LoadProjects();

            // === FIX: porównanie NORMALIZOWANE (case-insensitive + unify slash) —
            // wcześniej 'p.workingDir == lastPath' gubiło projekt przy różnicy
            // '\' vs '/' między zapisami checkoutu a LoadProject.
            var lastProject = projects.Find(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(
                    p.workingDir.Replace("\\", "/").TrimEnd('/'),
                    lastPath.Replace("\\", "/").TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase));

            // === FIX: hint o wstrzymanym checkoucie (stan pauzy istnieje po resecie,
            // ale nikt go nie czytał — użytkownik nie wiedział, że może wznowić).
            string pausedCheckoutPath = PlayerPrefs.GetString("SVN_CheckoutPaused_Path", "");
            if (!string.IsNullOrEmpty(pausedCheckoutPath) &&
                Directory.Exists(Path.Combine(pausedCheckoutPath, ".svn")))
            {
                SVNLogBridge.LogToOutput(
                    $"<color=yellow>[SVN] Wykryto wstrzymany checkout: {pausedCheckoutPath}. " +
                    "Dokończ go w panelu Checkout → Resume.</color>");
            }

            // S6: środowisko SVN. Bez CFA — dalszy bootstrap ma wrócić na MAIN.
            bool svnOk = await VerifySvnClientInstalledAsync();

            if (svnOk && !string.IsNullOrEmpty(lastPath))
            {
                bool workingCopyAlive =
                    lastProject != null &&
                    !string.IsNullOrWhiteSpace(lastProject.workingDir) &&
                    Directory.Exists(lastProject.workingDir) &&
                    SVNAssetLocator.IsWorkingCopy(lastProject.workingDir);

                if (workingCopyAlive && await LoadProject(lastProject))
                {
                    OnProjectChanged?.Invoke(lastProject);

                    if (projectSelectionPanel != null)
                        projectSelectionPanel.gameObject.SetActive(false);
                }
                else
                {
                    GetModule<SVNBar>()?.ShowNoWorkingCopy(lastProject?.projectName);
                    SVNLogBridge.LogToOutput(
                        $"<color=#8B0000>>[SVN] Working copy '{lastProject?.projectName ?? lastPath}' is missing on disk. Showing project selection.</color>");

                    if (projectSelectionPanel != null)
                    {
                        projectSelectionPanel.gameObject.SetActive(true);
                        projectSelectionPanel.RefreshList();
                    }
                }
            }
            else
            {
                projectSelectionPanel?.gameObject.SetActive(true);
                projectSelectionPanel?.RefreshList();
                CurrentProject = null;
            }
        }

        // ===================================================================
        //  CENTRUM SYNCHRONIZACJI — każde źródło (Settings/Checkout/Load/
        //  AddRepo/Browse) → manager + SvnRunner + wszystkie pola UI.
        // ===================================================================

        public enum SettingsSource { None, Settings, Checkout, Load }

        /// <summary>
        /// JEDYNY punkt synchronizacji danych połączenia (URL / katalog / klucz SSH).
        /// null = "nie zmieniaj tej wartości". SetTextWithoutNotify = brak pętli
        //  zdarzeń i brak skoku kursora w polu-źródle. Zapis przez debounce.
        /// </summary>
        public void SynchronizeConnectionSettings(
            SettingsSource source,
            string url = null,
            string workingDirValue = null,
            string key = null)
        {
            if (svnUI == null) return;

            bool changed = false;

            if (url != null && RepositoryUrl != url.Trim())
            {
                RepositoryUrl = url.Trim();
                changed = true;
            }

            if (workingDirValue != null && workingDirValue != WorkingDir)
            {
                WorkingDir = workingDirValue;
                changed = true;
            }

            if (key != null && key.Trim() != CurrentKey)
            {
                CurrentKey = key.Trim();
                SvnRunner.KeyPath = CurrentKey;   // realne SSH od następnej komendy
                changed = true;
            }

            void SyncField(TMP_InputField field, string value)
            {
                if (field != null) field.SetTextWithoutNotify(value ?? "");
            }

            if (source != SettingsSource.Settings)
            {
                SyncField(svnUI.SettingsRepoUrlInput, RepositoryUrl);
                SyncField(svnUI.SettingsWorkingDirInput, WorkingDir);
                SyncField(svnUI.SettingsSshKeyPathInput, CurrentKey);
            }

            if (source != SettingsSource.Checkout)
            {
                SyncField(svnUI.CheckoutRepoUrlInput, RepositoryUrl);
                SyncField(svnUI.CheckoutDestFolderInput, WorkingDir);
                SyncField(svnUI.CheckoutPrivateKeyInput, CurrentKey);
            }

            if (source != SettingsSource.Load)
            {
                SyncField(svnUI.LoadRepoUrlInput, RepositoryUrl);
                SyncField(svnUI.LoadDestFolderInput, WorkingDir);
                SyncField(svnUI.LoadPrivateKeyInput, CurrentKey);
            }

            if (changed)
                DebounceSaveProject();
        }

        // Deleguje do centrum (kompatybilność starych wywołań).
        public void SyncFromCheckoutUI()
        {
            SynchronizeConnectionSettings(
                SettingsSource.Checkout,
                url: svnUI.CheckoutRepoUrlInput.text,
                workingDirValue: svnUI.CheckoutDestFolderInput.text,
                key: svnUI.CheckoutPrivateKeyInput.text);
        }

        public async Task<string> AutoDetectSvnUser()
        {
            currentUserName = "Detecting...";
            if (string.IsNullOrEmpty(WorkingDir)) return SetUser("Unknown");

            if (!SVNAssetLocator.IsWorkingCopy(WorkingDir))
                return SetUser(Environment.UserName.ToLower());

            try
            {
                string xmlOutput = await SvnRunner.RunAsync("info --xml", WorkingDir, false);
                string detected = SVNAssetLocator.ExtractUserFromUrl(xmlOutput);
                if (!string.IsNullOrEmpty(detected)) return SetUser(detected);

                string authOutput = await SvnRunner.RunAsync("auth", WorkingDir, false);
                var userLine = authOutput.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("Username:"));
                if (userLine != null) return SetUser(userLine.Replace("Username:", "").Trim());
            }
            catch { }

            return SetUser(Environment.UserName.ToLower());
        }

        // === Wrapper: LoadProject czyta PlayerPrefs/UI — API main-only; przeskok
        // przez dispatcher gdy wołane z puli wątków.
        public Task<bool> LoadProject(SVNProject project)
        {
            if (project == null) return Task.FromResult(false);

            if (UnityMainThreadDispatcher.IsMainThread)
                return LoadProjectCore(project);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityMainThreadDispatcher.Enqueue(async () =>
            {
                try { tcs.TrySetResult(await LoadProjectCore(project).ConfigureAwait(true)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public async Task<bool> LoadProjectCore(SVNProject project)
        {
            if (project == null) return false;

            if (string.IsNullOrWhiteSpace(project.workingDir) ||
                !Directory.Exists(project.workingDir) ||
                !SVNAssetLocator.IsWorkingCopy(project.workingDir))
            {
                SVNLogBridge.LogErrorToOutput(
                    $"[SVN] Cannot load project '{project.projectName ?? "?"}': working copy missing at '{project.workingDir}'. Restore it with Checkout.");

                GetModule<SVNBar>()?.ShowNoWorkingCopy(project.projectName);

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (projectSelectionPanel != null)
                    {
                        projectSelectionPanel.gameObject.SetActive(true);
                        projectSelectionPanel.RefreshList();
                    }
                });

                return false;
            }

            await _managerLock.EnterWriteAsync(_lifetimeCts.Token);
            try
            {
                CurrentProject = project;
                WorkingDir = CleanPath(SVNAssetLocator.NormalizePath(project.workingDir));
                MainThreadWorkingDir = WorkingDir;              // === FIX: statyka nadąża za projektem
                RepositoryUrl = project.repoUrl;
                CurrentKey = SVNAssetLocator.NormalizePath(project.privateKeyPath);

                if (string.IsNullOrWhiteSpace(CurrentKey))
                    CurrentKey = SvnRunner.KeyPath;

                MergeToolPath = GetSettingWithFallback(project.mergeToolPath, KEY_TEXTEDITOR_TOOL);
                DiffToolPath = GetSettingWithFallback(project.diffToolPath, KEY_DIFF_TOOL);
                ResolveToolPath = GetSettingWithFallback(project.resolveToolPath, KEY_RESOLVE_TOOL);
                BlameToolPath = GetSettingWithFallback(project.blameToolPath, KEY_BLAME_TOOL);
                SvnRunner.KeyPath = CurrentKey;
                SshOptions = GetSettingWithFallback(project.sshOptions, KEY_SSH_OPTIONS) ?? "-o ServerAliveInterval=15 -o ServerAliveCountMax=10 -o IPQoS=throughput";
                SvnRunner.SshOptions = SshOptions;

                ProjectSettings.UpdateProject(project.workingDir, p => p.lastOpened = DateTime.UtcNow);
            }
            finally
            {
                _managerLock.ExitWrite();
            }

            var barModule = GetModule<SVNBar>();
            barModule?.SetLoadingContent(project.projectName ?? "Project");

            string keySnapshot = CurrentKey;
            string workingDirSnapshot = WorkingDir;
            string repoUrlSnapshot = RepositoryUrl;
            bool snapshotApplying = _isApplyingSnapshot;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                _isApplyingSnapshot = true;
                try
                {
                    SynchronizeConnectionSettings(SettingsSource.None,
                        url: repoUrlSnapshot,
                        workingDirValue: workingDirSnapshot,
                        key: keySnapshot);
                }
                finally
                {
                    _isApplyingSnapshot = snapshotApplying;
                }

                SVNPrefs.SetString("SVN_LastOpenedProjectPath", WorkingDir);
                SVNPrefs.SetString("SVN_LastOpenedProjectId", project.projectId);

                var statusModule = GetModule<SVNStatus>();
                statusModule?.ClearCurrentData();
            });

            if (Directory.Exists(WorkingDir))
            {
                await InitializeActiveProject(project);
            }

            return true;
        }

        private async Task InitializeActiveProject(SVNProject project)
        {
            if (string.IsNullOrEmpty(CurrentUserName) || CurrentUserName == "Unknown")
                await AutoDetectSvnUser();

            if (this == null) return;

            var barModule = GetModule<SVNBar>();
            if (barModule != null)
            {
                await barModule.ShowProjectInfo(project, WorkingDir);
                if (this == null) return;
            }

            // === OPTYMALIZACJA: URL już jest w snapshotcie (BuildSnapshotAsync
            // sparsował go z tego samego 'svn info'). Wcześniej RefreshRepository-
            // Info robił KOLEJNY spawn 'svn info --show-item url' o te same dane:
            string snapshotUrl = CurrentSnapshot?.Url;
            if (!string.IsNullOrWhiteSpace(snapshotUrl) && snapshotUrl != "unknown")
            {
                RepositoryUrl = snapshotUrl.Trim();
                UnityMainThreadDispatcher.Enqueue(() =>
                    SynchronizeConnectionSettings(SettingsSource.None, url: RepositoryUrl));
            }
            else
            {
                await RefreshRepositoryInfo();
            }
            if (this == null) return;

            await TryAutoSwitchToTrunkAsync();

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>Loading changes...</i>", "TREE", append: false);
            });

            _ = RefreshStatusAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[Init] Background refresh failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);

            // Safety-net: puste drzewo po loadzie → wymuszony refresh.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200);

                    if (this == null) return;
                    if (string.IsNullOrEmpty(WorkingDir)) return;
                    if (IsUpdateRunning) return;

                    var statusModule = GetModule<SVNStatus>();
                    if (statusModule == null) return;
                    if (statusModule.IsProcessing) return;

                    var data = statusModule.GetCurrentData();
                    if (data == null || data.Count == 0)
                    {
                        SVNLogBridge.LogToOutput("<color=yellow>[SVN Init] Changes tree empty after load — forcing status refresh.</color>");
                        await RefreshStatus(force: true);
                    }
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN Init] Safety-net refresh failed: {ex.Message}");
                }
            });

            // === OPTYMALIZACJA: RepoBrowser LAZY — 'svn list' (serwer) przy
            // starcie konkurował z RefreshStatus o semafor SvnRunner. Teraz
            // drzewo dociąga się przy pierwszym otwarciu panelu browsera:
            var repoBrowser = GetModule<SVNRepoBrowser>();
            if (repoBrowser != null && preloadRepoBrowserAtStartup)
            {
                _ = repoBrowser.LoadInitialTreeAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        SVNLogBridge.LogError($"[Init] Repo Browser load failed: {t.Exception?.InnerException?.Message}");
                }, TaskScheduler.Default);
            }
        }

        private async Task TryAutoSwitchToTrunkAsync()
        {
            string repoRoot = GetRepoRoot();
            if (string.IsNullOrEmpty(RepositoryUrl) || string.IsNullOrEmpty(repoRoot)) return;
            if (!RepositoryUrl.TrimEnd('/').Equals(repoRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;

            if (!autoSwitchRootCheckoutToTrunk)
            {
                SVNLogBridge.LogToOutput("<color=#00E5FF>[SVN Init] Working copy is rooted at repository root. Auto-switch to /trunk is disabled (enable in SVNManager inspector).</color>");
                return;
            }

            try
            {
                string localStatus = await SvnRunner.RunAsync("status", WorkingDir, false, _lifetimeCts.Token);
                if (!string.IsNullOrWhiteSpace(localStatus))
                {
                    SVNLogBridge.LogToOutput("<color=#8B0000>>[SVN Init] Root checkout has local changes — auto-switch to /trunk skipped.</color>");
                    return;
                }
            }
            catch
            {
                return;
            }

            string trunkUrl = $"{repoRoot}/trunk";
            try
            {
                await SvnRunner.RunAsync($"ls \"{trunkUrl}\" --depth empty", WorkingDir, false, _lifetimeCts.Token);
            }
            catch
            {
                SVNLogBridge.LogToOutput("<color=#8B0000>>[SVN Init] Repository has no /trunk — auto-switch skipped.</color>");
                return;
            }

            SVNLogBridge.LogToOutput($"<color=#00CCFF>[SVN Init] Switching root checkout to {trunkUrl}...</color>");
            try
            {
                await SvnRunner.RunAsync($"switch \"{trunkUrl}\" \"{WorkingDir}\" --non-interactive", WorkingDir, false, _lifetimeCts.Token);
                RepositoryUrl = trunkUrl;
                SVNLogBridge.LogToOutput("<color=green>[SVN Init] Switch to /trunk completed.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN Init] Auto-switch to /trunk failed: {ex.Message}");
            }
        }

        private async Task RefreshStatusAsync()
        {
            await RefreshStatus();
        }

        public void SetActiveProject(SVNProject project)
        {
            CurrentProject = project;

            _ = LoadAndNotifyAsync(project).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[SetActiveProject] Failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task LoadAndNotifyAsync(SVNProject project)
        {
            bool loaded = await LoadProject(project);
            if (!loaded) return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                OnProjectChanged?.Invoke(project);
            });
        }

        public void RaiseProjectChanged(SVNProject project)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                OnProjectChanged?.Invoke(project);
            });
        }

        public void RaiseSnapshotChanged(SVNProjectInfoSnapshot snapshot)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                OnSnapshotChanged?.Invoke(snapshot);
            });
        }

        private void SyncUIToCurrentState()
        {
            // Przez centrum — jeden kod, wszystkie panele.
            SynchronizeConnectionSettings(SettingsSource.None,
                url: RepositoryUrl,
                workingDirValue: WorkingDir,
                key: CurrentKey);

            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(MergeToolPath);
            svnUI.SettingsResolveToolPathInput?.SetTextWithoutNotify(ResolveToolPath);
            svnUI.SettingsDiffToolPathInput?.SetTextWithoutNotify(DiffToolPath);
            svnUI.SettingsBlameToolPathInput?.SetTextWithoutNotify(BlameToolPath);
        }

        public void ApplySettingsSnapshot()
        {
            if (svnUI == null) return;

            bool prev = _isApplyingSnapshot;
            _isApplyingSnapshot = true;
            try
            {
                SyncUIToCurrentState();
            }
            finally
            {
                _isApplyingSnapshot = prev;
            }
        }

        // === FIX: była 3. kopia filtra — deleguje
        private string CleanPath(string path) => FilterPathChars(path);

        public async Task RefreshStatus(bool force = false)
        {
            if (string.IsNullOrEmpty(WorkingDir)) return;

            if (!Directory.Exists(WorkingDir) || !SVNAssetLocator.IsWorkingCopy(WorkingDir))
            {
                SVNLogBridge.LogToOutput("<color=#8B0000>>[SVN] Working copy is missing — status refresh skipped.</color>");
                GetModule<SVNBar>()?.ShowNoWorkingCopy(CurrentProject?.projectName);
                return;
            }

            if (IsProcessing && !force) return;
            if (IsUpdateRunning) return;

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshStatusCts, newCts);
            if (oldCts != null)
            {
                oldCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }

            CancellationToken token = newCts.Token;

            try
            {
                GetModule<SVNStatus>()?.CancelCurrentRefresh();

                var statusModule = GetModule<SVNStatus>();
                if (statusModule == null) return;

                await statusModule.ExecuteRefreshWithAutoExpand(force: force);
                await PostProcessStatus();

                SVNLogBridge.LogLine("<color=green>Status updated successfully.</color>", false);

                if (!IsUpdateRunning && Interlocked.Exchange(ref _isUpdatingSize, 1) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var bar = GetModule<SVNBar>();
                            if (bar != null && CurrentSnapshot != null && !token.IsCancellationRequested)
                            {
                                var sizes = await bar.GetSizesWithCacheAsync(WorkingDir, token);

                                if (token.IsCancellationRequested || IsUpdateRunning) return;

                                CurrentSnapshot.WorkingCopySize = sizes.WorkingSize;
                                CurrentSnapshot.RepoTotalSize = sizes.TotalSize;

                                UnityMainThreadDispatcher.Enqueue(() =>
                                {
                                    if (this == null || CurrentSnapshot == null) return;
                                    if (IsUpdateRunning) return;
                                    bar.RenderSnapshot(CurrentSnapshot);
                                });
                            }
                        }
                        catch { }
                        finally { Interlocked.Exchange(ref _isUpdatingSize, 0); }
                    }, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { SVNLogBridge.LogErrorToOutput($"[SVN] Refresh Error: {e.Message}"); }
        }

        public async Task UpdateStatus()
        {
            if (IsUpdateRunning) return;

            var barModule = GetModule<SVNBar>();
            if (barModule != null && CurrentProject != null)
            {
                await barModule.ShowProjectInfo(CurrentProject, WorkingDir, isRefreshing: false);
            }
        }

        private void OnSvnProcessingChanged(bool isProcessing)
        {
            IsProcessing = isProcessing;
        }

        private async Task RefreshLocksSafe()
        {
            var statusModule = GetModule<SVNStatus>();
            if (statusModule == null)
                return;

            try
            {
                string root = WorkingDir;
                if (string.IsNullOrEmpty(root))
                    return;

                var lockDict = await statusModule.GetLocksDictionaryAsync(root);

                if (lockDict == null || lockDict.Count == 0)
                    return;

                statusModule.ApplyLockColors(statusModule.GetCurrentData(), lockDict);

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    statusModule.RefreshVisibleUIOnly();
                });
            }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] RefreshLocksSafe failed: {e.Message}");
            }
        }

        private async Task PostProcessStatus()
        {
            var statusDict = CurrentStatusDict;

            if (statusDict?.Values.Any(v => v.status?.Contains("C") == true) == true)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Conflicts detected! Opening Resolve panel.");
                UnityMainThreadDispatcher.Enqueue(() => panelHandler?.Button_OpenResolve());

                var resolveModule = GetModule<SVNResolve>();
                if (resolveModule != null)
                    await resolveModule.RefreshConflictUI();
            }

            await UpdateStatus();
            await RefreshLocksSafe();
        }

        public async Task PollRepositoryStateAsync()
        {
            if (string.IsNullOrEmpty(WorkingDir)) return;
            if (!Directory.Exists(WorkingDir)) return;
            if (IsProcessing || IsUpdateRunning) return;
            if (mainUIPanel != null && !mainUIPanel.activeInHierarchy) return;

            try
            {
                var lockModule = GetModule<SVNLock>();
                if (lockModule != null)
                    await lockModule.RefreshLockCacheAsync(true).ConfigureAwait(false);

                RaiseRepositoryStateChanged();
            }
            catch { /* cichy — sieć/serwer chwilowo; następny focus złapie */ }
        }

        private async Task FocusRefreshAsync()
        {
            // Jeśli trwa jakakolwiek operacja SVN, od razu przerywamy – bez czekania.
            if (mainUIPanel != null && !mainUIPanel.activeInHierarchy) return;
            if (IsProcessing || IsUpdateRunning) return;

            try
            {
                // Sprawdź zdalne commity (jeśli poller istnieje)
                if (_cachedPoller != null)
                    await _cachedPoller.CheckForRemoteCommitsAsync();

                // Odśwież locki i protection (szybkie, lokalne)
                await PollRepositoryStateAsync();

                // Zwykłe odświeżenie statusu – bez wymuszania.
                if (!string.IsNullOrEmpty(WorkingDir))
                    await RefreshStatus(force: false);
            }
            catch (OperationCanceledException)
            {
                // ciche – anulowanie nie jest błędem
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[FocusRefresh] Unexpected error: {ex.Message}");
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) return;

            // FIX: jeśli okno główne jest nieaktywne, nie rób nic
            if (mainUIPanel != null && !mainUIPanel.activeInHierarchy) return;

            if (_focusRefreshRunning) return;
            _focusRefreshRunning = true;

            _ = FocusRefreshAsync().ContinueWith(t =>
            {
                _focusRefreshRunning = false;
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"Focus refresh failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        public async Task<string> RunSvn(string args)
        {
            string output = await SvnRunner.RunAsync(args, workingDir);

            if (!string.IsNullOrEmpty(output))
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    SVNLogBridge.LogLine(output);
                });
            }

            return output;
        }

        public string ExtractPathFromStatusLine(string line, string statusChar)
        {
            const int prefixLength = 8;
            if (line.Length > prefixLength && line.StartsWith(statusChar))
            {
                return line.Substring(prefixLength).Trim().Replace("\t", string.Empty).Replace('\\', '/');
            }
            return null;
        }

        public async Task CancelBackgroundTasksAsync()
        {
            GetModule<SVNStatus>()?.CancelCurrentRefresh();
            try { Volatile.Read(ref _refreshStatusCts)?.Cancel(); }
            catch (ObjectDisposedException) { }   // stary CTS dysponowany po 1 s
            await Task.Yield();
        }

        // Browse-buttony (SVNExternal) i inne źródła → przez centrum.
        public void BroadcastWorkingDirChange(string path)
        {
            SynchronizeConnectionSettings(SettingsSource.None, workingDirValue: path);
        }

        public void BroadcastSshKeyChange(string newKeyPath)
        {
            SynchronizeConnectionSettings(SettingsSource.None, key: newKeyPath);
        }

        public void BroadcastUrlChange(string newUrl)
        {
            SynchronizeConnectionSettings(SettingsSource.None, url: newUrl);
        }

        public async Task RefreshRepositoryInfo()
        {
            if (!SVNAssetLocator.IsWorkingCopy(WorkingDir)) return;
            string url = await SvnRunner.GetRepoUrlAsync(WorkingDir);
            if (!string.IsNullOrEmpty(url))
            {
                RepositoryUrl = url.Trim();
                UnityMainThreadDispatcher.Enqueue(() =>
                    SynchronizeConnectionSettings(SettingsSource.None, url: RepositoryUrl));
            }
        }

        private void SetupInputListeners()
        {
            // --- SETTINGS → centrum ---
            svnUI.SettingsRepoUrlInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Settings, url: v);
            });

            svnUI.SettingsSshKeyPathInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Settings, key: v);
            });

            svnUI.SettingsWorkingDirInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Settings, workingDirValue: v);
            });

            // --- CHECKOUT → centrum ---
            svnUI.CheckoutRepoUrlInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Checkout, url: v);
            });

            svnUI.CheckoutDestFolderInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Checkout, workingDirValue: v);
            });

            svnUI.CheckoutPrivateKeyInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Checkout, key: v);
            });

            // --- LOAD → centrum (dotąd ZERO listenerów!) ---
            svnUI.LoadRepoUrlInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Load, url: v);
            });

            svnUI.LoadDestFolderInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Load, workingDirValue: v);
            });

            svnUI.LoadPrivateKeyInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                SynchronizeConnectionSettings(SettingsSource.Load, key: v);
            });

            // --- Tools (panel-specyficzne) + debounce zapisu ---
            svnUI.SettingsMergeToolPathInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                MergeToolPath = v.Trim();
                DebounceSaveProject();
            });

            svnUI.SettingsResolveToolPathInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                ResolveToolPath = v.Trim();
                DebounceSaveProject();
            });

            svnUI.SettingsDiffToolPathInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                DiffToolPath = v.Trim();
                DebounceSaveProject();
            });

            svnUI.SettingsBlameToolPathInput?.onValueChanged.AddListener(v =>
            {
                if (_ignoreSync || _isApplyingSnapshot) return;
                BlameToolPath = v.Trim();
                DebounceSaveProject();
            });
        }

        private string SetUser(string user)
        {
            currentUserName = user;
            CachedUserName = user;   // statyka nadąża za detekcją
            return user;
        }

        private static string FilterPathChars(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
                if (!char.IsControl(c) && c != '\u00A0' && c != '\u200B')
                    sb.Append(c);
            return sb.ToString().Trim();
        }
        // setter WorkingDir: workingDir = FilterPathChars(value);
        // CleanPath(path) => FilterPathChars(path);

        private void DebounceSaveProject()
        {
            var newCts = new CancellationTokenSource();
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _saveProjectDebounceCts, newCts));

            Task.Delay(500, newCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    UpdateCurrentProjectDataImmediate();
            }, TaskScheduler.Default);
        }

        // === FIX (wymazywanie danych): zapis NIEZNISZCZAJĄCY — pole opcjonalne
        // zapisywane tylko gdy mamy wartość (pamięć-pustka nie kasuje JSON).
        private void UpdateCurrentProjectDataImmediate()
        {
            try
            {
                ProjectSettings.UpdateProject(WorkingDir, p =>
                {
                    if (!string.IsNullOrWhiteSpace(RepositoryUrl)) p.repoUrl = RepositoryUrl;
                    if (!string.IsNullOrWhiteSpace(CurrentKey)) p.privateKeyPath = CurrentKey;
                    if (!string.IsNullOrWhiteSpace(MergeToolPath)) p.mergeToolPath = MergeToolPath;
                    if (!string.IsNullOrWhiteSpace(DiffToolPath)) p.diffToolPath = DiffToolPath;
                    if (!string.IsNullOrWhiteSpace(ResolveToolPath)) p.resolveToolPath = ResolveToolPath;
                    if (!string.IsNullOrWhiteSpace(BlameToolPath)) p.blameToolPath = BlameToolPath;
                    if (!string.IsNullOrWhiteSpace(SshOptions)) p.sshOptions = SshOptions;
                });
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Save project failed: {ex.Message}");
            }
        }

        public async Task CatAndOpenFile(string relativePath, long revision)
        {
            if (string.IsNullOrEmpty(RepositoryUrl))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Repository URL is missing. Cannot perform 'cat'.");
                return;
            }

            string tempPath = null;
            try
            {
                IsProcessing = true;   // UWAGA: ręczny set — patrz notka o clobberze; usuń, gdy RunToFileAsync strzela OnProcessingStateChanged
                SVNLogBridge.LogLine($"<b>[File]</b> Fetching r{revision}: {relativePath}...", append: true);

                string fileName = Path.GetFileName(relativePath);
                string tempFileName = $"r{revision}_{fileName}";
                string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Cache");

                Directory.CreateDirectory(cacheFolder);   // idempotentne — bez Exists

                tempPath = Path.Combine(cacheFolder, tempFileName);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                // === FIX: jawny błąd zamiast cichej złej ścieżki przy braku roota
                string repoRoot = GetRepoRoot();
                if (string.IsNullOrEmpty(repoRoot))
                {
                    SVNLogBridge.LogErrorToOutput("[SVN] Cannot resolve repository root for 'cat'.");
                    return;
                }

                string fullUrl = repoRoot + (relativePath.StartsWith("/") ? "" : "/") + relativePath;

                // === FIX: escapowanie (cudzysłów w ścieżce łamał komendę)
                string command = $"cat -r {revision} \"{SVNShared.EscapeSvnArg(fullUrl)}\"";
                var (exitCode, error) = await SvnRunner.RunToFileAsync(command, WorkingDir, tempPath);

                if (exitCode != 0)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN] Failed to fetch content for {relativePath} (code {exitCode}): {error?.Trim()}");
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    return;
                }

                CleanupOldCacheFiles(cacheFolder, "r*_");

                string absoluteTempPath = Path.GetFullPath(tempPath);
                // === FIX (spacje!): poprawny file:// URI — "C:\Users\Jan Kowalski\..."
                // łamał OpenURL; podobnie '#' i '%' w nazwie
                string fileUri = new Uri(absoluteTempPath).AbsoluteUri;

                string mergeToolSnapshot = MergeToolPath;

                // === FIX: Application.OpenURL to Unity-API (main only) — przez
                // dispatcher, niezależnie od wątku, z którego przyszliśmy
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(mergeToolSnapshot) && File.Exists(mergeToolSnapshot))
                    {
                        if (SVNShared.TryStartProcess(new System.Diagnostics.ProcessStartInfo()
                        {
                            FileName = mergeToolSnapshot,
                            Arguments = $"\"{absoluteTempPath}\"",
                            UseShellExecute = true
                        },
                             ex =>
                             {
                                 SVNLogBridge.LogErrorToOutput($"[SVN] Failed to open with MergeTool: {ex.Message}. Falling back to default.");
                                 Application.OpenURL(fileUri);
                             }))
                        {
                            SVNLogBridge.LogLine($"<color=green>Opened in editor:</color> {tempFileName}", append: true);
                        }
                    }
                    else
                    {
                        Application.OpenURL(fileUri);
                        SVNLogBridge.LogLine($"<color=yellow>Opened with default app:</color> {tempFileName} (No editor path set)", append: true);
                    }
                });
            }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] CatAndOpenFile error: {e.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private static void CleanupOldCacheFiles(string cacheFolder, string searchPattern)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(cacheFolder, searchPattern))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.CreationTimeUtc < DateTime.UtcNow.AddHours(-24))
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitFileSystemWatcher()
        {
            // === FIX: flaga REALNIE strzeże cooldownu restartu
            if (Interlocked.CompareExchange(ref _restartingWatcher, 0, 0) == 1)
                return;

            try
            {
                DisposeFileSystemWatcher();

                string path = WorkingDir;
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                _folderWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024
                };

                _folderWatcher.Changed += OnDiskEvent;
                _folderWatcher.Created += OnDiskEvent;
                _folderWatcher.Deleted += OnDiskEvent;
                _folderWatcher.Renamed += OnDiskEvent;
                _folderWatcher.Error += OnWatcherError;

                _folderWatcher.EnableRaisingEvents = true;
                SVNLogBridge.LogLine($"[SVN Watcher] Started watching: {path}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN Watcher] Failed to start: {ex.Message}");
            }
        }

        // === FIX: wyciągnięty z lambdy — (a) flaga cooldownu faktycznie USTAWIANA,
        // (b) atomowy swap CTS (Error leci z puli wątków watchera — dwa pod rząd
        // = leaknięty CTS przy zwykłym przypisaniu), (c) delayed dispose przez SVNShared.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            var ex = e.GetException();

            UnityMainThreadDispatcher.Enqueue(() =>
                SVNLogBridge.LogError($"[SVN Watcher] Buffer overflow/error: {ex.Message}. Will restart after cooldown."));

            Interlocked.Exchange(ref _restartingWatcher, 1);

            var newRestart = new CancellationTokenSource();
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _watcherRestartCts, newRestart));

            Task.Delay(TimeSpan.FromSeconds(15), newRestart.Token)
                .ContinueWith(t =>
                {
                    // canceled = teardown lub nadpisany przez nowszy błąd (ten czyści
                    // flagę przy swoim sukcesie)
                    if (!t.IsCanceled)
                    {
                        Interlocked.Exchange(ref _restartingWatcher, 0);
                        UnityMainThreadDispatcher.Enqueue(() => InitFileSystemWatcher());
                    }
                }, TaskScheduler.Default);
        }

        public void DisposeFileSystemWatcher()
        {
            if (_folderWatcher != null)
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Changed -= OnDiskEvent;
                _folderWatcher.Created -= OnDiskEvent;
                _folderWatcher.Deleted -= OnDiskEvent;
                _folderWatcher.Renamed -= OnDiskEvent;
                _folderWatcher.Error -= OnWatcherError;   // === FIX: była lambda — nieodczepialna
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }
        }

        private void OnDiskEvent(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.Contains(".svn")) return;

            Interlocked.Exchange(ref _lastDiskEventTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            DiskChangesDetected = true;
        }

        private string GetSettingWithFallback(string projectValue, string playerPrefsKey)
        {
            // GetSettingWithFallback, pierwsza linia body:
            string normalized = string.IsNullOrWhiteSpace(projectValue)
                ? "" : SVNAssetLocator.NormalizePath(projectValue);

            if (string.IsNullOrWhiteSpace(normalized))
                normalized = SVNPrefs.GetString(playerPrefsKey, "");

            return normalized;
        }

        private async Task<bool> VerifySvnClientInstalledAsync()
        {
            try
            {
                string version = await SvnRunner.RunAsync(
                    "--version --quiet",
                    Path.GetTempPath(),
                    retryOnLock: false,
                    token: CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(version))
                {
                    SvnClientAvailable = false;
                    ReportSvnClientMissing("svn --version returned empty output.");
                }
                else
                {
                    SvnClientAvailable = true;
                    SVNLogBridge.LogToOutput($"<color=green>[SVN] SVN client detected: v{version.Trim()}</color>");
                }
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                SvnClientAvailable = false;
                ReportSvnClientMissing(wex.Message);
            }
            catch (Exception ex)
            {
                SvnClientAvailable = false;
                SVNLogBridge.LogErrorToOutput(
                    $"<color=#FF4444><b>[SVN] svn client check FAILED (client may be broken):</b></color>\n" +
                    $"Details: {ex.Message}\n" +
                    "Check your Subversion installation and PATH.");

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (svnUI?.CheckoutStatusInfoText != null)
                        SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                            "<color=#FF4444><b>ERROR: svn client failed to start.</b></color>\n" +
                            $"Details: {ex.Message}", "Checkout");
                });
            }

            return SvnClientAvailable;
        }

        private void ReportSvnClientMissing(string details)
        {
            SVNLogBridge.LogErrorToOutput(
                "<color=#FF4444><b>[SVN] svn client NOT FOUND!</b></color>\n" +
                "Ensure Subversion is installed and 'svn' is available on PATH.\n" +
                $"Details: {details}");

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.CheckoutStatusInfoText != null)
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        "<color=#FF4444><b>ERROR: svn client not found.</b></color>\n" +
                        "Install Subversion and add it to PATH, then restart.", "Checkout");
            });
        }

        public async Task<WorkingCopyDirtyState> GetWorkingCopyDirtyStateAsync(string path, CancellationToken token = default)
        {
            var state = new WorkingCopyDirtyState();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                state.HasVersionedChanges = true;
                return state;
            }

            try
            {
                string output = await SvnRunner.RunAsync("status", path, token: token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return state;

                using var reader = new StringReader(output);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    char col0 = line[0];
                    char col1 = line.Length > 1 ? line[1] : ' ';

                    if (col0 == '?') state.UnversionedCount++;
                    else if (col0 != ' ' && col0 != 'I' && col0 != 'X')
                        state.HasVersionedChanges = true;

                    if (col1 != ' ')
                        state.HasVersionedChanges = true;

                    if (col0 == 'C' || col1 == 'C')
                        state.ConflictedCount++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Dirty-state check failed: {ex.Message}");
                state.HasVersionedChanges = true;
            }

            return state;
        }

        public async Task<bool> HasLocalModificationsAsync(string workingDir, bool includeUnversioned,
            CancellationToken token = default)
        {
            var state = await GetWorkingCopyDirtyStateAsync(workingDir, token).ConfigureAwait(false);
            return includeUnversioned ? state.IsDirty : state.IsBlockingDirty;
        }

        private void OnDestroy()
        {
            SVN.Core.SvnRunner.OnProcessingStateChanged -= OnSvnProcessingChanged;

            // moduły PRZED Shutdown bridża (Dispose może jeszcze logować)
            foreach (var module in _modules.Values)
            {
                if (module is IDisposable disposable)
                    disposable.Dispose();
            }
            _modules.Clear();

            SVNLogBridge.Shutdown();

            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _lifetimeCts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _refreshStatusCts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _projectSwitchDebounceCts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _watcherRestartCts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _saveProjectDebounceCts, null));

            DisposeFileSystemWatcher();

            _managerLock?.Dispose();              // poprawne — lock jest teraz instancyjny

            if (Instance == this) Instance = null; // fake-null łapie '== null', ale nie '?.'
        }

#if UNITY_EDITOR

        [InitializeOnLoad]
        public static class SvnEditorLifecycle
        {
            static SvnEditorLifecycle()
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }

            private static void OnPlayModeStateChanged(PlayModeStateChange state)
            {
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredPlayMode)
                {
                    SvnProcessTracker.KillAll();
                    SvnRunner.ResetStaticState();
                }
            }
        }
#endif
    }
}