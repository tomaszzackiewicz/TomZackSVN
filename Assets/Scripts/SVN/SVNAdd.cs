using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        private CancellationTokenSource _activeCTS;
        // Limit linii w konsoli, aby UI nie "puchło"
        private const int MaxLogLines = 50;
        private List<string> _logBuffer = new List<string>();

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void AddAll()
        {
            CancellationToken token = PrepareNewOperation();

            try
            {
                string root = svnManager.WorkingDir;

                // Czysty start konsoli
                ClearAndLog("<b>[Recursive Scan]</b> Initiating light-weight synchronization...");

                // KROK 1: Szybki skan (bez logowania każdego pliku)
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                int totalToProcess = rawStatus.Split('\n').Count(l => l.StartsWith("?"));

                if (totalToProcess == 0)
                {
                    UpdateLightLog("<color=green>No new files to add.</color>");
                    return;
                }

                UpdateLightLog($"Found <b>{totalToProcess}</b> new items. Adding recursively...");

                // KROK 2: Uruchomienie SVN (Add)
                // Używamy Task.Run, aby operacja tekstowa nie blokowała wątku głównego Unity
                string addResult = await Task.Run(() => SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, token));

                token.ThrowIfCancellationRequested();

                // KROK 3: Przetwarzanie wyniku w sposób lekki dla UI
                ParseAddResultLight(addResult, totalToProcess);

                UpdateLightLog("\n<color=green><b>[SUCCESS]</b> Sync complete.</color>");
                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                UpdateLightLog("\n<color=orange>Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                UpdateLightLog($"\n<color=red>Error: {ex.Message}</color>");
            }
            finally
            {
                CleanUpOperation(token);
            }
        }

        private void ParseAddResultLight(string result, int total)
        {
            if (string.IsNullOrEmpty(result)) return;

            string[] lines = result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            int count = lines.Length;

            // Zamiast listować wszystko, dajemy tylko podsumowanie co jakiś czas
            UpdateLightLog($"<color=#4FC3F7>Processed {count} items...</color>");

            if (svnUI.OperationProgressBar != null)
                svnUI.OperationProgressBar.value = 1.0f;
        }

        // --- Lekki System Logowania ---

        private void ClearAndLog(string initialMessage)
        {
            _logBuffer.Clear();
            _logBuffer.Add(initialMessage);
            SyncBufferWithUI();
        }

        private void UpdateLightLog(string message)
        {
            _logBuffer.Add(message);

            // Jeśli logów jest za dużo, usuwamy najstarsze (zachowując nagłówek)
            if (_logBuffer.Count > MaxLogLines)
            {
                _logBuffer.RemoveAt(1); // Usuwamy linię zaraz po nagłówku
            }

            SyncBufferWithUI();
        }

        private void SyncBufferWithUI()
        {
            if (svnUI.CommitConsoleContent == null) return;

            // Budujemy jeden zbiorczy string zamiast wielokrotnych aktualizacji pola tekstowego
            StringBuilder sb = new StringBuilder();
            foreach (var line in _logBuffer)
            {
                sb.AppendLine(line);
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: false);
        }

        // --- Zarządzanie Stanem ---

        private CancellationToken PrepareNewOperation()
        {
            if (_activeCTS != null) { _activeCTS.Cancel(); _activeCTS.Dispose(); }
            _activeCTS = new CancellationTokenSource();
            IsProcessing = true;
            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.1f;
            }
            return _activeCTS.Token;
        }

        private void CleanUpOperation(CancellationToken token)
        {
            if (_activeCTS != null && _activeCTS.Token == token)
            {
                IsProcessing = false;
                _activeCTS.Dispose();
                _activeCTS = null;
                HideProgressWithDelay(1.5f);
            }
        }

        private async void HideProgressWithDelay(float delay)
        {
            await Task.Delay((int)(delay * 1000));
            if (!IsProcessing && svnUI.OperationProgressBar != null)
                svnUI.OperationProgressBar.gameObject.SetActive(false);
        }
    }
}