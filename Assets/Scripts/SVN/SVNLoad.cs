using UnityEngine;
using System.IO;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNLoad : SVNBase
    {
        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void LoadRepoPathAndRefresh()
        {
            if (IsProcessing) return;

            // 1. DATA RETRIEVAL
            // Priority: Load specific inputs > Global Runner state
            string path = svnUI.LoadDestFolderInput.text.Trim();
            string url = svnUI.LoadRepoUrlInput != null ? svnUI.LoadRepoUrlInput.text.Trim() : "";

            string keyPath = (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
                             ? svnUI.LoadPrivateKeyInput.text.Trim()
                             : SvnRunner.KeyPath;

            // 2. VALIDATION
            if (string.IsNullOrEmpty(path))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Destination path cannot be empty!\n";
                return;
            }

            if (!Directory.Exists(path))
            {
                svnUI.LogText.text += $"<color=red>Error:</color> Directory not found: {path}\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text += $"<b>Linking existing repository...</b>\nPath: <color=cyan>{path}</color>\n";

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

                // 5. METADATA VERIFICATION
                if (!Directory.Exists(Path.Combine(normalizedPath, ".svn")))
                {
                    svnUI.LogText.text += "<color=yellow>Warning:</color> No .svn folder found. Attempting to fetch info anyway...\n";
                }

                // 6. REFRESH & DISCOVER URL
                // We run RefreshRepositoryInfo to let SVN tell us the "Truth" about the URL
                await svnManager.RefreshRepositoryInfo();

                if (svnUI.LoadRepoUrlInput != null)
                {
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;
                }

                // 7. PERSISTENCE (Global Keys)
                SaveToSettings(normalizedPath, keyPath, svnManager.RepositoryUrl);

                // 8. UI UPDATE
                svnManager.UpdateBranchInfo();
                svnManager.Button_RefreshStatus();

                svnUI.LogText.text += "<color=green>SUCCESS:</color> Repository linked and synchronized.\n";
            }
            catch (System.Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Load Error:</color> {ex.Message}\n";
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