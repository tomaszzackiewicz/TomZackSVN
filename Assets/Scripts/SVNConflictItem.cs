using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

public class SVNConflictItem : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Text fileNameText;

    [SerializeField] private Button mineButton;
    [SerializeField] private Button theirsButton;
    [SerializeField] private Button resolvedButton;
    [SerializeField] private Button openButton;
    [SerializeField] private Button deleteButton;

    private string _path;

    public void Setup(string path)
    {
        _path = path;

        fileNameText.text = path;

        mineButton.onClick.RemoveAllListeners();
        theirsButton.onClick.RemoveAllListeners();
        resolvedButton.onClick.RemoveAllListeners();
        openButton.onClick.RemoveAllListeners();
        deleteButton.onClick.RemoveAllListeners();

        mineButton.onClick.AddListener(() =>
        {
            SVNManager.Instance
                .GetModule<SVNResolve>()
                .ResolveSingleMine(_path);
        });

        theirsButton.onClick.AddListener(() =>
        {
            SVNManager.Instance
                .GetModule<SVNResolve>()
                .ResolveSingleTheirs(_path);
        });

        resolvedButton.onClick.AddListener(() =>
        {
            SVNManager.Instance
                .GetModule<SVNResolve>()
                .MarkSingleResolved(_path);
        });

        openButton.onClick.AddListener(() =>
        {
            SVNManager.Instance
                .GetModule<SVNResolve>()
                .OpenSingle(_path);
        });

        deleteButton.onClick.AddListener(() =>
        {
            SVNManager.Instance
                .GetModule<SVNResolve>()
                .DeleteObstruction(_path);
        });
    }
}