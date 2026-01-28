using System;
using System.IO;
using UnityEngine;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNSettings : SVNBase
    {
        public SVNSettings(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void SaveRepoUrl()
        {
            if (svnUI.SettingsRepoUrlInput == null) return;

            string newUrl = svnUI.SettingsRepoUrlInput.text.Trim();

            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);
            PlayerPrefs.Save();

            svnManager.RepositoryUrl = newUrl;

            svnUI.LogText.text += $"<color=green>Saved:</color> Repository URL updated to: {newUrl}\n";
        }

        public async void SaveWorkingDir()
        {
            if (IsProcessing) return;

            string newPath = svnUI.SettingsWorkingDirInput.text.Trim();

            if (Directory.Exists(newPath))
            {
                IsProcessing = true;
                try
                {
                    // Persist settings
                    PlayerPrefs.SetString(SVNManager.KEY_WORKING_DIR, newPath);
                    PlayerPrefs.Save();

                    // Sync Manager
                    svnManager.WorkingDir = newPath;

                    // Trigger automatic URL and metadata discovery
                    svnUI.LogText.text += $"<color=yellow>Switching project to:</color> {newPath}\n";

                    await svnManager.RefreshRepositoryInfo();
                    svnManager.UpdateBranchInfo();

                    svnUI.LogText.text += $"<color=green>Success:</color> Working Directory updated and synchronized.\n";

                    // Refresh file explorer
                    svnManager.RefreshStatus();
                }
                catch (Exception ex)
                {
                    svnUI.LogText.text += $"<color=red>Error updating directory:</color> {ex.Message}\n";
                }
                finally
                {
                    IsProcessing = false;
                }
            }
            else
            {
                svnUI.LogText.text += "<color=red>Error:</color> Specified directory does not exist on disk!\n";
            }
        }

        public void SaveSSHKeyPath()
        {
            string path = svnUI.SettingsSshKeyPathInput.text.Trim();
            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, path);
            PlayerPrefs.Save();

            // Update both Manager and Runner
            svnManager.CurrentKey = path;
            SvnRunner.KeyPath = path;

            svnUI.LogText.text += $"<color=green>Saved:</color> SSH Key path updated to: {Path.GetFileName(path)}\n";
        }

        public void SaveMergeEditorPath()
        {
            if (svnUI.SettingsMergeToolPathInput == null) return;

            string newPath = svnUI.SettingsMergeToolPathInput.text.Trim();

            // 1. Safety Check: If the new path is empty, do NOT overwrite the existing save
            if (string.IsNullOrEmpty(newPath))
            {
                Debug.LogWarning("[SVN] Attempted to save an empty Merge Tool path. Action cancelled to prevent data loss.");

                // Optional: Restore the UI text from the last known good value in Manager
                svnUI.SettingsMergeToolPathInput.SetTextWithoutNotify(svnManager.MergeToolPath);
                return;
            }

            // 2. Persistent Save
            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, newPath);
            PlayerPrefs.Save();

            // 3. Logical Sync
            svnManager.MergeToolPath = newPath;

            // 4. Feedback
            if (svnUI.LogText != null)
            {
                svnUI.LogText.text += $"<color=green>Saved:</color> External Merge Tool path updated to: {newPath}\n";
            }
        }

        public void UpdateUIFromManager()
        {
            // Safety check for UI reference
            if (svnUI == null) return;

            // 1. Repository Path (Working Directory)
            if (svnUI.SettingsWorkingDirInput != null)
            {
                // Use SetTextWithoutNotify to prevent triggering 'onValueChanged' listeners
                svnUI.SettingsWorkingDirInput.SetTextWithoutNotify(svnManager.WorkingDir);
            }

            // 2. SSH Private Key Path
            if (svnUI.SettingsSshKeyPathInput != null)
            {
                svnUI.SettingsSshKeyPathInput.SetTextWithoutNotify(svnManager.CurrentKey);
            }

            // 3. Repository URL
            if (svnUI.SettingsRepoUrlInput != null)
            {
                svnUI.SettingsRepoUrlInput.SetTextWithoutNotify(svnManager.RepositoryUrl);
            }

            // 4. Merge Tool Path
            if (svnUI.SettingsMergeToolPathInput != null)
            {
                svnUI.SettingsMergeToolPathInput.SetTextWithoutNotify(svnManager.MergeToolPath);
            }
        }

        public async void LoadSettings()
        {
            // 1. Start loading - lock listeners
            svnManager.IsProcessing = true;

            // 2. Fetch from PlayerPrefs
            string savedDir = PlayerPrefs.GetString(SVNManager.KEY_WORKING_DIR, "");
            string savedKey = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");
            string savedUrl = PlayerPrefs.GetString(SVNManager.KEY_REPO_URL, "");
            string savedMerge = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");

            // 3. Logical Sync
            svnManager.WorkingDir = savedDir;
            svnManager.CurrentKey = savedKey;
            svnManager.RepositoryUrl = savedUrl;
            svnManager.MergeToolPath = savedMerge;
            SvnRunner.KeyPath = savedKey;

            // Wait for UI to be ready
            await System.Threading.Tasks.Task.Yield();

            // 4. UI Sync (Using SetTextWithoutNotify to be extra safe)
            if (svnUI != null)
            {
                // Settings Panel
                if (svnUI.SettingsWorkingDirInput != null) svnUI.SettingsWorkingDirInput.SetTextWithoutNotify(savedDir);
                if (svnUI.SettingsSshKeyPathInput != null) svnUI.SettingsSshKeyPathInput.SetTextWithoutNotify(savedKey);
                if (svnUI.SettingsRepoUrlInput != null) svnUI.SettingsRepoUrlInput.SetTextWithoutNotify(savedUrl);
                if (svnUI.SettingsMergeToolPathInput != null) svnUI.SettingsMergeToolPathInput.SetTextWithoutNotify(savedMerge);

                // Checkout & Load Panels (These often cause the overwrite!)
                if (svnUI.CheckoutRepoUrlInput != null) svnUI.CheckoutRepoUrlInput.SetTextWithoutNotify(savedUrl);
                if (svnUI.CheckoutDestFolderInput != null) svnUI.CheckoutDestFolderInput.SetTextWithoutNotify(savedDir);
                if (svnUI.CheckoutPrivateKeyInput != null) svnUI.CheckoutPrivateKeyInput.SetTextWithoutNotify(savedKey);

                if (svnUI.LoadRepoUrlInput != null) svnUI.LoadRepoUrlInput.SetTextWithoutNotify(savedUrl);
                if (svnUI.LoadDestFolderInput != null) svnUI.LoadDestFolderInput.SetTextWithoutNotify(savedDir);
                if (svnUI.LoadPrivateKeyInput != null) svnUI.LoadPrivateKeyInput.SetTextWithoutNotify(savedKey);
            }

            // 5. Operational Init
            if (!string.IsNullOrEmpty(savedDir) && System.IO.Directory.Exists(savedDir))
            {
                await svnManager.SetWorkingDirectory(savedDir);
                svnManager.RefreshStatus();
            }

            // 6. Unlock listeners
            svnManager.IsProcessing = false;
            Debug.Log("[SVN] Settings loaded and listeners unlocked.");
        }

        public async void SetupUnrealIgnore()
        {
            if (IsProcessing) return;

            // Ensure we have a valid path to work with
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Invalid Working Directory. Cannot set ignore properties.\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text += "Configuring svn:ignore for Unreal Engine standards...\n";

            try
            {
                // Execute command: svn propset svn:ignore ...
                string output = await SvnRunner.IgnoreDefaultsAsync(root);

                svnUI.LogText.text += "<color=green>Ignore properties configured for:</color>\n";
                svnUI.LogText.text += "Binaries/, Intermediate/, Saved/, DerivedDataCache/, .vs/, *.sln\n";
                svnUI.LogText.text += "------------------------------------------\n";
                svnUI.LogText.text += output + "\n";
                svnUI.LogText.text += "------------------------------------------\n";

                svnUI.LogText.text += "<color=cyan>INFO:</color> Properties added locally.\n";
                svnUI.LogText.text += "<b>You must Commit the root folder</b> for these filters to affect other team members.\n";

                // Refresh status to show the property change on the root folder
                svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Ignore Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void RefreshUI()
        {
            // 1.Fill URL(optional, if you have a URL field in Settings)
            if (svnUI.SettingsRepoUrlInput != null)
            {
                svnUI.SettingsRepoUrlInput.text = svnManager.RepositoryUrl;
            }

            // 2.Fill Working Directory from Manager or Prefs

            if (svnUI.SettingsWorkingDirInput != null)
            {
                svnUI.SettingsWorkingDirInput.text = svnManager.WorkingDir;
            }

            // 3. Fill SSH Key Path
            if (svnUI.SettingsSshKeyPathInput != null)
            {
                svnUI.SettingsSshKeyPathInput.text = svnManager.CurrentKey;
            }

            // 4. Fill Merge Tool (This one is only in Settings)
            if (svnUI.SettingsMergeToolPathInput != null)
            {
                svnUI.SettingsMergeToolPathInput.text = svnManager.MergeToolPath;
            }
        }
    }
}