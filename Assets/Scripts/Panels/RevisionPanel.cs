using SVN.Core;
using System.IO;
using SFB;
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
}