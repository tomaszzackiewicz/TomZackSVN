using System;
using UnityEngine;
using System.IO;
using System.Threading;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager) { }

        public async void Checkout()
        {
            if (IsProcessing) return;

            // 1. DATA RETRIEVAL
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            string keyPath = string.IsNullOrWhiteSpace(svnUI.CheckoutPrivateKeyInput.text)
                             ? SvnRunner.KeyPath
                             : svnUI.CheckoutPrivateKeyInput.text.Trim();

            // 2. VALIDATION
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Provide both URL and Destination Path.\n";
                return;
            }

            // --- NEW SECURITY CHECK ---
            if (Directory.Exists(path))
            {
                // Check if it's already an SVN repository
                if (Directory.Exists(Path.Combine(path, ".svn")))
                {
                    svnUI.LogText.text += "<color=red>Abort:</color> Destination already contains an SVN repository!\n";
                    return;
                }

                // Check if directory is not empty
                if (Directory.GetFileSystemEntries(path).Length > 0)
                {
                    svnUI.LogText.text += "<color=orange>Warning:</color> Destination folder is not empty. Proceeding with --force...\n";
                }
            }
            // ---------------------------

            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            try
            {
                // 3. PERSISTENCE (Save after validation)
                SaveInputsToSettings(path, keyPath, url);

                string safeKey = keyPath.Replace("\\", "/");
                string sshConfig = !string.IsNullOrEmpty(safeKey)
                    ? $"--config-option config:tunnels:ssh=\"ssh -i '{safeKey}' -o StrictHostKeyChecking=no\""
                    : "";

                // Added --force to handle non-empty directories safely
                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive --force";

                // 4. DIRECTORY PREPARATION
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                svnUI.LogText.text += $"<b>Starting Checkout...</b>\nPath: <color=cyan>{path}</color>\n";

                // 5. EXECUTION
                await SvnRunner.RunAsync(cmd, "", true, _checkoutCTS.Token);

                // 6. SUCCESS & SYNC
                svnUI.LogText.text += "<color=green>SUCCESS:</color> Checkout completed.\n";
                svnManager.WorkingDir = path;

                await svnManager.RefreshRepositoryInfo();
                svnManager.UpdateBranchInfo();
                svnManager.Button_RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                svnUI.LogText.text += "<color=orange>CANCELLED:</color> Process stopped by user.\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Checkout Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
                _checkoutCTS?.Dispose();
                _checkoutCTS = null;
            }
        }

        public void CancelCheckout()
        {
            if (_checkoutCTS != null)
            {
                svnUI.LogText.text += "<color=yellow>Cancelling operation...</color>\n";
                _checkoutCTS.Cancel();
            }
        }

        private void SaveInputsToSettings(string workingDir, string sshKey, string repoUrl)
        {
            svnManager.WorkingDir = workingDir;
            SvnRunner.KeyPath = sshKey;

            PlayerPrefs.SetString(SVNManager.KEY_WORKING_DIR, workingDir);
            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, sshKey);
            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, repoUrl);
            PlayerPrefs.Save();

            svnUI.LogText.text += "<color=#888888>Settings persisted to system storage.</color>\n";
        }
    }
}