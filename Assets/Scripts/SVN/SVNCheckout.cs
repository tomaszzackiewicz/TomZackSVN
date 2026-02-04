using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
                RegisterNewProjectAfterCheckout(path, url, keyPath);

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

                // Zamiast SaveInputsToSettings, u¿ywamy nowej logiki:
                RegisterNewProjectAfterCheckout(path, url, keyPath);

                // Reszta odœwie¿ania pozostaje bez zmian
                svnManager.WorkingDir = path;
                await svnManager.RefreshRepositoryInfo();
                svnManager.UpdateBranchInfo();
                svnManager.RefreshStatus();
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
            // 1. Tworzymy obiekt nowego projektu
            var newProj = new SVNProject
            {
                projectName = Path.GetFileName(path), // Automatyczna nazwa z folderu
                repoUrl = repoUrl,
                workingDir = path,
                privateKeyPath = keyPath,
                lastOpened = DateTime.Now
            };

            // 2. Pobieramy listê i dodajemy projekt (o ile ju¿ nie istnieje)
            List<SVNProject> projects = ProjectSettings.LoadProjects();
            if (!projects.Exists(p => p.workingDir == path))
            {
                projects.Add(newProj);
                ProjectSettings.SaveProjects(projects);
                svnUI.LogText.text += $"<color=green>Project '{newProj.projectName}' added to Selection List.</color>\n";
            }

            // 3. Ustawiamy go jako ostatnio otwarty
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", path);
            PlayerPrefs.Save();
        }
    }
}