using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;
        private readonly SynchronizationContext _mainThreadContext;

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        public async void Checkout()
        {
            if (IsProcessing) return;

            // 1. GET DATA FROM UI (With corrected priority hierarchy)
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            // Key selection logic:
            string keyPath = "";

            // A. Was the key provided directly in the Checkout panel?
            if (!string.IsNullOrWhiteSpace(svnUI.CheckoutPrivateKeyInput?.text))
            {
                keyPath = svnUI.CheckoutPrivateKeyInput.text.Trim();
            }
            // B. Is there a key in the Settings panel? (Accessed via UI)
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
            {
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            }
            // C. Final fallback to Manager/Runner
            else
            {
                keyPath = SvnRunner.KeyPath;
            }

            // Final validation
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Provide both URL and Destination Path.\n";
                return;
            }

            if (string.IsNullOrEmpty(keyPath))
            {
                svnUI.LogText.text += "<color=red>Error:</color> SSH Key path is empty! Provide it in Checkout or Settings.\n";
                return;
            }

            // 2. STATE PREPARATION
            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0f;
            }

            // 3. LOG MONITORING (Task.Run...)
            bool isMonitoring = true;
            _ = Task.Run(async () => { /* ... monitoring loop ... */ });

            // 4. EXECUTE SVN PROCESS
            try
            {
                // Format key path for SSH (escaping quotes for Windows)
                string absoluteKey = Path.GetFullPath(keyPath).Replace("\\", "/");

                // Build SSH command to handle spaces in paths
                string sshArgs = $"ssh -i \\\"{absoluteKey}\\\" -o StrictHostKeyChecking=no -o BatchMode=yes";
                string sshConfig = $"--config-option config:tunnels:ssh=\"{sshArgs}\"";

                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive --force";

                if (!Directory.Exists(path)) Directory.CreateDirectory(path);

                svnUI.LogText.text += $"<b>Starting Checkout...</b>\nPath: <color=cyan>{path}</color>\n";
                svnUI.LogText.text += $"Using Key: <color=grey>{absoluteKey}</color>\n";

                await SvnRunner.RunAsync(cmd, "", true, _checkoutCTS.Token);

                // SUCCESS
                svnUI.LogText.text += "<color=green>SUCCESS:</color> Checkout completed.\n";
                if (svnUI.CheckoutStatusInfoText != null) svnUI.CheckoutStatusInfoText.text = "<color=green>Checkout Finished!</color>";

                RegisterNewProjectAfterCheckout(path, url, keyPath);

                svnManager.WorkingDir = path;
                await svnManager.RefreshRepositoryInfo();
                await svnManager.RefreshStatus();

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(1000);
                    svnManager.PanelHandler.Button_CloseCheckout();
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Checkout Error:</color> {ex.Message}\n";
                if (svnUI.CheckoutStatusInfoText != null) svnUI.CheckoutStatusInfoText.text = "<color=red>Failed!</color>";
            }
            finally
            {
                isMonitoring = false;
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

        private void RegisterNewProjectAfterCheckout(string path, string repoUrl, string keyPath)
        {
            var newProj = new SVNProject
            {
                projectName = Path.GetFileName(path),
                repoUrl = repoUrl,
                workingDir = path,
                privateKeyPath = keyPath,
                lastOpened = DateTime.Now
            };

            List<SVNProject> projects = ProjectSettings.LoadProjects();
            if (!projects.Exists(p => p.workingDir == path))
            {
                projects.Add(newProj);
                ProjectSettings.SaveProjects(projects);
                svnUI.LogText.text += $"<color=green>Project '{newProj.projectName}' added to Selection List.</color>\n";
            }

            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", path);
            PlayerPrefs.Save();
        }
    }
}