using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNUI : MonoBehaviour
    {
        public static SVNUI Instance { get; private set; }
        public SVNManager SvnManager;

        [Header("Tooltip")]
        [SerializeField] private TextMeshProUGUI tooltipText;
        [Header("Logs")]
        [SerializeField] private TextMeshProUGUI logText;
        [SerializeField] private TMP_InputField logCountInputField;
        [SerializeField] private TextMeshProUGUI checkoutConsoleText;
        [SerializeField] private TextMeshProUGUI outputText;
        [Header("Add New Project Settings (Popup)")]
        [SerializeField] private TMP_InputField addProjectNameInput;
        [SerializeField] private TMP_InputField addProjectRepoUrlInput;
        [SerializeField] private TMP_InputField addProjectFolderPathInput;
        [SerializeField] private TMP_InputField addProjectKeyPathInput;
        [Header("Add Repo Settings")]
        [SerializeField] private TMP_InputField loadRepoUrlInput = null;
        [SerializeField] private TMP_InputField loadDestFolderInput;
        [SerializeField] private TMP_InputField loadPrivateKeyInput = null;
        [Header("Checkout Settings")]
        [SerializeField] private TMP_InputField checkoutRepoUrlInput;
        [SerializeField] private TMP_InputField checkoutDestFolderInput;
        [SerializeField] private TMP_InputField checkoutPrivateKeyInput;
        [SerializeField] private TextMeshProUGUI checkoutStatusInfoText;
        [SerializeField] private TextMeshProUGUI checkoutedFilesText;
        [Header("Branching & Tagging")]
        [SerializeField] private TextMeshProUGUI mergeConsoleText;
        [SerializeField] private TMP_InputField revisionInput;
        [SerializeField] private TMP_InputField mergeSourceInput;
        [SerializeField] private TMP_InputField branchNameInput;
        [SerializeField] private TMP_InputField branchCommitMsgInput;
        [SerializeField] private TMP_Dropdown typeSelector;
        [SerializeField] private TMP_Dropdown branchesDropdown;
        [SerializeField] private TMP_Dropdown tagsDropdown;
        [SerializeField] private TMP_Dropdown mergeBranchesDropdown;
        [SerializeField] private TextMeshProUGUI branchTagConsoleText;
        [SerializeField] private Transform mergeFilesContainer;
        [SerializeField] private MergeFileItem mergeFileItemPrefab;
        [SerializeField] private TMP_InputField branchSourcePathInput;
        [SerializeField] private Toggle ignoreAncestryToggle;
        [SerializeField] private TMP_InputField mergeCherryPickRevisionInput;
        [Header("Status Info")]
        [SerializeField] private TextMeshProUGUI statusInfoText;
        [SerializeField] private TMP_InputField commitMessageInput;
        [SerializeField] private TMP_InputField filterTreeViewInput;
        [SerializeField] private SvnTreeView svnTreeView;
        [SerializeField] private SvnTreeView svnCommitTreeDisplay;
        [Header("Ignored")]
        [SerializeField] private TextMeshProUGUI ignoredText;
        [Header("Commit")]
        [SerializeField] private TextMeshProUGUI commitSizeText;
        [SerializeField] private TextMeshProUGUI commitTreeDisplay;
        [SerializeField] private TextMeshProUGUI commitStatsText;
        [SerializeField] private TextMeshProUGUI commitConsoleContent;
        [SerializeField] private UnityEngine.UI.Slider operationProgressBar;
        [SerializeField] private TextMeshProUGUI commitCurrentFileText;
        [SerializeField] private Toggle showUnversionedToggle;
        [Header("Loading Indicator")]
        [SerializeField] private TextMeshProUGUI treeDisplay;
        [SerializeField] private TextMeshProUGUI statsText;
        [SerializeField] private GameObject conflictGroup;
        [Header("Settings UI")]
        [SerializeField] private TMP_InputField settingsRepoUrlInput;
        [SerializeField] private TMP_InputField settingsWorkingDirInput;
        [SerializeField] private TMP_InputField settingsSshKeyPathInput;
        [SerializeField] private TMP_InputField settingsMergeToolPathInput;
        [SerializeField] private TMP_InputField settingsResolveToolPathInput;
        [SerializeField] private TMP_InputField settingsDiffToolPathInput;
        [SerializeField] private TMP_InputField settingsBlameToolPathInput;
        [Header("Terminal")]
        [SerializeField] private TMP_InputField terminalInputField;
        [SerializeField] private TextMeshProUGUI terminalConsoleOutput;
        [SerializeField] private ScrollRect logScrollRect;
        [Header("Shelves")]
        [SerializeField] private TMP_InputField shelfNameInput;
        [SerializeField] private ScrollRect shelfListContainer;
        [SerializeField] private GameObject shelfItemPrefab;
        [Header("Locks")]
        [SerializeField] private Transform locksContainer;
        [SerializeField] private TextMeshProUGUI stealLocksConsole;
        [SerializeField] private TMP_InputField lockCommentInput;
        [Header("Notifications")]
        [SerializeField] private GameObject notificationPanel;
        [SerializeField] private TextMeshProUGUI notificationText;
        [Header("Resolve")]
        [SerializeField] private TMP_InputField resolveTargetFileInput;
        [SerializeField] private TextMeshProUGUI resolveConsoleContent;
        [SerializeField] private TextMeshProUGUI resolveLogConsole;
        [SerializeField] private GameObject conflictPrefab = null;
        [Header("Clean")]
        [SerializeField] private TextMeshProUGUI cleanText;
        [Header("Diff Panel References")]
        [SerializeField] private TMP_InputField diffTargetFileInput;
        [SerializeField] private TextMeshProUGUI diffConsoleText;
        [Header("Blame Panel References")]
        [SerializeField] private TMP_InputField blameTargetFileInput;
        [SerializeField] private TextMeshProUGUI blameDisplayArea;
        [SerializeField] private TextMeshProUGUI blameConsoleText;
        [Header("Revision Graph")]
        [SerializeField] private Transform graphContainer;
        [SerializeField] public GameObject graphItemPrefab;
        [SerializeField] private List<SvnTreeView> svnTreeViews = new List<SvnTreeView>();
        [Header("Repo Browser (Server Files)")]
        [SerializeField] private Transform repoBrowserContentRoot;
        [SerializeField] private GameObject repoBrowserItemPrefab;
        [SerializeField] private TextMeshProUGUI repoBrowserCurrentPathText;
        [SerializeField] private TMP_InputField repoBrowserFilterInput;
        [Header("Lock References")]
        [SerializeField] private TextMeshProUGUI lockDisplayArea;
        [Header("Revision References")]
        [SerializeField] private TMP_InputField updateRevisionInput;
        [SerializeField] private TextMeshProUGUI revisionDisplayArea;
        [SerializeField] private TMP_InputField revisionFilePathInput;
        [Header("Snapshot")]   // === FIX: duplikat nagłówka "Revision References"
        [SerializeField] private Toggle snapshotUnversionedOnlyToggle;

        private Coroutine _notificationCoroutine;

        public TextMeshProUGUI TooltipText => tooltipText;
        public TextMeshProUGUI LogText => logText;
        public TMP_InputField LogCountInputField => logCountInputField;
        public TextMeshProUGUI CheckoutConsoleText => checkoutConsoleText;
        public TMP_InputField AddProjectNameInput => addProjectNameInput;
        public TMP_InputField AddProjectRepoUrlInput => addProjectRepoUrlInput;
        public TMP_InputField AddProjectFolderPathInput => addProjectFolderPathInput;
        public TMP_InputField AddProjectKeyPathInput => addProjectKeyPathInput;
        public TMP_InputField LoadRepoUrlInput => loadRepoUrlInput;
        public TMP_InputField LoadDestFolderInput => loadDestFolderInput;
        public TMP_InputField LoadPrivateKeyInput => loadPrivateKeyInput;
        public TMP_InputField CheckoutRepoUrlInput => checkoutRepoUrlInput;
        public TMP_InputField CheckoutDestFolderInput => checkoutDestFolderInput;
        public TMP_InputField CheckoutPrivateKeyInput => checkoutPrivateKeyInput;
        public TextMeshProUGUI CheckoutStatusInfoText => checkoutStatusInfoText;
        public TextMeshProUGUI CheckoutedFilesText => checkoutedFilesText;
        public TextMeshProUGUI MergeConsoleText => mergeConsoleText;
        public TMP_InputField RevisionInput => revisionInput;
        public TMP_InputField MergeSourceInput => mergeSourceInput;
        public TMP_InputField BranchNameInput => branchNameInput;
        public TMP_InputField BranchCommitMsgInput => branchCommitMsgInput;
        public TMP_Dropdown TypeSelector => typeSelector;
        public TMP_Dropdown BranchesDropdown => branchesDropdown;
        public TMP_Dropdown TagsDropdown => tagsDropdown;
        public TMP_Dropdown MergeBranchesDropdown => mergeBranchesDropdown;
        public TextMeshProUGUI BranchTagConsoleText => branchTagConsoleText;
        public Transform MergeFilesContainer => mergeFilesContainer;
        public MergeFileItem MergeFileItemPrefab => mergeFileItemPrefab;
        public TMP_InputField BranchSourcePathInput => branchSourcePathInput;
        public Toggle IgnoreAncestryToggle => ignoreAncestryToggle;
        public TMP_InputField MergeCherryPickRevisionInput => mergeCherryPickRevisionInput;
        public TextMeshProUGUI StatusInfoText => statusInfoText;
        public TMP_InputField CommitMessageInput => commitMessageInput;
        public TMP_InputField FilterTreeViewInput => filterTreeViewInput;
        public SvnTreeView SvnTreeView => svnTreeView;
        public SvnTreeView SVNCommitTreeDisplay => svnCommitTreeDisplay;
        public TextMeshProUGUI IgnoredText => ignoredText;
        public TextMeshProUGUI CommitSizeText => commitSizeText;
        public TextMeshProUGUI TreeDisplay
        {
            get => treeDisplay;
            set => treeDisplay = value;
        }

        public TextMeshProUGUI CommitTreeDisplay
        {
            get => commitTreeDisplay;
            set => commitTreeDisplay = value;
        }
        public TextMeshProUGUI StatsText => statsText;
        public TextMeshProUGUI CommitStatsText => commitStatsText;
        public TextMeshProUGUI CommitConsoleContent => commitConsoleContent;
        public TextMeshProUGUI OutputText => outputText;
        public UnityEngine.UI.Slider OperationProgressBar => operationProgressBar;
        public TextMeshProUGUI CommitCurrentFileText => commitCurrentFileText;
        public Toggle ShowUnversionedToggle => showUnversionedToggle;
        public GameObject ConflictGroup => conflictGroup;
        public TMP_InputField SettingsRepoUrlInput => settingsRepoUrlInput;
        public TMP_InputField SettingsWorkingDirInput => settingsWorkingDirInput;
        public TMP_InputField SettingsSshKeyPathInput => settingsSshKeyPathInput;
        public TMP_InputField SettingsResolveToolPathInput => settingsResolveToolPathInput;
        public TMP_InputField SettingsMergeToolPathInput => settingsMergeToolPathInput;
        public TMP_InputField SettingsDiffToolPathInput => settingsDiffToolPathInput;
        public TMP_InputField SettingsBlameToolPathInput => settingsBlameToolPathInput;
        public TMP_InputField TerminalInputField => terminalInputField;
        public TextMeshProUGUI TerminalConsoleOutput => terminalConsoleOutput;
        public ScrollRect LogScrollRect => logScrollRect;
        public TMP_InputField ShelfNameInput => shelfNameInput;
        public ScrollRect ShelfListContainer => shelfListContainer;
        public GameObject ShelfItemPrefab => shelfItemPrefab;
        public Transform LocksContainer => locksContainer;
        public TextMeshProUGUI StealLocksConsole => stealLocksConsole;
        public TMP_InputField LockCommentInput => lockCommentInput;
        public TMP_InputField ResolveTargetFileInput => resolveTargetFileInput;
        public TextMeshProUGUI ResolveConsoleContent => resolveConsoleContent;
        public TextMeshProUGUI ResolveLogConsole => resolveLogConsole;
        public GameObject ConflictPrefab => conflictPrefab;
        public TextMeshProUGUI CleanText => cleanText;
        public TMP_InputField DiffTargetFileInput => diffTargetFileInput;
        public TextMeshProUGUI DiffConsoleText => diffConsoleText;
        public TMP_InputField BlameTargetFileInput => blameTargetFileInput;
        public TextMeshProUGUI BlameDisplayArea => blameDisplayArea;
        public TextMeshProUGUI BlameConsoleText => blameConsoleText;
        public Transform GraphContainer => graphContainer;
        public GameObject GraphItemPrefab => graphItemPrefab;
        public List<SvnTreeView> SVNTreeViews => svnTreeViews;
        public Transform RepoBrowserContentRoot => repoBrowserContentRoot;
        public GameObject RepoBrowserItemPrefab => repoBrowserItemPrefab;
        public TextMeshProUGUI RepoBrowserCurrentPathText => repoBrowserCurrentPathText;
        public TMP_InputField RepoBrowserFilterInput => repoBrowserFilterInput;
        public TextMeshProUGUI LockDisplayArea => lockDisplayArea;
        public TMP_InputField UpdateRevisionInput => updateRevisionInput;
        public TextMeshProUGUI RevisionDisplayArea => revisionDisplayArea;
        public TMP_InputField RevisionFilePathInput => revisionFilePathInput;
        public Toggle SnapshotUnversionedOnlyToggle => snapshotUnversionedOnlyToggle;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject); return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (ShowUnversionedToggle != null)
            {
                var statusModule = SvnManager?.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    ShowUnversionedToggle.SetIsOnWithoutNotify(statusModule.ShowUnversionedFiles);
                }

                ShowUnversionedToggle.onValueChanged.AddListener(OnToggleUnversioned);
            }
        }

        // === FIX K1-minor: nie odpalaj odświeżania, gdy projekt jeszcze niezaładowany
        // (pierwszy klik toggle'a przed load = "Błąd podczas odświeżania"-klasy logów).
        private void OnToggleUnversioned(bool show)
        {
            var statusModule = SvnManager?.GetModule<SVNStatus>();
            if (statusModule != null)
            {
                statusModule.ShowUnversionedFiles = show;

                if (!string.IsNullOrEmpty(SvnManager?.WorkingDir))
                    statusModule.ShowOnlyModified();
            }

            SVNLogBridge.LogLine(show
                ? "<color=yellow>Unversioned files visible.</color>"
                : "<color=yellow>Unversioned files hidden (folders only).</color>");
        }

        private void OnDestroy()
        {
            if (ShowUnversionedToggle != null)
                ShowUnversionedToggle.onValueChanged.RemoveListener(OnToggleUnversioned);

            // === FIX: czyszczenie singletonu (po zniszczeniu jedynego SVNUI
            // Instance wisiło jako "fake null"; Unity == ratuje, ale czyściej).
            if (Instance == this)
                Instance = null;
        }

        public void ShowNotificationWithTimer(string message, float delay = 5f)
        {
            if (notificationPanel == null || notificationText == null) return;   // === FIX: null-check Text

            notificationText.text = message;
            notificationPanel.SetActive(true);

            if (_notificationCoroutine != null) StopCoroutine(_notificationCoroutine);

            _notificationCoroutine = StartCoroutine(HideNotificationAfterDelay(delay));
        }

        private IEnumerator HideNotificationAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (notificationPanel != null)   // === FIX: obiekt mógł zostać zniszczony
                notificationPanel.SetActive(false);
            _notificationCoroutine = null;
        }

        public void OnTreeViewFilterChanged(string value)
        {
            var treeView = SvnTreeView;
            if (treeView != null)
            {
                treeView.FilterTree(value);
            }
        }
    }
}