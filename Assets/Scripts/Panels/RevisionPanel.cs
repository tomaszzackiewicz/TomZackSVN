using System;
using System.IO;
using SFB;
using SVN.Core;
using UnityEngine;

public class RevisionPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_UpdateToRevision() => svnManager.GetModule<SVNRevision>().UpdateToRevisionButton();
    public void Button_ExportRevision() => svnManager.GetModule<SVNRevision>().ExportRevisionButton();
    public void Button_RevertRevision() => svnManager.GetModule<SVNRevision>().RevertCommitsButton();
    public void Button_RestoreSingleFileFromRevision() => RestoreSingleFileFromRevisionAsync();
    public void Button_ExtractSingleFileFromRevision() => ExtractSingleFileFromRevisionAsync();
    public void Button_ExtractFolderFromRevision() => ExtractFolderFromRevisionAsync();
    public void Button_RevertForPath() => RevertPathFromInputAsync();

    private async void RevertPathFromInputAsync()
    {
        if (svnUI == null || svnManager == null) return;

        string path = svnUI.RevisionFilePathInput?.text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Revert Path] Please enter a file or folder path.</color>");
            return;
        }

        await svnManager.GetModule<SVNRevision>().RevertPathAsync(path);
    }

    private async void RestoreSingleFileFromRevisionAsync()
    {
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
        string filePath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev) || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await svnManager.GetModule<SVNRevision>().RestoreSingleFileAsync(filePath, rev);
    }

    private async void ExtractSingleFileFromRevisionAsync()
    {
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
        string filePath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev) || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

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

        if (string.IsNullOrEmpty(chosenPath)) return;

        await svnManager.GetModule<SVNRevision>().ExtractSingleFileToAsync(filePath, rev, chosenPath);
    }

    private async void ExtractFolderFromRevisionAsync()
    {
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
        string folderPath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev) || string.IsNullOrWhiteSpace(folderPath)) return;

        string folderName = new DirectoryInfo(folderPath).Name;
        string suggestedName = $"{folderName}_r{rev}";

        string startingDirectory = svnManager.WorkingDir ?? "";

        // Poprawka: OpenFolderPanel zwraca tablicę string[]
        string[] selectedFolders = StandaloneFileBrowser.OpenFolderPanel(
            "Select Destination Folder for SVN Export",
            startingDirectory,
            false
        );

        if (selectedFolders == null || selectedFolders.Length == 0 || string.IsNullOrEmpty(selectedFolders[0]))
            return;

        string chosenParentFolder = selectedFolders[0];

        // Tworzymy podfolder o sugerowanej nazwie w wybranym katalogu
        string targetPath = Path.Combine(chosenParentFolder, suggestedName);
        int counter = 1;
        while (Directory.Exists(targetPath))
        {
            targetPath = Path.Combine(chosenParentFolder, $"{suggestedName}_{counter}");
            counter++;
        }

        try
        {
            await svnManager.GetModule<SVNRevision>().ExtractFolderToAsync(folderPath, rev, targetPath);
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogLine($"<color=#FFAA00>[RevisionPanel] Failed to extract folder: {ex.Message}</color>");
        }
    }
}