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

            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string path = svnUI.CheckoutDestFolderInput.text.Trim();
            string keyPath = "";

            if (!string.IsNullOrWhiteSpace(svnUI.CheckoutPrivateKeyInput?.text))
                keyPath = svnUI.CheckoutPrivateKeyInput.text.Trim();
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            else
                keyPath = SvnRunner.KeyPath;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
            {
                string errorMsg = "<color=red>Error:</color> Provide both URL and Destination Path.";
                SVNLogBridge.LogLine(errorMsg);
                // Używamy: uiField, content, logLabel
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, errorMsg, "Checkout");
                return;
            }

            if (string.IsNullOrEmpty(keyPath))
            {
                string errorMsg = "<color=red>Error:</color> SSH Key path is empty!";
                SVNLogBridge.LogLine(errorMsg);
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, errorMsg, "Checkout");
                return;
            }

            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0f;
            }

            try
            {
                string absoluteKey = Path.GetFullPath(keyPath).Replace("\\", "/");
                string sshArgs = $"ssh -i \\\"{absoluteKey}\\\" -o StrictHostKeyChecking=no -o BatchMode=yes";
                string sshConfig = $"--config-option config:tunnels:ssh=\"{sshArgs}\"";
                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive --force";

                if (!Directory.Exists(path)) Directory.CreateDirectory(path);

                SVNLogBridge.LogLine($"<b>Starting Checkout...</b>\nPath: <color=green>{path}</color>", append: false);

                await SvnRunner.RunLiveAsync(cmd, "", (line) =>
                {
                    _mainThreadContext.Post(_ =>
                    {
                        if (!string.IsNullOrWhiteSpace(line) && line.Length > 4)
                        {
                            // Przekazujemy label, aby dopasować się do sygnatury (4 argumenty)
                            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, $"<color=yellow>Processing:</color> {line.Trim()}", "Checkout", false);
                        }
                    }, null);
                }, _checkoutCTS.Token);

                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Finalizing & Verifying...</color>", "Checkout");

                await Task.Delay(1500);
                RegisterNewProjectAfterCheckout(path, url, keyPath);
                svnManager.WorkingDir = path;

                await svnManager.RefreshRepositoryInfo();
                await Task.Delay(1500);
                await svnManager.RefreshStatus();

                SVNLogBridge.LogLine("<color=green>SUCCESS:</color> Checkout completed and project initialized.");
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=green>Checkout Finished!</color>", "Checkout");

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(1000);
                    svnManager.PanelHandler.Button_CloseCheckout();
                }
            }
            catch (OperationCanceledException)
            {
                string cancelMsg = "<color=yellow>Checkout cancelled by user.</color>";
                SVNLogBridge.LogLine(cancelMsg);
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, cancelMsg, "Checkout");
            }
            catch (Exception ex)
            {
                string errorMsg = $"<color=red>Checkout Error:</color> {ex.Message}";
                SVNLogBridge.LogLine(errorMsg);
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, errorMsg, "Checkout");
            }
            finally
            {
                IsProcessing = false;
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.gameObject.SetActive(false);
                _checkoutCTS?.Dispose();
                _checkoutCTS = null;

                svnManager.GetModule<SVNStatus>().RefreshLocal();
            }
        }

        public void CancelCheckout()
        {
            _checkoutCTS?.Cancel();
        }

        private void RegisterNewProjectAfterCheckout(string path, string repoUrl, string keyPath)
        {
            var newProj = new SVNProject
            {
                projectName = Path.GetFileName(path.TrimEnd('/', '\\')),
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
                SVNLogBridge.LogLine($"<color=green>Project '{newProj.projectName}' added to Selection List.</color>");
            }

            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", path);
            PlayerPrefs.Save();
        }
    }
}