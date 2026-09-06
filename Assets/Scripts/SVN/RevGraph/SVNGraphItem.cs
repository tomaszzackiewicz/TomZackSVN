using SVN.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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

    private Coroutine _fileBuildCoroutine;
    private HorizontalLayoutGroup[] _layoutGroups;

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

    private static readonly Regex HtmlStripRegex = new Regex("<.*?>", RegexOptions.Compiled);
    private static readonly Regex ColorExtractRegex = new Regex("<color=([^>]+)>", RegexOptions.Compiled);
    private static readonly StringBuilder Sb = new StringBuilder(256);

    private readonly Dictionary<char, List<string>> fileGroups = new Dictionary<char, List<string>>(4)
    {
        ['A'] = new List<string>(16),
        ['M'] = new List<string>(16),
        ['D'] = new List<string>(16),
        ['?'] = new List<string>(8)
    };

    private readonly List<SVNFileItem> itemPool = new List<SVNFileItem>(32);

    public string GetBranchName() => rawBranchName;
    public string GetMessage() => rawMessage;
    public string GetAuthor() => rawAuthor;
    public long GetRevision() => revisionNumber;
    public List<string> GetChangedPaths() => changedPaths;
    public string GetDate() => rawDate;
    public bool IsSelected => isSelected;

    private void Awake()
    {
        _layoutGroups = GetComponentsInChildren<HorizontalLayoutGroup>(true);

        if (editMessageButton != null)
            editMessageButton.onClick.AddListener(OnEditMessageClicked);

        if (fileFilterInput != null)
            fileFilterInput.onValueChanged.AddListener(_ => { if (isExpanded) RebuildFilesAsync(); });
    }

    public void Setup(string graphUnused, SVNRevisionNode node, string branchName, string hexColor, SVNManager mgr,
                      string contextLabel = "", NodeType nodeType = NodeType.Unknown, bool isBranchPoint = false,
                      GraphData.NodeInfo details = default)
    {
        if (_layoutGroups != null)
        {
            for (int i = 0; i < _layoutGroups.Length; i++)
                _layoutGroups[i].enabled = false;
        }

        this.svnManager = mgr;
        this.revisionNumber = node.Revision;
        this.branchHexColor = hexColor;
        this.changedPaths = node.ChangedPaths ?? new List<string>();
        this.rawContextLabel = contextLabel ?? "";
        this.isBranchPoint = isBranchPoint;
        this.nodeType = nodeType;
        this.rawAuthor = string.IsNullOrEmpty(node.Author) ? "Unknown" : node.Author;
        this.rawBranchName = branchName;
        this.rawRevisionStr = "r" + node.Revision.ToString();

        if (!string.IsNullOrEmpty(node.Date) &&
            DateTime.TryParse(node.Date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out commitDate))
        {
            Sb.Clear();
            FormatRelativeTime(commitDate, Sb);
            Sb.Append(" • ").Append(commitDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            rawDate = Sb.ToString();
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

        // === FIX K2: usunięta heurystyka LastIndexOf(" /") — obcinała LEGALNE
        // wiadomości ("Refactor ModuleA / ModuleB" → "Refactor ModuleA").
        // Commit-message to dane użytkownika; bez pewnego wzorca sufiksu
        // nie manipulujemy treścią.
        this.rawMessage = node.Message ?? "";

        if (graphVisualText != null)
        {
            graphVisualText.gameObject.SetActive(true);
            if (!string.IsNullOrEmpty(details.MergeSource))
                graphVisualText.text = "<color=#FF88FF>◉</color>";
            else if (isBranchPoint)
                graphVisualText.text = "<color=#55FF55>▣</color>";
            else if (nodeType == NodeType.Trunk)
                graphVisualText.text = "<color=#3B82F6>■</color>";
            else if (nodeType == NodeType.Tag)
                graphVisualText.text = "<color=" + hexColor + ">◆</color>";
            else if (details.HasMergeInfoChange)
                graphVisualText.text = "<color=#FFAA00>⚡</color>";
            else
                graphVisualText.text = "<color=" + hexColor + ">●</color>";
        }

        if (branchNameText != null)
        {
            branchNameText.text = "<color=" + branchHexColor + ">[" + rawBranchName + "]</color>";
        }

        if (contextInfoText != null)
        {
            contextInfoText.text = string.IsNullOrEmpty(rawContextLabel)
                ? string.Empty
                : BuildSmartContextLabel(rawContextLabel);
        }

        if (filesSummaryText != null)
        {
            filesSummaryText.text = string.Empty;
        }

        if (revisionText != null) revisionText.text = "<color=white><b>" + rawRevisionStr + "</b></color>";
        if (authorText != null) authorText.text = "<color=#FFFFFF>" + rawAuthor + "</color>";
        if (messageText != null) messageText.text = "<color=#FFFFFF>" + rawMessage + "</color>";
        if (dateText != null) dateText.text = "<color=#CCCCCC>" + rawDate + "</color>";

        if (filesContainer != null) filesContainer.SetActive(false);

        if (_layoutGroups != null)
        {
            for (int i = 0; i < _layoutGroups.Length; i++)
                _layoutGroups[i].enabled = true;
        }

        SetSelected(false);
    }

    private string BuildSmartContextLabel(string contextLabel, string filter = null)
    {
        if (string.IsNullOrEmpty(contextLabel)) return string.Empty;

        if (!string.IsNullOrEmpty(filter))
        {
            string plain = HtmlStripRegex.Replace(contextLabel, "").Trim();
            if (plain.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string highlighted = GetMarkedText(plain, filter);
                return "<size=85%><i>" + highlighted + "</i></size>";
            }
        }

        string plainText = HtmlStripRegex.Replace(contextLabel, "").Trim();

        if (plainText.Length <= 50)
            return contextLabel;

        int revIndex = plainText.LastIndexOf(" r", StringComparison.Ordinal);

        if (revIndex > 10)
        {
            string prefix = plainText.Substring(0, revIndex).Trim();
            string revPart = plainText.Substring(revIndex);

            if (prefix.Length > 25)
            {
                prefix = prefix.Substring(0, 22) + "...";
            }

            string truncatedPlain = prefix + " " + revPart;

            string color = "#BBBBBB";
            var colorMatch = ColorExtractRegex.Match(contextLabel);
            if (colorMatch.Success) color = colorMatch.Groups[1].Value;

            return "<color=" + color + "><size=85%><i>" + truncatedPlain + "</i></size></color>";
        }

        return "<size=85%><i>" + plainText.Substring(0, 47) + "...</i></size>";
    }

    private void FormatRelativeTime(DateTime dt, StringBuilder builder)
    {
        var span = DateTime.Now - dt.ToLocalTime();
        double minutes = span.TotalMinutes;

        if (minutes < 1) builder.Append("just now");
        else if (minutes < 60) builder.Append((int)minutes).Append(" min ago");
        else if (span.TotalHours < 24) builder.Append((int)span.TotalHours).Append(" hours ago");
        else if (span.TotalDays < 7) builder.Append((int)span.TotalDays).Append(" days ago");
        else if (span.TotalDays < 30) builder.Append((int)(span.TotalDays / 7)).Append(" weeks ago");
        else if (span.TotalDays < 365) builder.Append((int)(span.TotalDays / 30)).Append(" months ago");
        else builder.Append(dt.ToLocalTime().ToString("yyyy-MM-dd"));
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        if (backgroundImage != null)
            backgroundImage.color = selected ? selectedColor : normalColor;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // === FIX K1: null-guard raycastu — klik "w nic" (raycast pada poza
        // kolidery) daje pointerCurrentRaycast.gameObject == null → wcześniej
        // NRE na porównaniu z gameObject.
        var hitGo = eventData.pointerCurrentRaycast.gameObject;
        if (hitGo == null) return;
        if (hitGo != gameObject && !hitGo.transform.IsChildOf(transform))
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
                // === FIX K1: legacy Input w try/catch — na projekcie z nowym
                // Input System Input.GetKey rzuca InvalidOperationException
                // przy KAŻDYM kliku (wzorzec z SVNTerminal/MainWindow).
                bool multi = false;
                try
                {
                    multi = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                }
                catch (InvalidOperationException) { /* Input System bez legacy */ }

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
        SVNLogBridge.LogLine($"[ContextMenu] r{revisionNumber} | {rawAuthor} | {rawBranchName}");
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
            revisionText.text = "<color=white><b>" + GetMarkedText(rawRevisionStr, filter) + "</b></color>";

        if (authorText != null)
            authorText.text = "<color=#FFFFFF>" + GetMarkedText(rawAuthor, filter) + "</color>";

        if (branchNameText != null)
            branchNameText.text = "<color=" + branchHexColor + ">[" + GetMarkedText(rawBranchName, filter) + "]</color>";

        if (messageText != null)
            messageText.text = "<color=#FFFFFF>" + GetMarkedText(rawMessage, filter) + "</color>";

        if (dateText != null)
            dateText.text = "<color=#CCCCCC>" + GetMarkedText(rawDate, filter) + "</color>";

        if (contextInfoText != null && !string.IsNullOrEmpty(rawContextLabel))
        {
            contextInfoText.text = BuildSmartContextLabel(rawContextLabel, filter);
        }
    }

    private string GetMarkedText(string text, string filter)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(text))
            return text;

        int idx = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;

        Sb.Clear();
        Sb.Append(text, 0, idx)
          .Append("<mark=#FFFF00AA>")
          .Append(text, idx, filter.Length)
          .Append("</mark>")
          .Append(text, idx + filter.Length, text.Length - (idx + filter.Length));

        return Sb.ToString();
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;

        if (_fileBuildCoroutine != null)
        {
            StopCoroutine(_fileBuildCoroutine);
            _fileBuildCoroutine = null;
        }

        if (isExpanded)
        {
            if (filesContainer != null) filesContainer.SetActive(true);
            _fileBuildCoroutine = StartCoroutine(BuildFileButtonsRoutine());
        }
        else
        {
            ClearFiles();
            if (filesContainer != null) filesContainer.SetActive(false);
        }

        RefreshLayout();
    }

    private void RebuildFilesAsync()
    {
        if (_fileBuildCoroutine != null)
        {
            StopCoroutine(_fileBuildCoroutine);
            _fileBuildCoroutine = null;
        }

        if (isExpanded)
        {
            _fileBuildCoroutine = StartCoroutine(BuildFileButtonsRoutine());
        }
    }

    private IEnumerator BuildFileButtonsRoutine()
    {
        ClearFiles();

        if (changedPaths == null || changedPaths.Count == 0)
        {
            if (summaryText != null)
                summaryText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            yield break;
        }

        string fileFilter = fileFilterInput != null ? fileFilterInput.text.Trim() : string.Empty;
        int added = 0, modified = 0, deleted = 0, shown = 0;

        foreach (var group in fileGroups.Values) group.Clear();

        for (int i = 0; i < changedPaths.Count; i++)
        {
            string path = changedPaths[i];
            if (string.IsNullOrEmpty(path) || path.Length < 2) continue;

            char status = char.ToUpper(path[0]);
            string filePath = path.Substring(1).Trim();

            if (!string.IsNullOrEmpty(fileFilter) &&
                filePath.IndexOf(fileFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (status == 'A') { fileGroups['A'].Add(path); added++; }
            else if (status == 'M') { fileGroups['M'].Add(path); modified++; }
            else if (status == 'D') { fileGroups['D'].Add(path); deleted++; }
            else fileGroups['?'].Add(path);
        }

        int activeItemIndex = 0;
        int processedThisFrame = 0;
        const int FilesPerFrame = 40;

        IEnumerator AddGroup(char status, string title, string color, List<string> list)
        {
            if (list.Count == 0) yield break;

            Sb.Clear();
            Sb.Append("<b>").Append(title).Append(" (").Append(list.Count).Append(")</b>");

            SVNFileItem headerScript = GetPooledItem(activeItemIndex++);
            if (headerScript != null)
                headerScript.Setup("[" + status + "]", Sb.ToString(), color, revisionNumber, svnManager);

            for (int i = 0; i < list.Count; i++)
            {
                string fPath = list[i].Substring(1).Trim();
                string highlighted = GetMarkedText(fPath, currentFilter);

                SVNFileItem script = GetPooledItem(activeItemIndex++);
                if (script != null)
                    script.Setup("[" + status + "]", highlighted, color, revisionNumber, svnManager);

                shown++;
                processedThisFrame++;

                if (processedThisFrame >= FilesPerFrame)
                {
                    processedThisFrame = 0;
                    RefreshLayout();
                    yield return null;
                }
            }
        }

        yield return AddGroup('A', "Added", "#55FF55", fileGroups['A']);
        yield return AddGroup('M', "Modified", "#FFFF55", fileGroups['M']);
        yield return AddGroup('D', "Deleted", "#FF9900", fileGroups['D']);
        yield return AddGroup('?', "Other", "yellow", fileGroups['?']);

        if (summaryText != null)
        {
            Sb.Clear();
            Sb.Append("<size=85%><b>Summary:</b> ")
              .Append("<color=#55FF55>").Append(added).Append("A</color> ")
              .Append("<color=#FFFF55>").Append(modified).Append("M</color> ")
              .Append("<color=#FF9900>").Append(deleted).Append("D</color> • shown ")
              .Append(shown).Append("</size>");
            summaryText.text = Sb.ToString();
        }

        RefreshLayout();
        _fileBuildCoroutine = null;
    }

    private SVNFileItem GetPooledItem(int index)
    {
        if (fileButtonPrefab == null || scrollContent == null) return null;

        if (index < itemPool.Count)
        {
            itemPool[index].gameObject.SetActive(true);
            return itemPool[index];
        }

        GameObject go = Instantiate(fileButtonPrefab, scrollContent);
        SVNFileItem script = go.GetComponent<SVNFileItem>();
        itemPool.Add(script);
        return script;
    }

    private void ClearFiles()
    {
        for (int i = 0; i < itemPool.Count; i++)
        {
            if (itemPool[i] != null)
                itemPool[i].gameObject.SetActive(false);
        }
    }

    private void RefreshLayout()
    {
        if (transform.parent != null && transform.parent is RectTransform rt)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }
    }

    public void SetExpanded(bool state)
    {
        if (!state && isExpanded)
        {
            isExpanded = false;
            if (_fileBuildCoroutine != null)
            {
                StopCoroutine(_fileBuildCoroutine);
                _fileBuildCoroutine = null;
            }
            ClearFiles();
            if (filesContainer != null) filesContainer.SetActive(false);
            RefreshLayout();
        }
    }

    public void SetChangedPaths(List<string> paths)
    {
        this.changedPaths = paths ?? new List<string>();
    }
}