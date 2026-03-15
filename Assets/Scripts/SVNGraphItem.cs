using UnityEngine;
using TMPro;
using SVN.Core;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Text.RegularExpressions; // Wymagane dla Regex

public class SVNGraphItem : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI graphVisualText;
    public TextMeshProUGUI revisionText;
    public TextMeshProUGUI authorText;
    public TextMeshProUGUI messageText;
    public TextMeshProUGUI branchNameText;
    public TextMeshProUGUI dateText;

    [Header("Scrollable File List")]
    public GameObject filesContainer;
    public Transform scrollContent;
    public TextMeshProUGUI summaryText;
    public GameObject fileButtonPrefab;

    private List<string> changedPaths = new List<string>();
    private long revisionNumber;
    private bool isExpanded = false;
    private SVNManager svnManager;

    // --- Surowe dane do podświetlania ---
    private string rawAuthor;
    private string rawBranchName;
    private string rawMessage;
    private string rawRevisionStr;
    private string currentFilter; // Zapamiętujemy filtr dla listy plików
    private string branchHexColor;

    public string GetBranchName() => rawBranchName;
    public string GetMessage() => rawMessage;
    public string GetAuthor() => rawAuthor;
    public long GetRevision() => revisionNumber;
    public List<string> GetChangedPaths() => changedPaths;
    public string GetDate() => dateText != null ? dateText.text : "Unknown Date";

    public void Setup(string visual, SVNRevisionNode node, string branchName, string hexColor, SVNManager mgr)
    {
        this.svnManager = mgr;
        this.revisionNumber = node.Revision;
        this.branchHexColor = hexColor;
        this.changedPaths = node.ChangedPaths;

        // Zapisujemy surowe dane
        this.rawAuthor = node.Author;
        this.rawBranchName = branchName;
        this.rawRevisionStr = $"r{node.Revision}";

        string cleanMsg = node.Message;
        int idx = cleanMsg.LastIndexOf(" /");
        if (idx != -1) cleanMsg = cleanMsg.Substring(0, idx).Trim();
        this.rawMessage = cleanMsg;

        // Stałe wizualia
        graphVisualText.text = visual;

        if (dateText != null)
        {
            if (System.DateTime.TryParse(node.Date, out System.DateTime dt))
                dateText.text = dt.ToString("yyyy-MM-dd HH:mm");
            else
                dateText.text = node.Date;
        }

        filesContainer.SetActive(false);

        // Inicjalne ustawienie tekstu bez filtra
        ApplyHighlight(null);
    }

    // --- KLUCZOWA METODA PODŚWIETLAJĄCA ---
    public void ApplyHighlight(string filter)
    {
        this.currentFilter = filter;

        // 1. Podświetlamy numer rewizji (zachowując kolor czarny)
        revisionText.text = $"<color=black><b>{GetMarkedText(rawRevisionStr, filter)}</b></color>";

        // 2. Autor
        authorText.text = GetMarkedText(rawAuthor, filter);

        // 3. Branch (zachowując jego oryginalny kolor)
        if (branchNameText != null)
        {
            string highlightedBranch = GetMarkedText(rawBranchName, filter);
            branchNameText.text = $"<color={branchHexColor}>[{highlightedBranch}]</color>";
        }

        // 4. Wiadomość
        messageText.text = GetMarkedText(rawMessage, filter);

        // 5. Jeśli lista plików jest otwarta, musimy ją przerysować z nowym podświetleniem
        if (isExpanded)
        {
            BuildFileButtons();
        }
    }

    private string GetMarkedText(string text, string filter)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(text)) return text;

        // Mark z lekką przezroczystością (AA)
        string pattern = Regex.Escape(filter);
        return Regex.Replace(text, pattern, "<mark=#FFFF00AA>$0</mark>", RegexOptions.IgnoreCase);
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;
        if (isExpanded) BuildFileButtons();
        else ClearFiles();

        filesContainer.SetActive(isExpanded);
        RefreshLayout();
    }

    private void BuildFileButtons()
    {
        ClearFiles();

        if (changedPaths == null || changedPaths.Count == 0)
        {
            summaryText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            return;
        }

        int added = 0, modified = 0, deleted = 0;

        foreach (var path in changedPaths)
        {
            string statusChar = path.Substring(0, 1);
            string filePath = path.Substring(1).Trim();

            string color = "#FFFFFF";
            if (statusChar == "A") { color = "#55FF55"; added++; }
            else if (statusChar == "M") { color = "#FFFF55"; modified++; }
            else if (statusChar == "D") { color = "#FF5555"; deleted++; }

            GameObject go = Instantiate(fileButtonPrefab, scrollContent);
            SVNFileItem script = go.GetComponent<SVNFileItem>();
            if (script != null)
            {
                // PRZEKAZUJEMY PODŚWIETLONĄ NAZWĘ PLIKU
                string highlightedPath = GetMarkedText(filePath, currentFilter);
                script.Setup($"[{statusChar}]", highlightedPath, color, revisionNumber, svnManager);
            }
        }

        summaryText.text = $"<size=85%><b>Summary: </b><color=#55FF55>{added}A</color> <color=#FFFF55>{modified}M</color> <color=#FF5555>{deleted}D</color></size>";
    }

    private void ClearFiles()
    {
        foreach (Transform child in scrollContent) Destroy(child.gameObject);
    }

    private void RefreshLayout()
    {
        if (transform.parent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(transform.parent.GetComponent<RectTransform>());
    }

    public void SetExpanded(bool state)
    {
        if (state == false && isExpanded)
        {
            isExpanded = false;
            ClearFiles();
            if (filesContainer != null)
                filesContainer.SetActive(false);

            RefreshLayout();
        }
    }
}