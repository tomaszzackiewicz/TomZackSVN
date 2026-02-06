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

            // 1. Pobranie danych z UI
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

            // 2. Przygotowanie stanu operacji
            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0f;
            }
            if (svnUI.CheckoutStatusInfoText != null) svnUI.CheckoutStatusInfoText.text = "Initializing Checkout...";

            // 3. MONITOROWANIE LOGÓW (W¹tek t³a analizuj¹cy tekst dopisywany przez SvnRunner)
            bool isMonitoring = true;
            _ = Task.Run(async () =>
            {
                while (isMonitoring && IsProcessing)
                {
                    await Task.Delay(100); // Sprawdzaj co 100ms

                    // U¿ywamy SynchronizationContext zamiast UnityMainThreadDispatcher
                    _mainThreadContext.Post(_ =>
                    {
                        if (svnUI.LogText == null || !IsProcessing) return;

                        string fullLog = svnUI.LogText.text;
                        if (string.IsNullOrEmpty(fullLog)) return;

                        // Szukamy ostatniej linii operacji SVN (A = Added, U = Updated)
                        string[] lines = fullLog.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            for (int i = lines.Length - 1; i >= 0; i--)
                            {
                                string line = lines[i].Trim();
                                // SVN oznacza checkout pliku liter¹ A (Added)
                                if (line.StartsWith("A  ") || line.StartsWith("U  "))
                                {
                                    string fileName = line.Substring(3).Trim();
                                    if (fileName.Length > 45) fileName = "..." + fileName.Substring(fileName.Length - 45);

                                    if (svnUI.CheckoutStatusInfoText != null)
                                        svnUI.CheckoutStatusInfoText.text = $"Downloading: <color=cyan>{fileName}</color>";

                                    // Animujemy pasek "na ¿ywo" przy ka¿dym pliku
                                    if (svnUI.OperationProgressBar != null)
                                        svnUI.OperationProgressBar.value = (svnUI.OperationProgressBar.value + 0.002f) % 1.0f;

                                    break;
                                }
                            }
                        }
                    }, null);
                }
            });

            // 4. WYKONANIE PROCESU SVN
            try
            {
                // Konfiguracja SSH
                string safeKey = keyPath.Replace("\\", "/");
                string sshConfig = !string.IsNullOrEmpty(safeKey)
                    ? $"--config-option config:tunnels:ssh=\"ssh -i '{safeKey}' -o StrictHostKeyChecking=no\""
                    : "";

                string cmd = $"checkout \"{url}\" \"{path}\" {sshConfig} --non-interactive --force";

                if (!Directory.Exists(path)) Directory.CreateDirectory(path);

                svnUI.LogText.text += $"<b>Starting Checkout...</b>\nPath: <color=cyan>{path}</color>\n";

                // Wywo³anie Twojego SvnRunnera
                await SvnRunner.RunAsync(cmd, "", true, _checkoutCTS.Token);

                // Sukces
                svnUI.LogText.text += "<color=green>SUCCESS:</color> Checkout completed.\n";
                if (svnUI.CheckoutStatusInfoText != null) svnUI.CheckoutStatusInfoText.text = "<color=green>Checkout Finished!</color>";
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1f;

                // Rejestracja projektu w systemie
                RegisterNewProjectAfterCheckout(path, url, keyPath);

                // Odœwie¿enie danych Managera
                svnManager.WorkingDir = path;
                await svnManager.RefreshRepositoryInfo();
                svnManager.UpdateBranchInfo();
                svnManager.RefreshStatus();

                // Automatyczne zamkniêcie panelu po sukcesie
                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(1000);
                    svnManager.PanelHandler.Button_CloseCheckout();
                }
            }
            catch (OperationCanceledException)
            {
                svnUI.LogText.text += "<color=orange>CANCELLED:</color> Checkout stopped by user.\n";
                if (svnUI.CheckoutStatusInfoText != null) svnUI.CheckoutStatusInfoText.text = "Cancelled.";
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
                //HideProgressBarAfterDelay(2.0f);
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