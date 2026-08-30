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

    // === FIX K1: null-safe dostęp do modułu — InitializeAllModules ma try/catch,
    // więc przy częściowej porażce inicjalizacji GetModule zwraca null, a stare
    // wywołania rzucały NRE w async void (nieprzechwycone).
    private SVNRevision GetModule()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        var module = svnManager?.GetModule<SVNRevision>();
        if (module == null)
            SVNLogBridge.LogError("[RevisionPanel] SVNRevision module is not available.");
        return module;
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
        if (svnUI == null || svnManager == null) return;

        string path = svnUI.RevisionFilePathInput?.text?.Trim();
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
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
        string filePath = svnUI.RevisionFilePathInput?.text?.Trim();

        // === FIX K2: czytelny komunikat zamiast cichego return + walidacja formatu
        // (śmieci typu "abc" odrzucane TU, a nie przez błąd svn dalej).
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
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
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

        if (string.IsNullOrEmpty(chosenPath)) return;

        await module.ExtractSingleFileToAsync(filePath, rev, chosenPath);
    }

    private async void ExtractFolderFromRevisionAsync()
    {
        if (svnUI == null || svnManager == null) return;

        string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
        string folderPath = svnUI.RevisionFilePathInput?.text?.Trim();

        if (string.IsNullOrWhiteSpace(rev) || string.IsNullOrWhiteSpace(folderPath)) return;

        if (!long.TryParse(rev, out _))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Extract Folder] Invalid revision format (numbers only, e.g. 150).</color>");
            return;
        }

        var module = GetModule();
        if (module == null) return;

        string startingDirectory = svnManager.WorkingDir ?? "";

        try
        {
            // === FIX K3: DirectoryInfo (nielegalne znaki w ścieżce → ArgumentException)
            // przeniesione DO try — wcześniej stało przed blokiem i rzucało nieprzechwycone.
            string folderName = new DirectoryInfo(folderPath).Name;
            string suggestedName = $"{folderName}_r{rev}";

            string[] selectedFolders = StandaloneFileBrowser.OpenFolderPanel(
                "Select Destination Folder for SVN Export",
                startingDirectory,
                false
            );

            if (selectedFolders == null || selectedFolders.Length == 0 || string.IsNullOrEmpty(selectedFolders[0]))
                return;

            string chosenParentFolder = selectedFolders[0];

            // Podfolder o sugerowanej nazwie w wybranym katalogu (anty-kolizyjnie).
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