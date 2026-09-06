using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

public class SVNConflictItem : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_Text fileNameText;
    [SerializeField] private TMP_Text conflictTypeText;

    [Header("Buttons - Text / Manual")]
    [SerializeField] private Button mineButton;
    [SerializeField] private Button theirsButton;
    [SerializeField] private Button resolvedButton;
    [SerializeField] private Button openButton;

    [Header("Buttons - Tree")]
    [SerializeField] private Button treeMineButton;
    [SerializeField] private Button treeTheirsButton;
    [SerializeField] private Button treeBaseButton;
    [SerializeField] private Button treeDeleteButton;

    [Header("Buttons - Tree Force")]
    [SerializeField] private Button treeMineForceButton;
    [SerializeField] private Button treeTheirsForceButton;
    [SerializeField] private Button treeBaseForceButton;

    private string _path;

    public enum ConflictType { Text, Manual, Tree }

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
        ClearAndHide(treeMineForceButton);
        ClearAndHide(treeTheirsForceButton);
        ClearAndHide(treeBaseForceButton);

        if (type == ConflictType.Text)
        {
            Show(mineButton);
            Show(theirsButton);
            Show(openButton);

            // === FIX (UX): tooltipy wyjaśniające strategie
            SetTooltip(mineButton, "Keep YOUR local version (mine-full).\nDiscards all server changes for this file.");
            SetTooltip(theirsButton, "Take the SERVER version (theirs-full).\nDiscards all your local changes for this file.");
            SetTooltip(openButton, "Open in external 3-way merge tool.\nManually combine both versions.");

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

            // === FIX (UX): tooltipy
            SetTooltip(openButton, "Open in external 3-way merge tool.\nManually resolve conflicts.");
            SetTooltip(resolvedButton, "Mark as manually resolved.\nFile must NOT contain conflict markers.");

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
        else if (type == ConflictType.Tree)
        {
            Show(treeMineButton);
            Show(treeTheirsButton);
            Show(treeBaseButton);
            Show(treeDeleteButton);
            Show(treeMineForceButton);
            Show(treeTheirsForceButton);
            Show(treeBaseForceButton);

            // === FIX (UX): tooltipy dokumentujące asymetrię Mine vs Theirs
            SetTooltip(treeMineButton,
                "Keep YOUR local directory/file.\nIf missing on disk, reverts to base.\nDoes NOT delete anything from repository.");

            SetTooltip(treeTheirsButton,
                "Take the SERVER version.\nBackups local files, then removes them\nand updates from repository.");

            SetTooltip(treeBaseButton,
                "Reset to BASE (common ancestor).\nReverts local changes and resolves.");

            SetTooltip(treeDeleteButton,
                "Remove unversioned obstruction files.\nBackups first, then schedules for deletion.\nUse when local files block SVN operations.");

            SetTooltip(treeMineForceButton,
                "FORCE: Keep local.\nTries standard resolve first.\nIf fails: reverts + resolves working.\nUse when normal 'Mine' doesn't work.");

            SetTooltip(treeTheirsForceButton,
                "FORCE: Take server version.\nTries standard resolve first.\nIf fails: backups → deletes → cleans → resolves → updates.\n⚠ DESTRUCTIVE — removes local version!");

            SetTooltip(treeBaseForceButton,
                "FORCE: Reset to base.\nTries standard resolve first.\nIf fails: reverts + resolves working.");

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

            treeMineForceButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeMineForce(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            treeTheirsForceButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeTheirsForce(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });

            treeBaseForceButton.onClick.AddListener(async () =>
            {
                try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeBaseForce(_path); }
                catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
            });
        }
    }

    private static void SetTooltip(Button btn, string tooltip)
    {
        if (btn == null) return;
        var handler = btn.GetComponent<SVNHoverHandler>();
        if (handler != null)
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