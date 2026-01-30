using System;
using UnityEngine;
using SFB;

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
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0];

                selectedPath = selectedPath.Replace('\\', '/');

                svnManager.WorkingDir = selectedPath;

                if (svnUI.LoadDestFolderInput != null)
                    svnUI.LoadDestFolderInput.text = selectedPath;

                _ = svnManager.SetWorkingDirectory(selectedPath);

                Debug.Log($"Wybrano œcie¿kê SVN: {selectedPath}");
            }
            else
            {
                Debug.Log("Anulowano wybór folderu.");
            }
        }

        public void BrowsePrivateKeyPath()
        {
            // 1. Define file filters
            // Since keys often have no extension, we provide an "All Files" filter.
            // Some systems still use .ppk or .key, so we include those for convenience.
            var extensions = new[] {
            new ExtensionFilter("All Files", "*"),
            new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh")
        };

            // 2. Open File Dialog
            // Parameters: Title, Starting Directory, Extension Filters, Multiselect
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key File", "", extensions, false);

            // 3. Validation
            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                // 4. Update Manager
                // Adjust the field name 'PrivateKeyPath' to match your svnManager structure
                svnManager.CurrentKey = selectedPath;

                // 5. Update UI
                // Adjust the field name 'PrivateKeyInput' to match your svnUI structure
                if (svnUI.LoadPrivateKeyInput != null)
                {
                    svnUI.LoadPrivateKeyInput.text = selectedPath;
                }

                Debug.Log($"Private Key path set to: {selectedPath}");
            }
            else
            {
                Debug.Log("Private Key selection canceled by user.");
            }
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.RepositoryUrl;
            string args = $"/command:log /path:\"{root}\"";
            System.Diagnostics.Process.Start("TortoiseProc.exe", args);
        }
    }

}