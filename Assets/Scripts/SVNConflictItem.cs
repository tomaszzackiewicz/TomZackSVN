using SVN.Core;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SVNConflictItem : MonoBehaviour
{
    [Header("UI")]
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

    // === FIX 14: null-safe bindowanie dla WSZYSTKICH przycisków (wcześniej
    // tylko force miały null-check — niepodpięty slot w Inspectorze = NRE
    // przy kliknięciu).
    private static void Bind(Button button, Func<System.Threading.Tasks.Task> action)
    {
        if (button == null) return;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(async () =>
        {
            try { await action(); }
            catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
        });
    }

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

            Bind(mineButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleMine(_path));
            Bind(theirsButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveSingleTheirs(_path));
            Bind(openButton, () => SVNManager.Instance.GetModule<SVNResolve>().OpenSingle(_path));
        }
        else if (type == ConflictType.Manual)
        {
            Show(openButton);
            Show(resolvedButton);

            Bind(openButton, () => SVNManager.Instance.GetModule<SVNResolve>().OpenSingle(_path));

            if (resolvedButton != null)
            {
                resolvedButton.interactable = !hasMarkers;
                Bind(resolvedButton, () => SVNManager.Instance.GetModule<SVNResolve>().MarkSingleResolved(_path));
            }
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

            Bind(treeMineButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeMine(_path));
            Bind(treeTheirsButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeTheirs(_path));
            Bind(treeBaseButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeBase(_path));
            Bind(treeDeleteButton, () => SVNManager.Instance.GetModule<SVNResolve>().DeleteObstruction(_path));
            Bind(treeMineForceButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeMineForce(_path));
            Bind(treeTheirsForceButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeTheirsForce(_path));
            Bind(treeBaseForceButton, () => SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeBaseForce(_path));
        }
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