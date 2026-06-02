using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

public class SVNConflictItem : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Text fileNameText;
    [SerializeField] private TMP_Text conflictTypeText;

    [Header("Buttons")]
    [SerializeField] private Button mineButton;
    [SerializeField] private Button theirsButton;
    [SerializeField] private Button resolvedButton;
    [SerializeField] private Button openButton;
    [SerializeField] private Button deleteButton;

    private string _path;

    // =====================================================
    // TYPES
    // =====================================================

    public enum ConflictType
    {
        Text,
        Manual,
        Tree
    }

    // =====================================================
    // SETUP
    // =====================================================

    public void Setup(
        string path,
        ConflictType type,
        bool hasMarkers)
    {
        _path = path;
        //_conflictCache = type;

        if (conflictTypeText != null)
        {
            conflictTypeText.text = type switch
            {
                ConflictType.Text => "Text conflict",
                ConflictType.Manual => "Manual conflict",
                ConflictType.Tree => "Tree conflict",
                _ => "Unknown"
            };
        }

        if (fileNameText != null)
            fileNameText.text = path;

        // =====================================================
        // CLEAR OLD EVENTS
        // =====================================================

        ClearButton(mineButton);
        ClearButton(theirsButton);
        ClearButton(resolvedButton);
        ClearButton(openButton);
        ClearButton(deleteButton);

        // =====================================================
        // HIDE EVERYTHING FIRST
        // =====================================================

        SetButton(mineButton, false);
        SetButton(theirsButton, false);
        SetButton(resolvedButton, false);
        SetButton(openButton, false);
        SetButton(deleteButton, false);

        // =====================================================
        // TEXT CONFLICT
        // [Mine] [Theirs] [Open]
        // =====================================================

        if (type == ConflictType.Text)
        {
            SetButton(mineButton, true);
            SetButton(theirsButton, true);
            SetButton(openButton, true);

            mineButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                    .GetModule<SVNResolve>()
                    .ResolveSingleMine(_path);
            });

            theirsButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                    .GetModule<SVNResolve>()
                    .ResolveSingleTheirs(_path);
            });

            openButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                     .GetModule<SVNResolve>()
                     .OpenSingle(_path);
            });
        }

        // =====================================================
        // MANUAL CONFLICT
        // [Open] [Resolved]
        // =====================================================

        else if (type == ConflictType.Manual)
        {
            SetButton(openButton, true);
            SetButton(resolvedButton, true);

            openButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                    .GetModule<SVNResolve>()
                    .OpenSingle(_path);
            });

            resolvedButton.interactable = !hasMarkers;

            resolvedButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                    .GetModule<SVNResolve>()
                    .MarkSingleResolved(_path);
            });
        }

        // =====================================================
        // TREE CONFLICT
        // [Delete Obstruction]
        // =====================================================

        else if (type == ConflictType.Tree)
        {
            SetButton(deleteButton, true);

            deleteButton.onClick.AddListener(async () =>
            {
                await SVNManager.Instance
                    .GetModule<SVNResolve>()
                    .DeleteObstruction(_path);
            });
        }
    }

    // =====================================================
    // HELPERS
    // =====================================================

    private void ClearButton(Button button)
    {
        if (button == null)
            return;

        button.onClick.RemoveAllListeners();
    }

    private void SetButton(Button button, bool state)
    {
        if (button == null)
            return;

        button.gameObject.SetActive(state);
    }
}