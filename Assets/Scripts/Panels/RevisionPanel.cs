using System;
using System.IO;
using SFB;
using SVN.Core;
using UnityEngine;

public class RevisionPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private SVNRevision GetModule()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        var module = svnManager?.GetModule<SVNRevision>();
        if (module == null)
            SVNLogBridge.LogError("[RevisionPanel] SVNRevision module is not available.");
        return module;
    }

    public void Button_BrowsePath()
    {
        var module = GetModule();
        if (module != null)
            module.BrowsePath();
    }

    public void Button_UpdateToRevision() { var m = GetModule(); if (m != null) m.UpdateToRevisionButton(); }
    public void Button_ExportRevision() { var m = GetModule(); if (m != null) m.ExportRevisionButton(); }
    public void Button_RevertRevision() { var m = GetModule(); if (m != null) m.RevertCommitsButton(); }
    public void Button_RestoreSingleFileFromRevision() => RestoreSingleFileFromRevisionAsync();
    public void Button_ExtractSingleFileFromRevision() => ExtractSingleFileFromRevisionAsync();
    public void Button_ExtractFolderFromRevision() => ExtractFolderFromRevisionAsync();
    public void Button_RevertForPath() => RevertPathFromInputAsync();

    private async void RevertPathFromInputAsync()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        if (svnManager == null) svnManager = SVNManager.Instance;

        if (svnUI == null)
        {
            SVNLogBridge.LogError("[RevisionPanel] SVNUI.Instance is not ready yet.");
            return;
        }
        if (svnUI.RevisionFilePathInput == null)
        {
            SVNLogBridge.LogError("[RevisionPanel] RevisionFilePathInput is not assigned in SVNUI.");
            return;
        }

        string path = svnUI.RevisionFilePathInput.text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Revert Path] Please enter a file or folder path.</color>");
            return;
        }

        var module = GetModule();
        if (module == null) return;

        await module.RevertPathAsync(path);
    }

    private async void RestoreSingleFileFromRevisionAsync()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        if (svnManager == null) svnManager = SVNManager.Instance;

        if (svnUI == null)
        {
            SVNLogBridge.LogError("[RevisionPanel] SVNUI.Instance is not ready yet.");
            return;
        }

        string rev = svnUI.UpdateRevisionInput?.text?.Trim()?.TrimStart('r', 'R');
        string filePath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Restore File] Please enter a revision number.</color>");
            return;
        }
        if (!long.TryParse(rev, out _))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Restore File] Invalid revision format (numbers only, e.g. 150).</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(filePath))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Restore File] Please enter a file path.</color>");
            return;
        }

        var module = GetModule();
        if (module == null) return;

        await module.RestoreSingleFileAsync(filePath, rev);
    }

    private async void ExtractSingleFileFromRevisionAsync()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        if (svnManager == null) svnManager = SVNManager.Instance;

        if (svnUI == null)
        {
            SVNLogBridge.LogError("[RevisionPanel] SVNUI.Instance is not ready yet.");
            return;
        }

        string rev = svnUI.UpdateRevisionInput?.text?.Trim()?.TrimStart('r', 'R');
        string filePath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev) || string.IsNullOrWhiteSpace(filePath))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract File] Revision and file path are required.</color>");
            return;
        }

        if (!long.TryParse(rev, out _))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract File] Invalid revision format (numbers only, e.g. 150).</color>");
            return;
        }

        var module = GetModule();
        if (module == null) return;

        string originalName = Path.GetFileName(filePath);
        string extension = Path.GetExtension(originalName).TrimStart('.');
        string suggestedName = $"{Path.GetFileNameWithoutExtension(originalName)}_r{rev}";

        string startingDirectory = svnManager.WorkingDir ?? "";

        string chosenPath = StandaloneFileBrowser.SaveFilePanel(
            "Extract File From SVN Revision",
            startingDirectory,
            suggestedName,
            extension
        );

        if (string.IsNullOrEmpty(chosenPath))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract File] Destination selection cancelled.</color>");
            return;
        }

        await module.ExtractSingleFileToAsync(filePath, rev, chosenPath);
    }

    private async void ExtractFolderFromRevisionAsync()
    {
        if (svnUI == null) svnUI = SVNUI.Instance;
        if (svnManager == null) svnManager = SVNManager.Instance;

        if (svnUI == null)
        {
            SVNLogBridge.LogError("[RevisionPanel] SVNUI.Instance is not ready yet.");
            return;
        }

        string rev = svnUI.UpdateRevisionInput?.text?.Trim()?.TrimStart('r', 'R');
        string folderPath = svnUI.RevisionFilePathInput?.text?.Trim();

        // === FIX: was a silent 'return' with no log - that's why "no logs appear" ===
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract Folder] Please enter or Browse a folder path first.</color>");
            return;
        }

        if (string.IsNullOrWhiteSpace(rev))
        {
            // === FIX: empty revision -> HEAD instead of silence (remove if it should be required) ===
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract Folder] No revision entered - using HEAD (latest server state).</color>");
            rev = "HEAD";
        }
        else if (rev.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            // explicit "HEAD" typed manually - OK
        }
        else if (!long.TryParse(rev, out _))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract Folder] Invalid revision format (numbers only, e.g. 150, or HEAD).</color>");
            return;
        }

        var module = GetModule();
        if (module == null) return;

        string startingDirectory = svnManager.WorkingDir ?? "";

        try
        {
            // Folder name - handles absolute paths and "." (working copy root)
            string rawFolder = folderPath.TrimEnd('/', '\\');
            string folderName = (rawFolder == "." || rawFolder.Length == 0)
                ? new DirectoryInfo(svnManager.WorkingDir ?? ".").Name
                : Path.GetFileName(rawFolder);
            string suggestedName = $"{folderName}_{rev}";

            string[] selectedFolders = StandaloneFileBrowser.OpenFolderPanel(
                "Select Destination Folder for SVN Export",
                startingDirectory,
                false
            );

            // === FIX: destination dialog cancellation was silent too ===
            if (selectedFolders == null || selectedFolders.Length == 0 || string.IsNullOrEmpty(selectedFolders[0]))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>[Extract Folder] Destination selection cancelled.</color>");
                return;
            }

            string chosenParentFolder = selectedFolders[0];

            string targetPath = Path.Combine(chosenParentFolder, suggestedName);
            int counter = 1;
            while (Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(chosenParentFolder, $"{suggestedName}_{counter}");
                counter++;
            }

            await module.ExtractFolderToAsync(folderPath, rev, targetPath);
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogLine($"<color=#FFAA00>[RevisionPanel] Failed to extract folder: {ex.Message}</color>");
        }
    }
}