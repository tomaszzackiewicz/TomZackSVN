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
                string root = svnManager.WorkingDir;

                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                {
                    svnUI.LogText.text += "<color=red>Error: Working directory is not set or does not exist!</color>\n";
                    return;
                }

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

                await SvnRunner.RunAsync($"diff \"{relativePath}\" --external-diff-cmd TortoiseMerge", root);
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Diff Error:</color> {ex.Message}\n";
            }
        }

        public void BrowseDestinationFolderPathLoad()
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

                Debug.Log($"SVN path selected: {selectedPath}");
            }
            else
            {
                Debug.Log("Folder selection canceled.");
            }
        }

        public void BrowsePrivateKeyPathLoad()
        {
            var extensions = new[] {
            new ExtensionFilter("All Files", "*"),
            new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh")
        };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key File", "", extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                svnManager.CurrentKey = selectedPath;

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

        public void BrowseDestinationFolderPathAdd()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                SVNUI.Instance.AddProjectFolderPathInput.text = paths[0].Replace('\\', '/');

                if (string.IsNullOrEmpty(SVNUI.Instance.AddProjectNameInput.text))
                {
                    SVNUI.Instance.AddProjectNameInput.text = System.IO.Path.GetFileName(paths[0]);
                }
            }
        }

        public void BrowsePrivateKeyPathAdd()
        {
            var extensions = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key", "", extensions, false);
            if (paths != null && paths.Length > 0)
            {
                SVNUI.Instance.AddProjectKeyPathInput.text = paths[0].Replace('\\', '/');
            }
        }

        public void BrowseDestinationFolderPathCheckout()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select Checkout Destination Directory", "", false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (svnUI.CheckoutDestFolderInput != null)
                {
                    svnUI.CheckoutDestFolderInput.text = selectedPath;
                }

                Debug.Log($"[Checkout] Destination path set to: {selectedPath}");
            }
        }


        public void BrowsePrivateKeyPathCheckout()
        {
           var extensions = new[] {
                new ExtensionFilter("All Files", "*"),
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh")
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select SSH Private Key for Checkout", "", extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (svnUI.CheckoutPrivateKeyInput != null)
                {
                    svnUI.CheckoutPrivateKeyInput.text = selectedPath;
                }

                Debug.Log($"[Checkout] SSH Key path set to: {selectedPath}");
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