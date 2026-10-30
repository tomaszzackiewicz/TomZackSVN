using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

public class SVNConflictItem : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_Text fileNameText;
    [SerializeField] private TMP_Text conflictTypeText;

    [Header("Buttons - Text / Manual / Binary")]
    [SerializeField] private Button mineButton;
    [SerializeField] private Button theirsButton;
    [SerializeField] private Button resolvedButton;
    [SerializeField] private Button openButton;

    [Header("Resolved Button Label (optional)")]
    [Tooltip("TMP_Text shown on the resolved button. The label changes per conflict type " +
             "(Manual: 'Mark resolved', Binary: 'Keep current file'). Leave empty to use the prefab's default text.")]
    [SerializeField] private TMP_Text resolvedButtonLabel;

    [Header("Buttons - Tree")]
    [SerializeField] private Button treeMineButton;
    [SerializeField] private Button treeTheirsButton;
    [SerializeField] private Button treeBaseButton;
    [SerializeField] private Button treeDeleteButton;

    private string _path;

    public enum ConflictType { Text, Manual, Tree, Binary }

    public void Setup(string path, ConflictType type, bool hasMarkers, string treeReason = null)
    {
        _path = path;

        if (conflictTypeText != null)
        {
            string typeText = type switch
            {
                ConflictType.Text => "Text conflict",
                ConflictType.Manual => "Manual conflict",
                ConflictType.Tree => string.IsNullOrEmpty(treeReason)
                                        ? "Tree conflict"
                                        : $"Tree: {treeReason}",
                ConflictType.Binary => "Binary conflict (no text merge)",
                _ => "Unknown"
            };

            if (typeText.Length > 60)
                typeText = typeText.Substring(0, 57) + "...";

            conflictTypeText.text = typeText;
        }

        if (fileNameText != null)
            fileNameText.text = path;

        ClearAndHide(mineButton);
        ClearAndHide(theirsButton);
        ClearAndHide(resolvedButton);
        ClearAndHide(openButton);
        ClearAndHide(treeMineButton);
        ClearAndHide(treeTheirsButton);
        ClearAndHide(treeBaseButton);
        ClearAndHide(treeDeleteButton);

        if (type == ConflictType.Text)
        {
            Show(mineButton);
            Show(theirsButton);
            Show(openButton);

            SetTooltip(mineButton,
                "Keep YOUR local version (mine-full).\n" +
                "Discards all server changes for this file.");

            SetTooltip(theirsButton,
                "Take the SERVER version (theirs-full).\n" +
                "Discards all your local changes for this file.");

            SetTooltip(openButton,
                "Open the conflicted file.\n" +
                "Uses 3-way merge tool if configured,\n" +
                "otherwise opens in a text editor with conflict markers.");

            mineButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleMine(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            theirsButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleTheirs(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            openButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().OpenSingle(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });
        }
        else if (type == ConflictType.Manual)
        {
            Show(openButton);
            Show(resolvedButton);

            SetResolvedButtonLabel("Mark resolved");

            SetTooltip(openButton,
                "Open the conflicted file.\n" +
                "Uses 3-way merge tool if configured,\n" +
                "otherwise opens in a text editor with conflict markers.");

            SetTooltip(resolvedButton,
                "Keep the CURRENT contents of the working file.\n" +
                "Use this AFTER manually resolving markers in your editor.\n" +
                "Disabled while conflict markers are still present.");

            openButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().OpenSingle(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            resolvedButton.interactable = !hasMarkers;
            resolvedButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().MarkSingleResolved(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });
        }
        else if (type == ConflictType.Binary)
        {
            Show(mineButton);
            Show(theirsButton);
            Show(resolvedButton);

            SetResolvedButtonLabel("Keep current file");

            SetTooltip(mineButton,
                "Keep YOUR local binary (mine-full).\n" +
                "Restores the .mine copy over the working file.\n" +
                "Use this to undo a manual replacement.");

            SetTooltip(theirsButton,
                "Take the SERVER binary (theirs-full).\n" +
                "Overwrites the working file with the repository version.\n" +
                "Discards your local binary for this file.");

            SetTooltip(resolvedButton,
                "Keep the CURRENT file on disk unchanged.\n" +
                "Use this if you ALREADY copied the correct binary\n" +
                "over the working file (from a backup, branch, or build).\n" +
                "SVN will mark the conflict resolved without touching the file.");

            mineButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleMine(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            theirsButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleTheirs(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            resolvedButton.interactable = true;
            resolvedButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().MarkSingleResolved(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });
        }
        else if (type == ConflictType.Tree)
        {
            // Tree conflicts resolve via the unified force-core path, which runs
            // Phase 1 (standard resolve) then Phase 3 (structural fix if needed).
            // No "FORCE" variants are exposed — the same button always works.
            Show(treeMineButton);
            Show(treeTheirsButton);
            Show(treeBaseButton);
            Show(treeDeleteButton);

            SetTooltip(treeMineButton,
                "Keep YOUR local version.\n" +
                "Tries the standard SVN resolve first; if SVN rejects it,\n" +
                "falls back to a structural fix (revert + resolve working).\n" +
                "Does NOT touch the repository.");

            SetTooltip(treeTheirsButton,
                "Take the SERVER version.\n" +
                "Backs up local files, removes them, then updates from repository.\n" +
                "⚠ DESTRUCTIVE — discards local changes for this item!");

            SetTooltip(treeBaseButton,
                "Reset to BASE (common ancestor).\n" +
                "Reverts local changes and resolves.\n" +
                "Safe — does not delete anything from the repository.");

            SetTooltip(treeDeleteButton,
                "Remove unversioned obstruction files.\n" +
                "Backs up first, then schedules for deletion.\n" +
                "Use when local files block SVN operations.");

            treeMineButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeMine(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            treeTheirsButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeTheirs(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            treeBaseButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeBase(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            treeDeleteButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().DeleteObstruction(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });
        }
    }

    private void SetResolvedButtonLabel(string label)
    {
        if (resolvedButtonLabel != null)
            resolvedButtonLabel.text = label;
    }

    private static void SetTooltip(Button btn, string tooltip)
    {
        if (btn == null) return;

        var handler = btn.GetComponent<SVNHoverHandler>();
        if (handler == null)
            handler = btn.gameObject.AddComponent<SVNHoverHandler>();

        handler.TooltipText = tooltip;
    }

    private void ClearAndHide(Button button)
    {
        if (button == null) return;
        button.onClick.RemoveAllListeners();
        button.gameObject.SetActive(false);
    }

    private void Show(Button button)
    {
        if (button == null) return;
        button.gameObject.SetActive(true);
    }
}