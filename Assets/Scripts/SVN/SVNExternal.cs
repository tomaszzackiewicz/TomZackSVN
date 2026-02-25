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
                    SVNLogBridge.LogLine("<color=red>Error: Working directory is not set or does not exist!</color>");
                    return;
                }

                System.Diagnostics.Process.Start("explorer.exe", root.Replace('/', '\\'));
                SVNLogBridge.LogLine($"<color=green>Explorer:</color> Opened {root}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Explorer Error:</color> {ex.Message}");
            }
        }

        public async void ShowChangesForSelected(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No file selected for Diff.");
                return;
            }

            string root = svnManager.WorkingDir;
            string fullPath = System.IO.Path.Combine(root, relativePath);

            if (!System.IO.File.Exists(fullPath))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> File not found on disk.");
                return;
            }

            try
            {
                SVNLogBridge.LogLine($"Opening Diff for: {relativePath}...");
                await SvnRunner.RunAsync($"diff \"{relativePath}\" --external-diff-cmd TortoiseMerge", root);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Diff Error:</color> {ex.Message}");
            }
        }

        public void BrowseDestinationFolderPathLoad()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
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
                string path = paths[0].Replace('\\', '/');
                SVNUI.Instance.AddProjectFolderPathInput.text = path;

                if (string.IsNullOrEmpty(SVNUI.Instance.AddProjectNameInput.text))
                {
                    SVNUI.Instance.AddProjectNameInput.text = System.IO.Path.GetFileName(path);
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

        public void BrowseResolveFilePath()
        {
            string root = svnManager.WorkingDir;

            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Resolve", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (selectedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(root.Length).TrimStart('/');
                }

                if (svnUI.ResolveTargetFileInput != null)
                {
                    svnUI.ResolveTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Resolve:</color> Selected target file: {selectedPath}");
                }
                else
                {
                    Debug.LogWarning("[SVN] ResolveTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseDiffFilePath()
        {
            // Pobieramy ścieżkę roboczą z managera
            string root = svnManager.WorkingDir;

            // Definiujemy filtr plików (wszystkie pliki)
            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            // Otwieramy okno wyboru pliku (false na końcu oznacza brak multiselectu)
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            // Sprawdzamy czy użytkownik coś wybrał
            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                // Normalizujemy ukośniki
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                // Sprawdzamy, czy wybrany plik znajduje się wewnątrz WorkingDir
                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Wycinamy ścieżkę absolutną, zostawiając relatywną dla SVN
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!", true);
                }

                // Przypisujemy do pola Input w panelu Diff
                if (svnUI.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
                else
                {
                    Debug.LogWarning("[SVN] DiffTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseBlameFilePath()
        {
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> Working Directory is not set!", true);
                return;
            }

            // Filtr plików tekstowych jest świetnym pomysłem dla Blame
            var extensions = new[] {
        new ExtensionFilter("Text Files", "cs", "shader", "json", "txt", "xml", "yaml"),
        new ExtensionFilter("All Files", "*")
    };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File for Blame", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Konwersja na ścieżkę relatywną
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');

                    if (svnUI.BlameTargetFileInput != null)
                    {
                        svnUI.BlameTargetFileInput.text = selectedPath;
                        SVNLogBridge.LogLine($"<color=green>Blame:</color> Target file set to: {selectedPath}");
                    }
                    else
                    {
                        Debug.LogWarning("[SVN] BlameTargetFileInput is not assigned in SVNUI!");
                    }
                }
                else
                {
                    // Bardzo ważne ostrzeżenie
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file for Blame is outside of the Working Directory!", true);
                }
            }
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.RepositoryUrl;
            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> Repository URL not found.");
                return;
            }

            string args = $"/command:log /path:\"{root}\"";
            System.Diagnostics.Process.Start("TortoiseProc.exe", args);
            SVNLogBridge.LogLine("<b>[External]</b> Opening TortoiseSVN Log...");
        }
    }
}