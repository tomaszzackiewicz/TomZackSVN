using SVN.Core;
using System.Collections.Generic;
using UnityEngine;
using System;
using TMPro;

public class ProjectSelectionPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject projectButtonPrefab;
    [SerializeField] private Transform container;

    [Header("Add Project UI Container")]
    [SerializeField] private GameObject addProjectSubPanel;

    // ===== NOWE POLA DLA RELOCATE =====
    [Header("Relocate Panel")]
    [SerializeField] private GameObject relocateProjectSubPanel;          // nowy sub‑panel (podobny do addProjectSubPanel)
    [SerializeField] private TMP_InputField relocateNewUrlInput;    // pole na nowy URL

    private SVNUI svnUI;
    private SVNManager svnManager;
    private List<SVNProject> projects = new List<SVNProject>();
    private SVNProject _projectToRelocate;                      // aktualnie relokowany projekt

    void Start()
    {
        svnManager = SVNManager.Instance;
        svnUI = SVNUI.Instance;
        RefreshList();
    }

    private void OnEnable()
    {
        RefreshList();
    }

    public void RefreshList()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        projects = ProjectSettings.LoadProjects();

        foreach (Transform child in container)
            Destroy(child.gameObject);

        foreach (var project in projects)
        {
            GameObject itemObj = Instantiate(projectButtonPrefab, container);
            ProjectUIItem uiItem = itemObj.GetComponent<ProjectUIItem>();

            if (uiItem != null)
            {
                uiItem.projectNameText.text = project.projectName;
                uiItem.selectButton.onClick.AddListener(() => OnProjectSelected(project));
                uiItem.deleteButton.onClick.AddListener(() => Button_DeleteProject(project));

                // NOWA LINIA – podpięcie przycisku Relocate
                if (uiItem.relocateButton != null)
                    uiItem.relocateButton.onClick.AddListener(() => Button_OpenRelocatePanel(project));
            }
        }
    }

    // ----------------------------------------------------------------
    // Istniejące metody (bez zmian)
    // ----------------------------------------------------------------
    private async void OnProjectSelected(SVNProject project)
    {
        if (project == null || svnManager == null || !svnManager.isActiveAndEnabled) return;
        if (svnManager.IsProcessing)
        {
            SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
            return;
        }

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
            gameObject.SetActive(false);
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
            ui.AddProjectNameInput.text = "";
            ui.AddProjectRepoUrlInput.text = "";
            ui.AddProjectFolderPathInput.text = "";
            ui.AddProjectKeyPathInput.text = "";
            ui.AddProjectRepoUrlInput.onEndEdit.RemoveListener(OnUrlInputEndEdit);
            ui.AddProjectRepoUrlInput.onEndEdit.AddListener(OnUrlInputEndEdit);
        }
    }

    public void Button_BrowseDestFolder() => svnManager.GetModule<SVNExternal>().BrowseDestinationFolderPathAdd();
    public void Button_BrowsePrivateKey() => svnManager.GetModule<SVNExternal>().BrowsePrivateKeyPathAdd();

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
            string[] segments = cleanedUrl.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                string lastSegment = segments[segments.Length - 1];
                List<string> svnKeywords = new List<string> { "trunk", "branches", "tags" };
                if (svnKeywords.Contains(lastSegment.ToLower()) && segments.Length > 1)
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
        var newProj = new SVNProject { projectName = name, repoUrl = url, workingDir = normalizedPath, privateKeyPath = key, lastOpened = DateTime.Now };
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
        ProjectSettings.DeleteProject(project.workingDir);
        if (PlayerPrefs.GetString("SVN_LastOpenedProjectPath") == project.workingDir)
            PlayerPrefs.DeleteKey("SVN_LastOpenedProjectPath");
        RefreshList();
    }

    // ----------------------------------------------------------------
    // NOWE METODY DLA RELOCATE
    // ----------------------------------------------------------------
    public void Button_OpenRelocatePanel(SVNProject project)
    {
        if (project == null || string.IsNullOrEmpty(project.workingDir)) return;

        _projectToRelocate = project;
        if (relocateNewUrlInput != null)
            relocateNewUrlInput.text = project.repoUrl;   // pokaż aktualny URL

        if (relocateProjectSubPanel != null)
            relocateProjectSubPanel.SetActive(true);                // panel pokazuje się nad listą, lista pozostaje widoczna
    }

    public void Button_ConfirmRelocate()
    {
        if (_projectToRelocate == null) return;
        string newUrl = relocateNewUrlInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(newUrl))
        {
            SVNLogBridge.LogError("New URL cannot be empty.");
            return;
        }

        _ = ExecuteRelocateAsync(_projectToRelocate, newUrl);
        Button_CancelRelocate();   // zamknij panel po zatwierdzeniu
    }

    public void Button_CancelRelocate()
    {
        _projectToRelocate = null;
        if (relocateProjectSubPanel != null) relocateProjectSubPanel.SetActive(false);
    }

    private async System.Threading.Tasks.Task ExecuteRelocateAsync(SVNProject project, string newUrl)
    {
        try
        {
            // Walidacja, czy katalog roboczy istnieje
            if (!System.IO.Directory.Exists(project.workingDir))
            {
                SVNLogBridge.LogError($"Working directory not found: {project.workingDir}");
                return;
            }

            string result = await SvnRunner.RunAsync($"relocate {newUrl}", project.workingDir);
            SVNLogBridge.LogLine($"<color=green>Relocated successfully to {newUrl}</color>");

            // Aktualizacja URL w zapisanych projektach
            var projects = ProjectSettings.LoadProjects();
            var existing = projects.Find(p => p.workingDir == project.workingDir);
            if (existing != null)
            {
                existing.repoUrl = newUrl;
                ProjectSettings.SaveProjects(projects);
            }

            RefreshList();   // odśwież listę, aby pokazać nowy URL
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"Relocate failed: {ex.Message}");
        }
    }
}