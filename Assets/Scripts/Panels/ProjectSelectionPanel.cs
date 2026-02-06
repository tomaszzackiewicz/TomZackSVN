using SVN.Core;
using System.Collections.Generic;
using UnityEngine;
using System;

public class ProjectSelectionPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject projectButtonPrefab;
    [SerializeField] private Transform container;

    [Header("Add Project UI Container")]
    [SerializeField] private GameObject addProjectSubPanel;

    private SVNUI svnUI;
    private SVNManager svnManager;

    private List<SVNProject> projects = new List<SVNProject>();

    void Start()
    {
        svnManager = SVNManager.Instance;
        svnUI = SVNUI.Instance;

        RefreshList();
    }

    public void RefreshList()
    {
        projects = ProjectSettings.LoadProjects();

        foreach (Transform child in container)
        {
            Destroy(child.gameObject);
        }

        foreach (var project in projects)
        {
            GameObject itemObj = Instantiate(projectButtonPrefab, container);
            ProjectUIItem uiItem = itemObj.GetComponent<ProjectUIItem>();

            if (uiItem != null)
            {
                uiItem.projectNameText.text = project.projectName;
                uiItem.selectButton.onClick.AddListener(() => OnProjectSelected(project));
                uiItem.deleteButton.onClick.AddListener(() => Button_DeleteProject(project));
            }
        }
    }

    private void OnProjectSelected(SVNProject project)
    {
        svnManager.LoadProject(project);
        this.gameObject.SetActive(false);
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
    public void Button_BrowseDestFolder() => svnManager.SVNExternal.BrowseNewProjectFolder();

    public void Button_BrowsePrivateKey() => svnManager.SVNExternal.BrowseNewProjectPrivateKey();

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
            Debug.LogError("Project name and path are required!");
            return;
        }

        AddNewProject(name, url, path, key);
    }

    // Add this method to handle the event
    public void OnUrlInputEndEdit(string url)
    {
        // Only auto-fill if the name field is currently empty
        if (string.IsNullOrWhiteSpace(svnUI.AddProjectNameInput.text) && !string.IsNullOrWhiteSpace(url))
        {
            svnUI.AddProjectNameInput.text = GetProjectNameFromUrl(url);
        }
    }

    private string GetProjectNameFromUrl(string url)
    {
        try
        {
            // 1. Clean up the URL
            string cleanedUrl = url.Trim().TrimEnd('/', '\\');

            // 2. Split by slashes to look at segments
            string[] segments = cleanedUrl.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > 0)
            {
                // 3. Get the last segment
                string lastSegment = segments[segments.Length - 1];

                // 4. If the last segment is a standard SVN folder, take the one before it
                List<string> svnKeywords = new List<string> { "trunk", "branches", "tags" };
                if (svnKeywords.Contains(lastSegment.ToLower()) && segments.Length > 1)
                {
                    lastSegment = segments[segments.Length - 2];
                }

                // 5. Remove common extensions
                if (lastSegment.EndsWith(".git")) lastSegment = lastSegment.Substring(0, lastSegment.Length - 4);
                if (lastSegment.EndsWith(".svn")) lastSegment = lastSegment.Substring(0, lastSegment.Length - 4);

                return lastSegment;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SVN] URL Parse failed: {e.Message}");
        }

        return "";
    }

    private void AddNewProject(string name, string url, string path, string key)
    {
        var newProj = new SVNProject
        {
            projectName = name,
            repoUrl = url,
            workingDir = path,
            privateKeyPath = key,
            lastOpened = DateTime.Now
        };

        List<SVNProject> currentList = ProjectSettings.LoadProjects();
        currentList.Add(newProj);
        ProjectSettings.SaveProjects(currentList);

        RefreshList();
        Button_CloseAddProjectPanel();
        OnProjectSelected(newProj);
    }

    public void Button_DeleteProject(SVNProject project)
    {
        ProjectSettings.DeleteProject(project.workingDir);

        if (PlayerPrefs.GetString("SVN_LastOpenedProjectPath") == project.workingDir)
        {
            PlayerPrefs.DeleteKey("SVN_LastOpenedProjectPath");
        }

        RefreshList();
    }
}