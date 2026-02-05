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

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager) { }

        public async void Checkout()
        {
            if (IsProcessing) return;

            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            string keyPath = string.IsNullOrWhiteSpace(svnUI.CheckoutPrivateKeyInput.text)
                             ? SvnRunner.KeyPath
                             : svnUI.CheckoutPrivateKeyInput.text.Trim();

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Provide both URL and Destination Path.\n";
                return;
            }

            if (Directory.Exists(path))
            {
                if (Directory.Exists(Path.Combine(path, ".svn")))
                {
                    svnUI.LogText.text += "<color=red>Abort:</color> Destination already contains an SVN repository!\n";
                    return;
                }

                if (Directory.GetFileSystemEntries(path).Length > 0)
                {
                    svnUI.LogText.text += "<color=orange>Warning:</color> Destination folder is not empty. Proceeding with --force...\n";
                }
            }

            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            try
            {
                RegisterNewProjectAfterCheckout(path, url, keyPath);

                string safeKey = keyPath.Replace("\\", "/");
                string sshConfig = !string.IsNullOrEmpty(safeKey)
                    ? $"--config-option config:tunnels:ssh=\"ssh -i '{safeKey}' -o StrictHostKeyChecking=no\""
                    : "";

                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive --force";

                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                svnUI.LogText.text += $"<b>Starting Checkout...</b>\nPath: <color=cyan>{path}</color>\n";

                await SvnRunner.RunAsync(cmd, "", true, _checkoutCTS.Token);

                svnUI.LogText.text += "<color=green>SUCCESS:</color> Checkout completed.\n";

                RegisterNewProjectAfterCheckout(path, url, keyPath);

                svnManager.WorkingDir = path;
                await svnManager.RefreshRepositoryInfo();
                svnManager.UpdateBranchInfo();
                svnManager.RefreshStatus();

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(500);
                    svnManager.PanelHandler.Button_CloseCheckout();
                }
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