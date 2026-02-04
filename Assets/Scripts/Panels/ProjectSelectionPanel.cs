using SVN.Core;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

public class ProjectSelectionPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject projectButtonPrefab;
    [SerializeField] private Transform container;
    [SerializeField] private GameObject mainUIPanel;

    [Header("Add Project UI Container")]
    [SerializeField] private GameObject addProjectSubPanel;

    private SVNManager svnManager;

    private List<SVNProject> projects = new List<SVNProject>();

    void Start()
    {
        svnManager = SVNManager.Instance;
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
        SVNManager.Instance.LoadProject(project);
        if (mainUIPanel != null) mainUIPanel.SetActive(true);
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
        var ui = SVNUI.Instance;

        string name = ui.AddProjectNameInput.text;
        string url = ui.AddProjectRepoUrlInput.text;
        string path = ui.AddProjectFolderPathInput.text;
        string key = ui.AddProjectKeyPathInput.text;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            Debug.LogError("Nazwa i œcie¿ka s¹ wymagane!");
            return;
        }

        AddNewProject(name, url, path, key);
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