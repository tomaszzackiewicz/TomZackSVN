using UnityEngine;
using SVN.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using TMPro;
using UnityEngine.UI;

public class LockPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject lockEntryPrefab;
    [SerializeField] private Transform locksContainer;

    private bool isProcessing = false;
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Awake()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    private void OnEnable()
    {
        if (!Application.isPlaying) return;

        RefreshAndShow();
    }

    private void OnDisable()
    {
        ClearContainer();
    }

    public void Button_RefreshLocks() => RefreshAndShow();

    public async void RefreshAndShow()
    {
        if (isProcessing || !Application.isPlaying) return;

        isProcessing = true;

        LogToPanel("<color=orange>[System]</color> Preparing to fetch lock data...");

        ClearContainer();

        try
        {
            LogToPanel("<color=#FFD700>[Process]</color> Querying SVN server (this may take a moment)...");

            var allLocks = await svnManager.GetModule<SVNLock>().GetDetailedLocks(svnManager.WorkingDir);

            LogToPanel($"<color=white>[Info]</color> Received {allLocks.Count} total locks from server.");

            string currentUserName = (svnManager.CurrentUserName ?? "NULL").Trim().ToLower();

            var othersLocks = allLocks.Where(l =>
            {
                if (string.IsNullOrEmpty(l.Owner)) return false;
                return l.Owner.Trim().ToLower() != currentUserName;
            }).ToList();

            if (othersLocks.Count == 0)
            {
                LogToPanel("<color=yellow>[Info]</color> No locks from other users found.");
            }
            else
            {
                LogToPanel($"<color=yellow>[UI]</color> Spawning {othersLocks.Count} entries...");
                Populate(othersLocks);
                LogToPanel("<color=green>[Success]</color> List updated.");
            }
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=red>[Error]</color> Sync failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void Populate(List<SVNLockDetails> locks)
    {
        if (!Application.isPlaying) return;

        foreach (var lockItem in locks)
        {
            GameObject entry = Instantiate(lockEntryPrefab, locksContainer);
            LockUIItem uiItem = entry.GetComponent<LockUIItem>();

            if (uiItem != null)
            {
                uiItem.Setup(
                    lockItem.Path,
                    lockItem.Owner,
                    lockItem.CreationDate,
                    lockItem.Comment,
                    false,
                    () => ExecuteSteal(lockItem)
                );
            }
        }
    }

    private async void ExecuteSteal(SVNLockDetails lockDetails)
    {
        if (isProcessing || lockDetails == null || !Application.isPlaying) return;

        isProcessing = true;
        LogToPanel($"<color=green>[Action]</color> Forcing break on: <b>{lockDetails.Path}</b>");

        try
        {
            string cmd = $"lock --force -m \"Administrative takeover by {svnManager.CurrentUserName}\" \"{lockDetails.FullPath}\"";
            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Stole lock: {lockDetails.Path}");

            await System.Threading.Tasks.Task.Delay(600);
            await svnManager.RefreshStatus();

            isProcessing = false;
            RefreshAndShow();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=red>[Error]</color> Operation failed: {ex.Message}");
            isProcessing = false;
        }
    }

    private void LogToPanel(string message)
    {
        if (svnUI.StealLocksConsole == null || !Application.isPlaying) return;

        SVNLogBridge.UpdateUIField(svnUI.StealLocksConsole, message, "STEAL_LOCKS", append: true);

        ScrollRect scroll = svnUI.StealLocksConsole.GetComponentInParent<ScrollRect>();
        if (scroll != null)
        {
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }
    }

    private void ClearContainer()
    {
        if (locksContainer == null) return;

        foreach (Transform child in locksContainer)
        {
            Destroy(child.gameObject);
        }
    }
}