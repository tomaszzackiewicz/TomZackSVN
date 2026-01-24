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
                svnUI.LogText.text += "<color=red>Error:</color> Please provide both Repository URL and Destination Path.\n";
                return;
            }

            // 3. PERSISTENCE
            SaveInputsToSettings(path, keyPath, url);

            // 4. INITIALIZE PROCESS
            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            svnUI.LogText.text += $"<b>Initializing SSH Checkout...</b> <color=yellow>(Cancel available)</color>\n" +
                                  $"Source: <color=cyan>{url}</color>\n";

            try
            {
                string safeKey = keyPath.Replace("\\", "/");
                string sshConfig = !string.IsNullOrEmpty(safeKey)
                    ? $"--config-option config:tunnels:ssh=\"ssh -i '{safeKey}' -o StrictHostKeyChecking=no\""
                    : "";

                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive";

                // Ensure parent directory exists
                string parentDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                // 5. EXECUTION
                await SvnRunner.RunAsync(cmd, "", true, _checkoutCTS.Token);

                // 6. SUCCESS HANDLING
                svnUI.LogText.text += "<color=green>SUCCESS:</color> Checkout completed successfully.\n";

                svnManager.WorkingDir = path;

                await svnManager.RefreshRepositoryInfo();
                svnManager.UpdateBranchInfo();
                svnManager.Button_RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                svnUI.LogText.text += "<color=orange>CANCELLED:</color> Checkout process was stopped by user.\n";
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