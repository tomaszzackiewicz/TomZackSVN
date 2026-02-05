using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNUI : MonoBehaviour
    {
        public static SVNUI Instance { get; private set; }

        [Header("Logs")]
        [SerializeField] private TextMeshProUGUI logText;
        [SerializeField] private TMP_InputField logCountInputField;
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
        [Header("Branching & Tagging")]
        [SerializeField] private TMP_InputField mergeSourceInput;
        [SerializeField] private TMP_InputField branchNameInput;
        [SerializeField] private TMP_InputField branchCommitMsgInput;
        [SerializeField] private TMP_Dropdown typeSelector;
        [SerializeField] private TMP_Dropdown branchesDropdown;
        [SerializeField] private TMP_Dropdown tagsDropdown;
        [SerializeField] private TMP_Dropdown mergeBranchesDropdown;
        [Header("Status Info")]
        [SerializeField] private TextMeshProUGUI branchInfoText;
        [SerializeField] private TextMeshProUGUI locksText;
        [SerializeField] private TMP_InputField commitMessageInput;
        [Header("Ignored")]
        [SerializeField] private TextMeshProUGUI ignoredText;
        [Header("Commit")]
        [SerializeField] private TextMeshProUGUI commitSizeText;
        [SerializeField] private TextMeshProUGUI commitTreeDisplay;
        [SerializeField] private TextMeshProUGUI commitStatsText;
        [SerializeField] private TextMeshProUGUI commitConsoleContent;
        [SerializeField] private Slider operationProgressBar;
        [Header("Loading Indicator")]
        [SerializeField] private GameObject loadingOverlay;
        [SerializeField] private TextMeshProUGUI treeDisplay;
        [SerializeField] private TextMeshProUGUI statsText;
        [SerializeField] private GameObject conflictGroup;
        [Header("Settings UI")]
        [SerializeField] private TMP_InputField settingsRepoUrlInput;
        [SerializeField] private TMP_InputField settingsWorkingDirInput;
        [SerializeField] private TMP_InputField settingsSshKeyPathInput;
        [SerializeField] private TMP_InputField settingsMergeToolPathInput;
        [Header("Terminal")]
        [SerializeField] private TMP_InputField terminalInputField;
        [SerializeField] private ScrollRect logScrollRect;


        public TextMeshProUGUI LogText => logText;
        public TMP_InputField LogCountInputField => logCountInputField;
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
        public TMP_InputField MergeSourceInput => mergeSourceInput;
        public TMP_InputField BranchNameInput => branchNameInput;
        public TMP_InputField BranchCommitMsgInput => branchCommitMsgInput;
        public TMP_Dropdown TypeSelector => typeSelector;
        public TMP_Dropdown BranchesDropdown => branchesDropdown;
        public TMP_Dropdown TagsDropdown => tagsDropdown;
        public TMP_Dropdown MergeBranchesDropdown => mergeBranchesDropdown;
        public TextMeshProUGUI BranchInfoText => branchInfoText;
        public TextMeshProUGUI LocksText => locksText;
        public TMP_InputField CommitMessageInput => commitMessageInput;
        public TextMeshProUGUI IgnoredText => ignoredText;
        public TextMeshProUGUI CommitSizeText => commitSizeText;
        public GameObject LoadingOverlay => loadingOverlay;
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
        public Slider OperationProgressBar => operationProgressBar;
        public GameObject ConflictGroup => conflictGroup;
        public TMP_InputField SettingsRepoUrlInput => settingsRepoUrlInput;
        public TMP_InputField SettingsWorkingDirInput => settingsWorkingDirInput;
        public TMP_InputField SettingsSshKeyPathInput => settingsSshKeyPathInput;
        public TMP_InputField SettingsMergeToolPathInput => settingsMergeToolPathInput;
        public TMP_InputField TerminalInputField => terminalInputField;
        public ScrollRect LogScrollRect => logScrollRect;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject); return;
            }
            Instance = this;
        }

        

    }
}