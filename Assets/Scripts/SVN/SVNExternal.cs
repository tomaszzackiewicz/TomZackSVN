using SFB;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

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

                _ = svnManager.SetWorkingDirectory(selectedPath).ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        SVNLogBridge.LogError($"Failed to set working directory: {task.Exception.Message}");
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());

                SVNLogBridge.LogLine($"SVN path selected: {selectedPath}");
            }
            else
            {
                SVNLogBridge.LogLine("Folder selection canceled.");
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

                SVNLogBridge.LogLine($"Private Key path set to: {selectedPath}");
            }
            else
            {
                SVNLogBridge.LogLine("Private Key selection canceled by user.");
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
            var extensions = new[] {
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh"),
                new ExtensionFilter("All Files", "*")
            };
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

                SVNLogBridge.LogLine($"[Checkout] Destination path set to: {selectedPath}");
            }
        }

        public void BrowsePrivateKeyPathCheckout()
        {
            var extensions = new[] {
                new ExtensionFilter("All Files", "*"),
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select SSH Private Key for Checkout", "", extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (svnUI.CheckoutPrivateKeyInput != null)
                {
                    svnUI.CheckoutPrivateKeyInput.text = selectedPath;
                }

                SVNLogBridge.LogLine($"[Checkout] SSH Key path set to: {selectedPath}");
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
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!", true);
                }

                if (svnUI.ResolveTargetFileInput != null)
                {
                    svnUI.ResolveTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Resolve:</color> Selected target file: {selectedPath}");
                }
                else
                {
                    SVNLogBridge.LogError("[SVN] ResolveTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseDiffFilePath()
        {
            string root = svnManager.WorkingDir;

            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!", true);
                }

                if (svnUI.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
                else
                {
                    SVNLogBridge.LogError("[SVN] DiffTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseBlameFilePath()
        {
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> Working Directory is not set or does not exist!");
                return;
            }

            var extensions = new[] {
        new ExtensionFilter("All Files", "*")
    };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File for Blame", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');

                    if (svnUI.BlameTargetFileInput != null)
                    {
                        svnUI.BlameTargetFileInput.text = selectedPath;
                        SVNLogBridge.LogLine($"<color=green>Blame:</color> Target file set to: {selectedPath}");
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                }
            }
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> Working directory not set.");
                return;
            }

            string args = $"/command:log /path:\"{root}\"";
            System.Diagnostics.Process.Start("TortoiseProc.exe", args);
            SVNLogBridge.LogLine("<b>[External]</b> Opening TortoiseSVN SVNLogBridge.LogLine...");
        }

        public void SaveHistoryToFile(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No content to export.");
                return;
            }

            string defaultName = $"SVN_History_{DateTime.Now:yyyyMMdd_HHmm}";
            string path = StandaloneFileBrowser.SaveFilePanel("Save SVN History Report", "", defaultName, "txt");

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    System.IO.File.WriteAllText(path, content);
                    SVNLogBridge.LogLine($"<color=green>Success:</color> History exported to {path}");

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=red>Export Error:</color> {ex.Message}");
                }
            }
        }

        public void OpenInExplorerAndSelect(string relativePath)
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root)) return;

                string fullPath = System.IO.Path.Combine(root, relativePath).Replace('/', '\\');

                if (System.IO.File.Exists(fullPath) || System.IO.Directory.Exists(fullPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", root.Replace('/', '\\'));
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Explorer Error:</color> {ex.Message}");
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(long wEventId, uint uFlags, string dwItem1, string dwItem2);

        private const long SHCNE_UPDATEDIR = 0x00001000L;
        private const uint SHCNF_PATHW = 0x0005;

        public void RefreshWindowsShellIcons(string targetPath)
        {
            try
            {
                Process[] cacheProcesses = Process.GetProcessesByName("TSVNCache");
                foreach (var process in cacheProcesses)
                {
                    process.Kill();
                }

                string fullPath = Path.Combine(svnManager.WorkingDir, targetPath);
                string directoryToRefresh = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;

                if (!string.IsNullOrEmpty(directoryToRefresh))
                {
                    SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, directoryToRefresh, null);
                }

                LogBoth("[Shell] Triggered Windows Explorer icon cache update.");
            }
            catch (Exception ex)
            {
                LogBoth($"[Shell Error] Failed to refresh icons: {ex.Message}");
            }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
        }
    }
}