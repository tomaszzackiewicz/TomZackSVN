using SVN.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

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

    private SVNUI svnUI;
    private SVNManager svnManager;
    private List<SVNProject> projects = new List<SVNProject>();
    private SVNProject _projectToRelocate;

    // Thread-safe flag for relocate operation
    private int _isRelocating = 0;

    private static readonly HashSet<string> SvnKeywords = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    {
        "trunk", "branches", "tags"
    };

    void Start()
    {
        svnManager = SVNManager.Instance;
        svnUI = SVNUI.Instance;
        RefreshList();
    }

    private void OnEnable()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        if (svnUI == null) svnUI = SVNUI.Instance;
        RefreshList();
    }

    private void OnDestroy()
    {
        // CRITICAL: Remove listeners from long-lived UI objects to prevent memory leak.
        if (svnUI?.AddProjectRepoUrlInput != null)
            svnUI.AddProjectRepoUrlInput.onEndEdit.RemoveListener(OnUrlInputEndEdit);
    }

    public void RefreshList()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        projects = ProjectSettings.LoadProjects();

        // Collect children to destroy
        var toDestroy = new List<GameObject>(container.childCount);
        foreach (Transform child in container)
            toDestroy.Add(child.gameObject);

        foreach (var go in toDestroy)
            Destroy(go);

        if (projectButtonPrefab == null || container == null) return;

        foreach (var project in projects)
        {
            GameObject itemObj = Instantiate(projectButtonPrefab, container);
            ProjectUIItem uiItem = itemObj.GetComponent<ProjectUIItem>();

            if (uiItem != null)
            {
                uiItem.projectNameText.text = project.projectName;
                uiItem.selectButton.onClick.AddListener(() => OnProjectSelected(project));
                uiItem.deleteButton.onClick.AddListener(() => Button_DeleteProject(project));

                if (uiItem.relocateButton != null)
                    uiItem.relocateButton.onClick.AddListener(() => Button_OpenRelocatePanel(project));
            }
        }
    }

    private void OnProjectSelected(SVNProject project)
    {
        if (project == null || svnManager == null || !svnManager.isActiveAndEnabled) return;
        if (svnManager.IsProcessing)
        {
            SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
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
        await svnManager.CancelBackgroundTasksAsync();
        svnManager.CurrentSnapshot = null;
        svnManager.IsUpdateRunning = false;

        try
        {
            var statusModule = svnManager.GetModule<SVNStatus>();
            var settingsModule = svnManager.GetModule<SVNSettings>();
            statusModule?.ClearCurrentData();
            statusModule?.ClearSVNTreeView();
            svnManager.CurrentKey = string.IsNullOrWhiteSpace(project.privateKeyPath) ? "" : project.privateKeyPath;
            await svnManager.LoadProject(project);

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this != null)
                    gameObject.SetActive(false);
            });

            settingsModule?.UpdateUIFromManager();
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[ProjectSelection] OnProjectSelected failed: {ex}");
        }
    }

    public void Button_OpenAddProjectPanel()
    {
        if (addProjectSubPanel != null)
        {
            addProjectSubPanel.SetActive(true);
            var ui = SVNUI.Instance;
            if (ui == null) return;

            ui.AddProjectNameInput.text = "";
            ui.AddProjectRepoUrlInput.text = "";
            ui.AddProjectFolderPathInput.text = "";
            ui.AddProjectKeyPathInput.text = "";

            // Safe double-listen prevention
            ui.AddProjectRepoUrlInput.onEndEdit.RemoveListener(OnUrlInputEndEdit);
            ui.AddProjectRepoUrlInput.onEndEdit.AddListener(OnUrlInputEndEdit);
        }
    }

    public void Button_BrowseDestFolder() => svnManager.GetModule<SVNExternal>()?.BrowseDestinationFolderPathAdd();
    public void Button_BrowsePrivateKey() => svnManager.GetModule<SVNExternal>()?.BrowsePrivateKeyPathAdd();

    public void Button_CloseAddProjectPanel()
    {
        if (addProjectSubPanel != null) addProjectSubPanel.SetActive(false);
    }

    public void Button_CloseRelocateProjectPanel()
    {
        if (relocateProjectSubPanel != null) relocateProjectSubPanel.SetActive(false);
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
            string[] segments = cleanedUrl.Split(new char[] { '/', '\\' }, System.StringSplitOptions.RemoveEmptyEntries);
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
        catch (Exception e) { SVNLogBridge.LogError($"[SVN] URL Parse failed: {e.Message}"); }
        return "New Project";
    }

    private void AddNewProject(string name, string url, string path, string key)
    {
        string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
        var newProj = new SVNProject
        {
            projectName = name,
            repoUrl = url,
            workingDir = normalizedPath,
            privateKeyPath = key,
            lastOpened = DateTime.UtcNow
        };
        List<SVNProject> currentList = ProjectSettings.LoadProjects();
        int existingIndex = currentList.FindIndex(p => p.workingDir == normalizedPath);
        if (existingIndex != -1) currentList[existingIndex] = newProj;
        else currentList.Add(newProj);
        ProjectSettings.SaveProjects(currentList);
        RefreshList();
        Button_CloseAddProjectPanel();
        OnProjectSelected(newProj);
    }

    public void Button_DeleteProject(SVNProject project)
    {
        if (project == null) return;

        ProjectSettings.DeleteProject(project.workingDir);
        if (PlayerPrefs.GetString("SVN_LastOpenedProjectPath") == project.workingDir)
            PlayerPrefs.DeleteKey("SVN_LastOpenedProjectPath");

        RefreshList();
    }

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
            SVNLogBridge.LogLine("<color=orange>Relocate already in progress...</color>");
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
            SVNLogBridge.LogLine("<color=orange>New URL is the same as current. No changes made.</color>");
            Interlocked.Exchange(ref _isRelocating, 0);
            Button_CancelRelocate();
            return;
        }

        var projectToRelocate = _projectToRelocate; // capture for closure
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

            // SECURITY: Validate URL
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

            // Wrap URL in quotes to handle spaces safely
            string result = await SvnRunner.RunAsync($"relocate \"{newUrl}\"", project.workingDir);
            SVNLogBridge.LogLine($"<color=green>Relocated successfully to {newUrl}</color>");

            var projects = ProjectSettings.LoadProjects();
            string normalizedDir = project.workingDir.Replace("\\", "/").TrimEnd('/');
            var existing = projects.Find(p => !string.IsNullOrEmpty(p.workingDir) && p.workingDir.Replace("\\", "/").TrimEnd('/') == normalizedDir);

            if (existing != null)
            {
                existing.repoUrl = newUrl;
                ProjectSettings.SaveProjects(projects);
            }

            if (svnManager?.CurrentProject?.workingDir == project.workingDir)
            {
                svnManager.RepositoryUrl = newUrl;
            }

            UnityMainThreadDispatcher.Enqueue(() => RefreshList());
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"Relocate failed: {ex.Message}");
        }
    }
}