using System;
using System.IO;
using UnityEngine;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNSettings : SVNBase
    {
        public SVNSettings(SVNUI ui, SVNManager manager) : base(ui, manager) { }

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
                    svnManager.Button_RefreshStatus();
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
            string path = svnUI.SettingsMergeToolPathInput.text.Trim();
            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, path);
            PlayerPrefs.Save();

            svnUI.LogText.text += $"<color=green>Saved:</color> External Merge Tool path updated.\n";
        }

        public void UpdateUIFromManager()
        {
            // 1. Repository Path (Working Directory)
            if (svnUI.SettingsWorkingDirInput != null)
            {
                svnUI.SettingsWorkingDirInput.text = svnManager.WorkingDir;
            }

            // 2. SSH Private Key Path
            if (svnUI.SettingsSshKeyPathInput != null)
            {
                svnUI.SettingsSshKeyPathInput.text = svnManager.CurrentKey;
            }

            // 3. Repository URL (The "link" to the repo)
            if (svnUI.SettingsRepoUrlInput != null)
            {
                svnUI.SettingsRepoUrlInput.text = svnManager.RepositoryUrl;
            }

            // 4. Merge Tool Path (Loaded directly from PlayerPrefs)
            if (svnUI.SettingsMergeToolPathInput != null)
            {
                svnUI.SettingsMergeToolPathInput.text = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
            }
        }

        public async void LoadSettings()
        {
            string savedDir = PlayerPrefs.GetString(SVNManager.KEY_WORKING_DIR, "");
            string savedKey = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");
            string savedUrl = PlayerPrefs.GetString(SVNManager.KEY_REPO_URL, "");

            // Wype³nianie pól w Settings
            if (svnUI.SettingsWorkingDirInput != null) svnUI.SettingsWorkingDirInput.text = savedDir;
            if (svnUI.SettingsSshKeyPathInput != null) svnUI.SettingsSshKeyPathInput.text = savedKey;

            // Wype³nianie pól w Checkout (¿eby nie wpisywaæ tego co restart)
            if (svnUI.CheckoutRepoUrlInput != null) svnUI.CheckoutRepoUrlInput.text = savedUrl;
            if (svnUI.CheckoutDestFolderInput != null) svnUI.CheckoutDestFolderInput.text = savedDir;
            if (svnUI.CheckoutPrivateKeyInput != null) svnUI.CheckoutPrivateKeyInput.text = savedKey;

            // Synchronizacja logiczna
            svnManager.WorkingDir = savedDir;
            svnManager.CurrentKey = savedKey;
            SvnRunner.KeyPath = savedKey;

            if (!string.IsNullOrEmpty(savedDir))
            {
                await svnManager.SetWorkingDirectory(savedDir);
            }
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
                svnManager.Button_RefreshStatus();
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
                string savedMerge = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                svnUI.SettingsMergeToolPathInput.text = savedMerge;
            }
        }
    }
}