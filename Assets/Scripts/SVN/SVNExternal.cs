using System;
using UnityEngine;
using System.Runtime.InteropServices;

namespace SVN.Core
{
    public class SVNExternal : SVNBase
    {
        public SVNExternal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void OpenInExplorer()
        {
            try
            {
                // Using verified path from the Manager
                string root = svnManager.WorkingDir;

                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                {
                    svnUI.LogText.text += "<color=red>Error: Working directory is not set or does not exist!</color>\n";
                    return;
                }

                // Standard Windows Explorer call
                // We use backslashes for Windows Explorer compatibility
                System.Diagnostics.Process.Start("explorer.exe", root.Replace('/', '\\'));

                svnUI.LogText.text += $"<color=green>Explorer:</color> Opened {root}\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Explorer Error:</color> {ex.Message}\n";
            }
        }

        public async void ShowChangesForSelected(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                svnUI.LogText.text += "<color=yellow>Warning:</color> No file selected for Diff.\n";
                return;
            }

            string root = svnManager.WorkingDir;
            string fullPath = System.IO.Path.Combine(root, relativePath);

            if (!System.IO.File.Exists(fullPath))
            {
                svnUI.LogText.text += "<color=red>Error:</color> File not found on disk.\n";
                return;
            }

            try
            {
                svnUI.LogText.text += $"Opening Diff for: {relativePath}...\n";

                // Command: svn diff --diff-cmd [external_tool]
                // However, it's easier to use the default 'TortoiseIDiff.exe' if available,
                // or simply call 'svn diff' which outputs to console.

                // Let's use the standard SVN command that works with the system's default diff tool:
                await SvnRunner.RunAsync($"diff \"{relativePath}\" --external-diff-cmd TortoiseMerge", root);
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Diff Error:</color> {ex.Message}\n";
            }
        }

        public void BrowseLocalPath()
        {
            //// W Standalone u¿ywamy System.Windows.Forms
            //string selectedPath = "";
            //using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            //{
            //    dialog.Description = "Select SVN Working Directory";
            //    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            //    {
            //        selectedPath = dialog.SelectedPath;
            //    }
            //}

            //if (!string.IsNullOrEmpty(selectedPath))
            //{
            //    selectedPath = selectedPath.Replace('\\', '/');
            //    svnManager.WorkingDir = selectedPath;

            //    // Aktualizacja UI (opcjonalnie)
            //    if (svnUI.WorkingDirInput != null) svnUI.WorkingDirInput.text = selectedPath;

            //    // Odœwie¿enie danych w Managerze
            //    _ = svnManager.SetWorkingDirectory(selectedPath);
            //}
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.RepositoryUrl;
            // Wywo³anie TortoiseSVN bezpoœrednio z komendy (jeœli u¿ytkownik ma go zainstalowanego)
            string args = $"/command:log /path:\"{root}\"";
            System.Diagnostics.Process.Start("TortoiseProc.exe", args);
        }
    }

}