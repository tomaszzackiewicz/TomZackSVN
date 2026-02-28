using UnityEngine;
using TMPro;
using SVN.Core;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UI;

public class SVNGraphItem : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI graphVisualText;
    public TextMeshProUGUI revisionText;
    public TextMeshProUGUI authorText;
    public TextMeshProUGUI messageText;
    public TextMeshProUGUI branchNameText;
    public TextMeshProUGUI dateText;

    [Header("Scrollable File List (Prefabs)")]
    public GameObject filesContainer;  // Obiekt posiadający komponent ScrollRect
    public Transform scrollContent;    // Obiekt 'Content' wewnątrz ScrollRect (z VerticalLayoutGroup)
    public GameObject fileItemPrefab;  // Mały prefab z komponentem TextMeshProUGUI
    public TextMeshProUGUI summaryText; // Nagłówek nad listą plików (np. "Summary: 2A, 3M")

    private List<string> changedPaths = new List<string>();

    private string currentAuthor;
    private string currentBranchName;
    private string currentMessage;
    private long revisionNumber;
    private bool isExpanded = false;

    // Metody wymagane przez RevGraphPanel.cs do filtrowania
    public string GetBranchName() => currentBranchName;
    public string GetMessage() => currentMessage;
    public string GetAuthor() => currentAuthor;
    public long GetRevision() => revisionNumber;

    public void Setup(string visual, SVNRevisionNode node, string branchName, string hexColor)
    {
        // Przypisanie danych do pól (kluczowe dla filtrowania i logowania)
        this.revisionNumber = node.Revision;
        this.currentBranchName = branchName;
        this.currentMessage = node.Message;
        this.changedPaths = node.ChangedPaths;

        // Wizualizacja grafu i rewizji
        graphVisualText.text = visual;
        revisionText.text = $"<color=black><b>r{node.Revision}</b></color>";

        if (branchNameText != null)
        {
            branchNameText.text = $"<color={hexColor}>[{branchName}]</color>";
        }

        authorText.text = node.Author;
        currentAuthor = node.Author; ;

        // Czyszczenie wiadomości commita
        string cleanMsg = node.Message;
        int idx = cleanMsg.LastIndexOf(" /");
        if (idx != -1) cleanMsg = cleanMsg.Substring(0, idx).Trim();
        messageText.text = cleanMsg;

        // Formatowanie daty
        if (dateText != null)
        {
            if (System.DateTime.TryParse(node.Date, out System.DateTime dt))
                dateText.text = dt.ToString("yyyy-MM-dd HH:mm");
            else
                dateText.text = node.Date;
        }

        // Przygotowanie listy plików i ukrycie jej na starcie
        PrepareFileList();
        filesContainer.SetActive(false);
    }

    private void PrepareFileList()
    {
        // Czyszczenie starych elementów w ScrollView (jeśli istnieją)
        foreach (Transform child in scrollContent)
        {
            Destroy(child.gameObject);
        }

        if (changedPaths == null || changedPaths.Count == 0)
        {
            summaryText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            return;
        }

        int added = 0, modified = 0, deleted = 0, replaced = 0, conflicted = 0;

        // Generowanie prefabów dla każdego pliku
        foreach (var path in changedPaths)
        {
            // Neonowe kolory dla szarego tła
            string color = "#FFFFFF";
            string statusTag = "";

            if (path.StartsWith("A")) { color = "#55FF55"; statusTag = "[A]"; added++; }
            else if (path.StartsWith("M")) { color = "#FFFF55"; statusTag = "[M]"; modified++; }
            else if (path.StartsWith("D")) { color = "#FF5555"; statusTag = "[D]"; deleted++; }
            else if (path.StartsWith("R")) { color = "#CC88FF"; statusTag = "[R]"; replaced++; }
            else if (path.StartsWith("C")) { color = "#FFB347"; statusTag = "[C]"; conflicted++; }
            else { statusTag = "[?]"; }

            // Tworzenie nowego elementu listy
            GameObject item = Instantiate(fileItemPrefab, scrollContent);
            TextMeshProUGUI txt = item.GetComponent<TextMeshProUGUI>();

            if (txt != null)
            {
                // Wyświetlamy status i ścieżkę (bez pierwszej litery statusu z danych surowych)
                txt.text = $"<size=90%><color={color}><b>{statusTag}</b> {path.Substring(1).Trim()}</color></size>";
            }
        }

        // Aktualizacja nagłówka Summary
        StringBuilder sb = new StringBuilder();
        sb.Append("<size=85%><color=#EEEEEE><b>Summary: </b></color>");
        if (added > 0) sb.Append($"<color=#55FF55>{added}A</color> ");
        if (modified > 0) sb.Append($"<color=#FFFF55>{modified}M</color> ");
        if (deleted > 0) sb.Append($"<color=#FF5555>{deleted}D</color> ");
        if (replaced > 0) sb.Append($"<color=#CC88FF>{replaced}R</color> ");
        if (conflicted > 0) sb.Append($"<color=#FFB347>{conflicted}C</color>");

        summaryText.text = sb.ToString();
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;
        filesContainer.SetActive(isExpanded);

        // Wymuszenie odświeżenia layoutu rodzica (Content w głównym grafie), aby wiersz się rozsunął
        if (transform.parent != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(transform.parent.GetComponent<RectTransform>());
        }
    }

    public void LogChangedFiles()
    {
        if (changedPaths == null || changedPaths.Count == 0)
        {
            Debug.Log("No changed paths data found.");
            return;
        }

        string report = $"Revision r{revisionNumber} changed {changedPaths.Count} files:\n";
        foreach (var path in changedPaths)
        {
            report += $"- {path}\n";
        }
        Debug.Log(report);
    }

    public void OnClick()
    {
        Debug.Log($"Selected revision r{revisionNumber}");
    }

    public void SetExpanded(bool state)
    {
        isExpanded = state;
        if (filesContainer != null)
        {
            filesContainer.SetActive(state);
        }
    }
}