using SVN.Core;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

public class ProjectSelectionPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject projectButtonPrefab; // Prefab przycisku projektu
    [SerializeField] private Transform container;            // Content w ScrollRect
    [SerializeField] private GameObject mainUIPanel;         // G³ówny panel aplikacji

    [Header("Add Project UI")]
    [SerializeField] private GameObject addProjectSubPanel; // Okienko formularza
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private TMP_InputField urlInput;
    [SerializeField] private TMP_InputField pathInput;
    [SerializeField] private TMP_InputField keyInput;

    private List<SVNProject> projects = new List<SVNProject>();

    void Start()
    {
        RefreshList();
    }

    public void RefreshList()
    {
        projects = ProjectSettings.LoadProjects();

        // Czyszczenie listy
        foreach (Transform child in container)
        {
            Destroy(child.gameObject);
        }

        foreach (var project in projects)
        {
            // Spawnowanie ca³ego paska (itemu)
            GameObject itemObj = Instantiate(projectButtonPrefab, container);
            ProjectUIItem uiItem = itemObj.GetComponent<ProjectUIItem>();

            if (uiItem != null)
            {
                // Ustawienie nazwy
                uiItem.projectNameText.text = project.projectName;

                // G³ówny przycisk - Wybór projektu
                uiItem.selectButton.onClick.AddListener(() => OnProjectSelected(project));

                // Przycisk boczny - Usuwanie
                uiItem.deleteButton.onClick.AddListener(() => Button_DeleteProject(project));
            }
        }
    }

    private void OnProjectSelected(SVNProject project)
    {
        Debug.Log($"Loading project: {project.projectName}");

        // Wywo³ujemy ³adowanie w SVNManager
        SVNManager.Instance.LoadProject(project);

        // Prze³¹czamy widoki
        if (mainUIPanel != null) mainUIPanel.SetActive(true);
        this.gameObject.SetActive(false);
    }

    // Otwiera okienko dodawania (Podepnij pod przycisk "Plus")
    public void Button_OpenAddProjectPanel()
    {
        if (addProjectSubPanel != null)
        {
            addProjectSubPanel.SetActive(true);
            // Czyœcimy pola przy otwarciu
            nameInput.text = ""; urlInput.text = ""; pathInput.text = ""; keyInput.text = "";
        }
    }

    // Zamyka okienko dodawania (Podepnij pod przycisk "Cancel")
    public void Button_CloseAddProjectPanel()
    {
        if (addProjectSubPanel != null) addProjectSubPanel.SetActive(false);
    }

    // Logika przycisku "Save"
    public void Button_SaveNewProject()
    {
        if (string.IsNullOrWhiteSpace(nameInput.text) || string.IsNullOrWhiteSpace(pathInput.text))
        {
            Debug.LogError("Nazwa i œcie¿ka s¹ wymagane!");
            return;
        }

        AddNewProject(nameInput.text, urlInput.text, pathInput.text, keyInput.text);
    }

    // Fizyczne dodanie do listy i zapis do JSON
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

        // Pobieramy aktualn¹ listê, dodajemy projekt i zapisujemy
        List<SVNProject> currentList = ProjectSettings.LoadProjects();
        currentList.Add(newProj);
        ProjectSettings.SaveProjects(currentList);

        // Odœwie¿amy widok przycisków
        RefreshList();

        // Zamykamy formularz
        Button_CloseAddProjectPanel();

        // OPCJONALNIE: Od razu za³aduj ten projekt
        OnProjectSelected(newProj);
    }

    // Ta metoda zostanie wywo³ana po klikniêciu w czerwony "X"
    public void Button_DeleteProject(SVNProject project)
    {
        // Opcjonalnie: mo¿esz dodaæ tu okienko "Czy na pewno?"
        Debug.Log($"Removing: {project.projectName} Project");

        // 1. Usuñ z pliku JSON
        ProjectSettings.DeleteProject(project.workingDir);

        // 2. Jeœli usuniêty projekt by³ tym ostatnio otwartym, wyczyœæ pamiêæ
        if (PlayerPrefs.GetString("SVN_LastOpenedProjectPath") == project.workingDir)
        {
            PlayerPrefs.DeleteKey("SVN_LastOpenedProjectPath");
        }

        // 3. Odœwie¿ listê w UI
        RefreshList();
    }
}