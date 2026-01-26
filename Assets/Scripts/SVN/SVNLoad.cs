using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLoad : SVNBase
    {
        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void LoadRepoPathAndRefresh()
        {
            if (IsProcessing) return;

            // 1. DATA RETRIEVAL
            string path = svnUI.LoadDestFolderInput.text.Trim();
            string manualUrl = svnUI.LoadRepoUrlInput != null ? svnUI.LoadRepoUrlInput.text.Trim() : "";
            string keyPath = (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
                             ? svnUI.LoadPrivateKeyInput.text.Trim()
                             : SvnRunner.KeyPath;

            // 2. PRE-VALIDATION
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Invalid destination path!\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text += $"<b>Processing path:</b> <color=cyan>{path}</color>\n";

            try
            {
                // 3. PATH NORMALIZATION
                string normalizedPath = path.Replace("\\", "/");
                svnManager.WorkingDir = normalizedPath;
                svnUI.LoadDestFolderInput.text = normalizedPath;

                // 4. SSH KEY SYNC
                if (!string.IsNullOrEmpty(keyPath))
                {
                    SvnRunner.KeyPath = keyPath;
                    svnManager.CurrentKey = keyPath;
                }

                // 5. METADATA & PROTECTION CHECK
                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (hasSvnFolder)
                {
                    // PROTECTED: Already an SVN project, just link it
                    svnUI.LogText.text += "<b>Existing repository detected.</b> Linking files...\n";
                    await svnManager.RefreshRepositoryInfo();
                }
                else
                {
                    // CHECKOUT SCENARIO: No .svn folder found
                    if (!string.IsNullOrEmpty(manualUrl))
                    {
                        // Check if the directory is empty
                        bool isFolderEmpty = Directory.GetFileSystemEntries(normalizedPath).Length == 0;
                        string forceFlag = isFolderEmpty ? "" : " --force";

                        if (!isFolderEmpty)
                            svnUI.LogText.text += "<color=orange>Note:</color> Folder not empty. Merging with existing files...\n";

                        svnUI.LogText.text += "<color=yellow>Starting Checkout...</color>\n";

                        // Execute checkout with or without --force
                        await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath);

                        svnUI.LogText.text += "<color=green>Checkout completed!</color>\n";
                        await svnManager.RefreshRepositoryInfo();
                    }
                    else
                    {
                        svnUI.LogText.text += "<color=red>Error:</color> Path is not a repository and no URL provided!\n";
                        IsProcessing = false;
                        return;
                    }
                }

                // 6. FINAL SYNC & PERSISTENCE
                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;

                SaveToSettings(normalizedPath, keyPath, svnManager.RepositoryUrl);

                // 8. REFRESH SYSTEM
                svnManager.UpdateBranchInfo();
                svnManager.RefreshStatus();

                svnUI.LogText.text += "<color=green>SUCCESS:</color> System synchronized.\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Operation Failed:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Load Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void UpdateUIFromManager()
        {
            // Optional: If your Add Repo panel has URL or Key fields, fill them too
            if (svnUI.LoadRepoUrlInput != null)
            {
                svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;
            }

            // Fill the Local Path input with the current WorkingDir
            if (svnUI.LoadDestFolderInput != null)
            {
                svnUI.LoadDestFolderInput.text = svnManager.WorkingDir;
            }

            if (svnUI.LoadPrivateKeyInput != null)
            {
                svnUI.LoadPrivateKeyInput.text = svnManager.CurrentKey;
            }
        }

        private void SaveToSettings(string path, string key, string url)
        {
            PlayerPrefs.SetString(SVNManager.KEY_WORKING_DIR, path);
            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, key);
            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, url);
            PlayerPrefs.Save();

            svnUI.LogText.text += "<color=#888888>System configuration synchronized.</color>\n";
        }
    }
}