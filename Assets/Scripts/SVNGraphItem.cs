using SVN.Core;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class SVNGraphItem : MonoBehaviour, IPointerClickHandler
{
    [Header("UI References")]
    public TextMeshProUGUI graphVisualText;
    public TextMeshProUGUI revisionText;
    public TextMeshProUGUI authorText;
    public TextMeshProUGUI messageText;
    public TextMeshProUGUI branchNameText;
    public TextMeshProUGUI dateText;
    public TextMeshProUGUI contextInfoText;
    public TextMeshProUGUI filesSummaryText;

    [Header("Scrollable File List")]
    public GameObject filesContainer;
    public Transform scrollContent;
    public TextMeshProUGUI summaryText;
    public GameObject fileButtonPrefab;
    public TMP_InputField fileFilterInput;

    [Header("Edit Message")]
    public Button editMessageButton;

    [Header("Selection")]
    public Image backgroundImage;
    public Color normalColor = new Color(0.12f, 0.12f, 0.14f, 1f);
    public Color selectedColor = new Color(0.22f, 0.32f, 0.48f, 1f);
    public Color hoverColor = new Color(0.18f, 0.18f, 0.22f, 1f);

    private List<string> changedPaths = new List<string>();
    private long revisionNumber;
    private bool isExpanded = false;
    private SVNManager svnManager;

    private string rawAuthor;
    private string rawBranchName;
    private string rawMessage;
    private string rawRevisionStr;
    private string rawDate;
    private string currentFilter;
    private string branchHexColor;
    private string rawContextLabel;
    private bool isBranchPoint;
    private NodeType nodeType;
    private bool isSelected = false;
    private DateTime commitDate;

    public string GetBranchName() => rawBranchName;
    public string GetMessage() => rawMessage;
    public string GetAuthor() => rawAuthor;
    public long GetRevision() => revisionNumber;
    public List<string> GetChangedPaths() => changedPaths;
    public string GetDate() => rawDate;
    public bool IsSelected => isSelected;

    private void Start()
    {
        if (editMessageButton != null)
            editMessageButton.onClick.AddListener(OnEditMessageClicked);

        if (fileFilterInput != null)
            fileFilterInput.onValueChanged.AddListener(_ => { if (isExpanded) BuildFileButtons(); });
    }

    public void Setup(string graphUnused, SVNRevisionNode node, string branchName, string hexColor, SVNManager mgr,
                  string contextLabel = "", NodeType nodeType = NodeType.Unknown, bool isBranchPoint = false,
                  GraphData.NodeInfo details = default)
    {
        this.svnManager = mgr;
        this.revisionNumber = node.Revision;
        this.branchHexColor = hexColor;
        this.changedPaths = node.ChangedPaths ?? new List<string>();
        this.rawContextLabel = contextLabel ?? "";
        this.isBranchPoint = isBranchPoint;
        this.nodeType = nodeType;

        this.rawAuthor = string.IsNullOrEmpty(node.Author) ? "Unknown" : node.Author;
        this.rawBranchName = branchName;
        this.rawRevisionStr = $"r{node.Revision}";

        if (!string.IsNullOrEmpty(node.Date) &&
            DateTime.TryParse(node.Date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out commitDate))
        {
            rawDate = FormatRelativeTime(commitDate) + "  •  " + commitDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        else
        {
            rawDate = node.Date ?? "";
        }

        if (editMessageButton != null)
        {
            string currentUser = mgr != null ? mgr.CurrentUserName : null;
            bool isOwnCommit = !string.IsNullOrEmpty(currentUser) &&
                               currentUser != "Unknown" &&
                               string.Equals(currentUser, node.Author, StringComparison.OrdinalIgnoreCase);
            editMessageButton.interactable = isOwnCommit;
        }

        string cleanMsg = node.Message ?? "";
        int idx = cleanMsg.LastIndexOf(" /");
        if (idx != -1) cleanMsg = cleanMsg.Substring(0, idx).Trim();
        this.rawMessage = cleanMsg;

        if (graphVisualText != null)
        {
            graphVisualText.gameObject.SetActive(true);

            string icon;
            if (!string.IsNullOrEmpty(details.MergeSource))
                icon = "<color=#FF88FF>◉</color>";
            else if (isBranchPoint)
                icon = "<color=#55FF55>▣</color>";
            else if (nodeType == NodeType.Trunk)
                icon = "<color=#3B82F6>■</color>";
            else if (nodeType == NodeType.Tag)
                icon = $"<color={hexColor}>◆</color>";
            else if (details.HasMergeInfoChange)
                icon = "<color=#FFAA00>⚡</color>";
            else
                icon = $"<color={hexColor}>●</color>";

            graphVisualText.text = icon;
        }

        if (branchNameText != null)
        {
            string contextShort = BuildContextShort(rawContextLabel);
            branchNameText.text = $"<color={branchHexColor}>[{rawBranchName}]</color>{contextShort}";
        }

        if (filesSummaryText != null)
        {
            if (details.ChangedFilesCount > 0)
            {
                filesSummaryText.text =
                    $"<color=#64D2FF>{details.ChangedFilesCount}</color>  " +
                    $"<color=#55FF55>{details.AddedCount}A</color> " +
                    $"<color=#FFFF55>{details.ModifiedCount}M</color> " +
                    $"<color=#FF9900>{details.DeletedCount}D</color>";
            }
            else
            {
                filesSummaryText.text = "";
            }
        }

        if (filesContainer != null)
            filesContainer.SetActive(false);

        SetSelected(false);
        ApplyHighlight(null);
    }

    private string FormatRelativeTime(DateTime dt)
    {
        var span = DateTime.Now - dt.ToLocalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hours ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)} weeks ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} months ago";
        return dt.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        if (backgroundImage != null)
            backgroundImage.color = selected ? selectedColor : normalColor;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.pointerCurrentRaycast.gameObject != gameObject &&
            !eventData.pointerCurrentRaycast.gameObject.transform.IsChildOf(transform))
            return;

        if (eventData.button == PointerEventData.InputButton.Left)
        {
            if (eventData.clickCount >= 2)
            {
                OnItemClicked();
                eventData.Use();
            }
            else
            {
                bool multi = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                var panel = GetComponentInParent<RevGraphPanel>();
                if (panel != null)
                    panel.OnItemClicked(this, multi);
                else
                    SetSelected(!isSelected);

                eventData.Use();
            }
        }
        else if (eventData.button == PointerEventData.InputButton.Right)
        {
            ShowContextMenu();
            eventData.Use();
        }
    }

    private void ShowContextMenu()
    {
        Debug.Log($"[ContextMenu] r{revisionNumber} | {rawAuthor} | {rawBranchName}");
    }

    private void OnEditMessageClicked()
    {
        EditMessagePopup.Show(revisionNumber, rawMessage, svnManager, (newMessage) =>
        {
            rawMessage = newMessage;
            ApplyHighlight(currentFilter);
        });
    }

    public void ApplyHighlight(string filter)
    {
        this.currentFilter = filter;

        if (revisionText != null)
            revisionText.text = $"<color=white><b>{GetMarkedText(rawRevisionStr, filter)}</b></color>";

        if (authorText != null)
            authorText.text = $"<color=#FFFFFF>{GetMarkedText(rawAuthor, filter)}</color>";

        if (branchNameText != null)
        {
            string contextShort = BuildContextShort(rawContextLabel, filter);
            branchNameText.text = $"<color={branchHexColor}>[{GetMarkedText(rawBranchName, filter)}]</color>{contextShort}";
        }

        if (messageText != null)
            messageText.text = $"<color=#FFFFFF>{GetMarkedText(rawMessage, filter)}</color>";

        if (dateText != null)
            dateText.text = $"<color=#CCCCCC>{GetMarkedText(rawDate, filter)}</color>";
    }

    private string BuildContextShort(string contextLabel, string filter = null)
    {
        if (string.IsNullOrEmpty(contextLabel))
            return "";

        string plain = Regex.Replace(contextLabel, "<.*?>", "").Trim();

        if (string.IsNullOrEmpty(plain))
            return "";

        if (plain.Length > 42)
            plain = plain.Substring(0, 39) + "...";

        if (!string.IsNullOrEmpty(filter) && plain.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            plain = GetMarkedText(plain, filter);

        return $" <size=80%><color=#DDDDDD>{plain}</color></size>";
    }

    private string GetMarkedText(string text, string filter)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(text)) return text;
        string pattern = Regex.Escape(filter);
        return Regex.Replace(text, pattern, "<mark=#FFFF00AA>$0</mark>", RegexOptions.IgnoreCase);
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;
        if (isExpanded) BuildFileButtons();
        else ClearFiles();

        if (filesContainer != null)
            filesContainer.SetActive(isExpanded);

        RefreshLayout();
    }

    private void BuildFileButtons()
    {
        ClearFiles();

        if (changedPaths == null || changedPaths.Count == 0)
        {
            if (summaryText != null)
                summaryText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            return;
        }

        string fileFilter = fileFilterInput != null ? fileFilterInput.text.Trim() : "";
        int added = 0, modified = 0, deleted = 0, shown = 0;

        var groups = new Dictionary<char, List<string>>
        {
            ['A'] = new List<string>(),
            ['M'] = new List<string>(),
            ['D'] = new List<string>(),
            ['?'] = new List<string>()
        };

        foreach (var path in changedPaths)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2) continue;

            char status = char.ToUpper(path[0]);
            string filePath = path.Substring(1).Trim();

            if (!string.IsNullOrEmpty(fileFilter) &&
                filePath.IndexOf(fileFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (status == 'A') { groups['A'].Add(path); added++; }
            else if (status == 'M') { groups['M'].Add(path); modified++; }
            else if (status == 'D') { groups['D'].Add(path); deleted++; }
            else groups['?'].Add(path);
        }

        void AddGroup(char status, string title, string color, List<string> list)
        {
            if (list.Count == 0) return;

            if (fileButtonPrefab != null)
            {
                var headerGo = Instantiate(fileButtonPrefab, scrollContent);
                var headerScript = headerGo.GetComponent<SVNFileItem>();
                if (headerScript != null)
                    headerScript.Setup($"[{status}]", $"<b>{title} ({list.Count})</b>", color, revisionNumber, svnManager);
            }

            foreach (var path in list)
            {
                string filePath = path.Substring(1).Trim();
                string highlighted = GetMarkedText(filePath, currentFilter);

                if (fileButtonPrefab != null)
                {
                    var go = Instantiate(fileButtonPrefab, scrollContent);
                    var script = go.GetComponent<SVNFileItem>();
                    if (script != null)
                        script.Setup($"[{status}]", highlighted, color, revisionNumber, svnManager);
                }
                shown++;
            }
        }

        AddGroup('A', "Added", "#55FF55", groups['A']);
        AddGroup('M', "Modified", "#FFFF55", groups['M']);
        AddGroup('D', "Deleted", "#FF9900", groups['D']);
        AddGroup('?', "Other", "#AAAAAA", groups['?']);

        if (summaryText != null)
        {
            summaryText.text = $"<size=85%><b>Summary:</b> " +
                               $"<color=#55FF55>{added}A</color> " +
                               $"<color=#FFFF55>{modified}M</color> " +
                               $"<color=#FF9900>{deleted}D</color>  • shown {shown}</size>";
        }
    }

    private void ClearFiles()
    {
        if (scrollContent == null) return;
        foreach (Transform child in scrollContent)
            Destroy(child.gameObject);
    }

    private void RefreshLayout()
    {
        if (transform.parent != null)
        {
            var rt = transform.parent.GetComponent<RectTransform>();
            if (rt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }
    }

    public void SetExpanded(bool state)
    {
        if (!state && isExpanded)
        {
            isExpanded = false;
            ClearFiles();
            if (filesContainer != null) filesContainer.SetActive(false);
            RefreshLayout();
        }
    }
}