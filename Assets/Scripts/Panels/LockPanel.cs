using UnityEngine;
using SVN.Core;

public class LockPanel : MonoBehaviour
{
    [Header("UI References")]
    public GameObject lockEntryPrefab;
    public Transform locksContainer;
    //public GameObject panelRoot; // Obiekt całego panelu do włączania/wyłączania

    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Awake()
    {
        // Jeśli masz Singletony, to pobieramy je tutaj
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;


    }

    void OnEnable()
    {

    }

    public void OpenAndRefresh()
    {
        svnManager.SVNLock.OpenStealPanel();
    }

    //public void Close() => svnManager.SVNLock.StealLockSelected();
}