using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

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

        // Ukryj i wyczyść wszystkie przyciski
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

            // Force – pokazujemy tylko jeśli przypisano w inspektorze
            Show(treeMineForceButton);
            Show(treeTheirsForceButton);
            Show(treeBaseForceButton);

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

            // Listenery Force
            if (treeMineForceButton != null)
            {
                treeMineForceButton.onClick.AddListener(async () =>
                {
                    try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeMineForce(_path); }
                    catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
                });
            }

            if (treeTheirsForceButton != null)
            {
                treeTheirsForceButton.onClick.AddListener(async () =>
                {
                    try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeTheirsForce(_path); }
                    catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
                });
            }

            if (treeBaseForceButton != null)
            {
                treeBaseForceButton.onClick.AddListener(async () =>
                {
                    try { await SVNManager.Instance.GetModule<SVNResolve>().ResolveTreeBaseForce(_path); }
                    catch (System.Exception ex) { SVNLogBridge.LogException(ex); }
                });
            }
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