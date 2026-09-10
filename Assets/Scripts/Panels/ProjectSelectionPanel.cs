using SVN.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class ProjectSelectionPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject projectButtonPrefab;
    [SerializeField] private Transform container;

    [Header("Add Project UI Container")]
    [SerializeField] private GameObject addProjectSubPanel;

    [Header("Relocate Panel")]
    [SerializeField] private GameObject relocateProjectSubPanel;
    [SerializeField] private TMP_InputField relocateNewUrlInput;

    [Header("Rename Panel")]
    [SerializeField] private GameObject renameProjectSubPanel;
    [SerializeField] private TMP_InputField renameNewNameInput;

    [Header("Search & Sort")]
    [SerializeField] private TMP_InputField searchInput;
    [SerializeField] private TMP_Dropdown sortDropdown;

    private SVNUI svnUI;
    private SVNManager svnManager;
    private List<SVNProject> projects = new List<SVNProject>();
    private SVNProject _projectToRelocate;
    private SVNProject _projectToRename;

    private int _isRelocating = 0;
    private int _isRenaming = 0;

    private List<SVNProject> _allProjects = new List<SVNProject>();
    private bool _dataDirty = true;

    private string _renderFingerprint;

    private UnityAction<string> _onSearchChanged;
    private UnityAction<int> _onSortChanged;

    private static readonly SemaphoreSlim _jsonLock = new SemaphoreSlim(1, 1);

    private static readonly HashSet<string> SvnKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "trunk", "branches", "tags"
    };

    private void Start()
    {
        svnManager = SVNManager.Instance;
        svnUI = SVNUI.Instance;

        if (sortDropdown != null && sortDropdown.options.Count == 0)
        {
            sortDropdown.ClearOptions();
            sortDropdown.AddOptions(new List<string> { "Name", "Last Opened" });
            sortDropdown.value = 0;
        }

        _onSearchChanged = _ => RenderList();
        _onSortChanged = _ => RenderList();

        if (searchInput != null)
            searchInput.onValueChanged.AddListener(_onSearchChanged);
        if (sortDropdown != null)
            sortDropdown.onValueChanged.AddListener(_onSortChanged);

        if (svnManager != null)
            svnManager.OnProjectChanged += OnProjectLoaded;

        if (_dataDirty)
        {
            ReloadData();
            RenderList();
        }
    }

    private void OnEnable()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        if (svnUI == null) svnUI = SVNUI.Instance;

        if (_dataDirty)
            ReloadData();

        RenderList();
    }

    private void OnProjectLoaded(SVNProject project)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this != null)
                RefreshList();
        });
    }

    private void OnDestroy()
    {
        if (svnUI?.AddProjectRepoUrlInput != null)
            svnUI.AddProjectRepoUrlInput.onEndEdit.RemoveListener(OnUrlInputEndEdit);

        if (svnUI?.AddProjectKeyPathInput != null)
            svnUI.AddProjectKeyPathInput.onEndEdit.RemoveListener(OnAddProjectKeyEndEdit);

        if (searchInput != null && _onSearchChanged != null)
            searchInput.onValueChanged.RemoveListener(_onSearchChanged);
        if (sortDropdown != null && _onSortChanged != null)
            sortDropdown.onValueChanged.RemoveListener(_onSortChanged);

        if (svnManager != null)
            svnManager.OnProjectChanged -= OnProjectLoaded;
    }

    private void ReloadData()
    {
        if (!isActiveAndEnabled)
        {
            _dataDirty = true;
            return;
        }

        var allProjects = ProjectSettings.LoadProjects();

        bool needsMigrationSave = false;
        int i = 0;
        foreach (var p in allProjects)
        {
            if (p.lastOpened == default(DateTime) || p.lastOpened.Year < 2000)
            {
                p.lastOpened = DateTime.UtcNow.AddSeconds(-i);
                needsMigrationSave = true;
            }
            i++;
        }

        if (needsMigrationSave)
        {
            try { ProjectSettings.SaveProjects(allProjects); }
            catch { }
        }

        _allProjects = allProjects;
        _dataDirty = false;
    }

    private void RenderList()
    {
        if (container == null) return;
        if (!isActiveAndEnabled) return;

        bool sortByDate = sortDropdown != null && sortDropdown.value == 1;

        IEnumerable<SVNProject> source = _allProjects;
        source = sortByDate
            ? source.OrderByDescending(p => p.lastOpened)
            : source.OrderBy(p => p.projectName);

        string filter = searchInput?.text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(filter))
            source = source.Where(p => p.projectName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        projects = source.ToList();

        string fingerprint = string.Join("|",
            projects.Select(p => $"{p.projectName}|{p.lastOpened.Ticks}"));

        if (fingerprint == _renderFingerprint && container.childCount > 0)
            return;

        _renderFingerprint = fingerprint;

        var toDestroy = new List<GameObject>(container.childCount);
        foreach (Transform child in container)
            toDestroy.Add(child.gameObject);
        foreach (var go in toDestroy)
            Destroy(go);

        if (projectButtonPrefab == null) return;

        foreach (var project in projects)
        {
            GameObject itemObj = Instantiate(projectButtonPrefab, container);
            ProjectUIItem uiItem = itemObj.GetComponent<ProjectUIItem>();

            if (uiItem != null)
            {
                uiItem.projectNameText.text = project.projectName;

                if (uiItem.dateText != null)
                    uiItem.dateText.text = project.lastOpened.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                uiItem.selectButton.onClick.AddListener(() => OnProjectSelected(project));
                uiItem.deleteButton.onClick.AddListener(() => Button_DeleteProject(project));

                if (uiItem.relocateButton != null)
                    uiItem.relocateButton.onClick.AddListener(() => Button_OpenRelocatePanel(project));

                if (uiItem.renameButton != null)
                    uiItem.renameButton.onClick.AddListener(() => Button_OpenRenamePanel(project));
            }
        }
    }

    public void RefreshList()
    {
        ReloadData();
        RenderList();
    }

    #region Rename

    public void Button_OpenRenamePanel(SVNProject project)
    {
        if (project == null) return;
        _projectToRename = project;

        if (renameNewNameInput != null)
            renameNewNameInput.text = project.projectName;

        if (renameProjectSubPanel != null)
            renameProjectSubPanel.SetActive(true);
    }

    public void Button_ConfirmRename()
    {
        if (_projectToRename == null) return;

        if (Interlocked.CompareExchange(ref _isRenaming, 1, 0) == 1)
        {
            SVNLogBridge.LogLine("<color=#8B0000>>Rename already in progress...</color>");
            return;
        }

        string newName = renameNewNameInput?.text?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            SVNLogBridge.LogError("Project name cannot be empty.");
            Interlocked.Exchange(ref _isRenaming, 0);
            return;
        }

        var projectToRename = _projectToRename;
        _ = ExecuteRenameAsync(projectToRename, newName).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _isRenaming, 0);
            if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
            {
                var msg = t.Exception.InnerException?.Message ?? "Unknown error";
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogError($"[Rename] Failed: {msg}"));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        Button_CancelRename();
    }

    public void Button_CancelRename()
    {
        _projectToRename = null;
        if (renameProjectSubPanel != null) renameProjectSubPanel.SetActive(false);
    }

    private async Task ExecuteRenameAsync(SVNProject project, string newName)
    {
        try
        {
            await _jsonLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var projectsList = ProjectSettings.LoadProjects();
                string normalizedDir = project.workingDir.Replace("\\", "/").TrimEnd('/');
                var existing = projectsList.Find(p =>
                    !string.IsNullOrEmpty(p.workingDir) &&
                    string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'), normalizedDir, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.projectName = newName;
                    ProjectSettings.SaveProjects(projectsList);
                    SVNLogBridge.LogLine($"<color=green>Project renamed to:</color> {newName}");

                    if (svnManager != null && svnManager.CurrentProject != null)
                    {
                        string currentNormalizedDir = svnManager.CurrentProject.workingDir.Replace("\\", "/").TrimEnd('/');
                        if (string.Equals(currentNormalizedDir, normalizedDir, StringComparison.OrdinalIgnoreCase))
                        {
                            svnManager.CurrentProject.projectName = newName;
                        }
                    }
                }
                else
                {
                    SVNLogBridge.LogError("Project not found in list.");
                }
            }
            finally
            {
                _jsonLock.Release();
            }
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"Rename failed: {ex.Message}");
        }
        finally
        {
            _dataDirty = true;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this != null)
                    RefreshList();
            });
        }
    }

    #endregion

    #region Relocate

    public void Button_OpenRelocatePanel(SVNProject project)
    {
        if (project == null || string.IsNullOrEmpty(project.workingDir)) return;

        _projectToRelocate = project;
        if (relocateNewUrlInput != null)
            relocateNewUrlInput.text = project.repoUrl;

        if (relocateProjectSubPanel != null)
            relocateProjectSubPanel.SetActive(true);
    }

    public void Button_ConfirmRelocate()
    {
        if (_projectToRelocate == null) return;

        if (Interlocked.CompareExchange(ref _isRelocating, 1, 0) == 1)
        {
            SVNLogBridge.LogLine("<color=#8B0000>>Relocate already in progress...</color>");
            return;
        }

        string newUrl = relocateNewUrlInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(newUrl))
        {
            SVNLogBridge.LogError("New URL cannot be empty.");
            Interlocked.Exchange(ref _isRelocating, 0);
            return;
        }

        if (newUrl == _projectToRelocate.repoUrl)
        {
            SVNLogBridge.LogLine("<color=#8B0000>>New URL is the same as current. No changes made.</color>");
            Interlocked.Exchange(ref _isRelocating, 0);
            Button_CancelRelocate();
            return;
        }

        var projectToRelocate = _projectToRelocate;
        _ = ExecuteRelocateAsync(projectToRelocate, newUrl).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _isRelocating, 0);
            if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
            {
                var msg = t.Exception.InnerException?.Message ?? "Unknown error";
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogError($"[Relocate] Failed: {msg}"));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        Button_CancelRelocate();
    }

    public void Button_CancelRelocate()
    {
        _projectToRelocate = null;
        if (relocateProjectSubPanel != null) relocateProjectSubPanel.SetActive(false);
    }

    private async Task ExecuteRelocateAsync(SVNProject project, string newUrl)
    {
        try
        {
            if (!Directory.Exists(project.workingDir))
            {
                SVNLogBridge.LogError($"Working directory not found: {project.workingDir}");
                return;
            }

            if (!Uri.TryCreate(newUrl, UriKind.Absolute, out _))
            {
                SVNLogBridge.LogError("Invalid repository URL format.");
                return;
            }

            if (newUrl.Contains("\""))
            {
                SVNLogBridge.LogError("Repository URL contains invalid characters.");
                return;
            }

            await SvnRunner.RunAsync($"relocate \"{newUrl}\"", project.workingDir).ConfigureAwait(false);
            SVNLogBridge.LogLine($"<color=green>Relocated successfully to {newUrl}</color>");

            await _jsonLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var projectsList = ProjectSettings.LoadProjects();
                string normalizedDir = project.workingDir.Replace("\\", "/").TrimEnd('/');
                var existing = projectsList.Find(p =>
                    !string.IsNullOrEmpty(p.workingDir) &&
                    string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'), normalizedDir, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.repoUrl = newUrl;
                    ProjectSettings.SaveProjects(projectsList);
                }
            }
            finally
            {
                _jsonLock.Release();
            }

            // === FIX: przez centrum — unieważnia cache filtrów RepoBrowsera
            // i syncuje wszystkie pola UI (Settings / Checkout / Load).
            if (svnManager != null &&
                svnManager.CurrentProject != null &&
                string.Equals(
                    svnManager.CurrentProject.workingDir.Replace("\\", "/").TrimEnd('/'),
                    project.workingDir.Replace("\\", "/").TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                svnManager.CurrentProject.repoUrl = newUrl;
                UnityMainThreadDispatcher.Enqueue(() =>
                    svnManager.SynchronizeConnectionSettings(SVNManager.SettingsSource.None, url: newUrl));
            }

            _dataDirty = true;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this != null)
                    RefreshList();
            });
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"Relocate failed: {ex.Message}");
        }
    }

    #endregion

    #region Project Selection

    private void OnProjectSelected(SVNProject project)
    {
        if (project == null || svnManager == null || !svnManager.isActiveAndEnabled) return;
        if (svnManager.IsProcessing)
        {
            SVNLogBridge.LogLine("<color=#8B0000>>Another operation is running. Please wait.</color>");
            return;
        }

        _ = OnProjectSelectedAsync(project).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
            {
                var msg = t.Exception.InnerException?.Message ?? "Unknown error";
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogError($"[ProjectSelection] OnProjectSelected failed: {msg}"));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async Task OnProjectSelectedAsync(SVNProject project)
    {
        await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
        svnManager.CurrentSnapshot = null;
        svnManager.IsUpdateRunning = false;

        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (svnUI == null) return;

            SVNLogBridge.LogLine("", append: false);

            var graphModule = svnManager?.GetModule<SVNRevGraph>();
            graphModule?.ClearGraph();

            var statusModule = svnManager?.GetModule<SVNStatus>();
            statusModule?.ClearCurrentData();
            statusModule?.ClearSVNTreeView();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>Loading changes...</i>", "TREE", append: false);
        });

        ProjectSettings.UpdateProject(project.workingDir, p => p.lastOpened = DateTime.UtcNow);
        _dataDirty = true;

        try
        {
            var settingsModule = svnManager.GetModule<SVNSettings>();

            // === FIX: NIE czyścimy ręcznie CurrentKey. LoadProjectCore robi
            // to poprawnie — gdy projekt ma pusty privateKeyPath, zachowuje
            // poprzednio zapisany klucz z SvnRunner.KeyPath.
            bool loaded = await svnManager.LoadProject(project).ConfigureAwait(false);
            if (!loaded)
            {
                SVNLogBridge.LogError(
                    $"[ProjectSelection] '{project.projectName}' could not be loaded — working copy missing/invalid at: {project.workingDir}");

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (this != null)
                        RefreshList();
                });
                return;
            }

            svnManager.RaiseProjectChanged(project);

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this != null)
                    gameObject.SetActive(false);
            });

            settingsModule?.UpdateUIFromManager();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    await svnManager.RefreshStatus(force: true).ConfigureAwait(false);
                }
                catch (Exception refreshEx)
                {
                    SVNLogBridge.LogError($"[ProjectSelection] Post-load refresh failed: {refreshEx.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[ProjectSelection] OnProjectSelected failed: {ex}");
        }
    }

    #endregion

    #region Add Project

    public void Button_OpenAddProjectPanel()
    {
        if (addProjectSubPanel != null)
        {
            addProjectSubPanel.SetActive(true);
            var ui = SVNUI.Instance;
            if (ui == null) return;

            // === FIX: przy otwieraniu panelu wypełnij go AKTUALNYM stanem
            // managera — nie zostawiaj pustych pól ani starych wartości po
            // poprzednim projekcie.
            string currentUrl = svnManager?.RepositoryUrl ?? "";
            string currentKey = svnManager?.CurrentKey ?? "";

            ui.AddProjectNameInput.SetTextWithoutNotify("");
            ui.AddProjectRepoUrlInput.SetTextWithoutNotify(currentUrl);
            ui.AddProjectFolderPathInput.SetTextWithoutNotify("");
            ui.AddProjectKeyPathInput.SetTextWithoutNotify(currentKey);

            ui.AddProjectRepoUrlInput.onEndEdit.RemoveListener(OnUrlInputEndEdit);
            ui.AddProjectRepoUrlInput.onEndEdit.AddListener(OnUrlInputEndEdit);

            ui.AddProjectKeyPathInput.onEndEdit.RemoveListener(OnAddProjectKeyEndEdit);
            ui.AddProjectKeyPathInput.onEndEdit.AddListener(OnAddProjectKeyEndEdit);
        }
    }

    private void OnAddProjectKeyEndEdit(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (svnManager == null) return;

        svnManager.SynchronizeConnectionSettings(SVNManager.SettingsSource.None, key: key);
    }

    public void Button_BrowseDestFolder() => svnManager.GetModule<SVNExternal>()?.BrowseDestinationFolderPathAdd();
    public void Button_BrowsePrivateKey() => svnManager.GetModule<SVNExternal>()?.BrowsePrivateKeyPathAdd();

    public void Button_CloseAddProjectPanel()
    {
        if (addProjectSubPanel != null) addProjectSubPanel.SetActive(false);
    }

    public void Button_SaveNewProject()
    {
        string name = svnUI.AddProjectNameInput.text;
        string url = svnUI.AddProjectRepoUrlInput.text;
        string path = svnUI.AddProjectFolderPathInput.text;
        string key = svnUI.AddProjectKeyPathInput.text;

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
        {
            name = GetProjectNameFromUrl(url);
            svnUI.AddProjectNameInput.text = name;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            SVNLogBridge.LogError("Project name and path are required!");
            return;
        }

        if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            SVNLogBridge.LogError("Invalid repository URL format.");
            return;
        }

        AddNewProject(name, url, path, key);
    }

    public void OnUrlInputEndEdit(string url)
    {
        if (string.IsNullOrWhiteSpace(svnUI.AddProjectNameInput.text) && !string.IsNullOrWhiteSpace(url))
            svnUI.AddProjectNameInput.text = GetProjectNameFromUrl(url);
    }

    private string GetProjectNameFromUrl(string url)
    {
        try
        {
            string cleanedUrl = url.Trim().TrimEnd('/', '\\');
            string[] segments = cleanedUrl.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                string lastSegment = segments[segments.Length - 1];
                if (SvnKeywords.Contains(lastSegment) && segments.Length > 1)
                    lastSegment = segments[segments.Length - 2];
                if (lastSegment.EndsWith(".git")) lastSegment = lastSegment.Substring(0, lastSegment.Length - 4);
                if (lastSegment.EndsWith(".svn")) lastSegment = lastSegment.Substring(0, lastSegment.Length - 4);
                return lastSegment;
            }
        }
        catch (Exception e) { SVNLogBridge.LogErrorToOutput($"[SVN] URL Parse failed: {e.Message}"); }
        return "New Project";
    }

    private void AddNewProject(string name, string url, string path, string key)
    {
        string normalizedPath = path.Replace("\\", "/").TrimEnd('/');

        ProjectSettings.AddOrUpdateProject(normalizedPath, (p, created) =>
        {
            p.projectName = name;
            p.repoUrl = url;
            p.privateKeyPath = key;
            p.lastOpened = DateTime.UtcNow;
        });

        _dataDirty = true;
        RefreshList();
        Button_CloseAddProjectPanel();

        var newProj = new SVNProject
        {
            projectName = name,
            repoUrl = url,
            workingDir = normalizedPath,
            privateKeyPath = key,
            lastOpened = DateTime.UtcNow
        };
        OnProjectSelected(newProj);
    }

    #endregion

    #region Delete

    public void Button_DeleteProject(SVNProject project)
    {
        if (project == null) return;

        ProjectSettings.DeleteProject(project.workingDir);
        if (PlayerPrefs.GetString("SVN_LastOpenedProjectPath") == project.workingDir)
            PlayerPrefs.DeleteKey("SVN_LastOpenedProjectPath");

        _dataDirty = true;
        RefreshList();
    }

    #endregion
}