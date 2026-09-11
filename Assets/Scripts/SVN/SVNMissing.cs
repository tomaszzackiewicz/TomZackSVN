using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMissing : SVNBase, IDisposable
    {
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int BatchSize = 500;
        private int _processingFlag;
        private int _disposed;

        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        private void LogToConsole(string msg, bool append = true)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            PostToMainThread(() => SVNLogBridge.LogLine(msg, append));
        }

        public void Cancel()
        {
            try
            {
                var cts = Volatile.Read(ref _cts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
                LogToConsole("<color=#8B0000>><b>[Missing Files]</b> Cancel requested...</color>");
            }
            catch (ObjectDisposedException) { }
        }

        public async void FixMissingFiles()
        {
            // === FIX: double-click confirmation (analogicznie do Revert)
            // Operacja jest destrukcyjna (usuwa ścieżki z indeksu SVN).
            float clickTime = Time.unscaledTime;
            if (!ConfirmFixMissing(clickTime))
                return;

            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                // === FIX: było CICHE return — user potwierdził dwuklikiem destrukcyjną
                // operację i nic się nie stało bez słowa (rodzina buga z SVNRevert).
                // (Jeśli _processingFlag i _operationLock strzegą tego samego i flaga
                // nie jest używana przez inne metody klasy — rozważ usunięcie flagi
                // i zostawienie samego semafora z komunikatem, wzorzec SVNRevert.)
                LogToConsole("<color=#FFAA00><b>[Missing Files]</b> Another operation is already in progress.</color>");
                return;
            }

            bool hasLock = false;
            CancellationTokenSource localCts = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock)
                {
                    LogToConsole("<color=#FFAA00><b>[Missing Files]</b> Another operation is already in progress.</color>");
                    return;
                }

                // === FIX: walidacja katalogu na wejściu (wzorzec z reszty modułów) —
                // wcześniej pusty WorkingDir leciał aż do svn-komend i kończył się
                // enigmatycznym "FixMissing Error"
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory does not exist.");
                    return;
                }

                IsProcessing = true;

                localCts = new CancellationTokenSource();
                Volatile.Write(ref _cts, localCts);
                var token = localCts.Token;

                LogToConsole("<b>[Missing Files]</b> Scanning for items removed from disk...");

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                int removedCount = await FixMissingLogicAsync(token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token).ConfigureAwait(false);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    LogToConsole("<color=#4FC3F7>Refreshing SVN status...</color>");

                    await statusModule.ExecuteRefreshWithAutoExpand(force: true).ConfigureAwait(false);

                    // === trivia: ten blok jest redundantny — pusta ścieżka refreshu
                    // (ShowEmptyState) sama pisze "No changes detected." — zostawiam
                    // jako no-op bezpieczny, do usunięcia przy okazji
                }

                if (removedCount > 0)
                    LogToConsole($"<color=green><b>SUCCESS!</b></color> Removed {removedCount} missing file(s) from SVN index.");
                else
                    LogToConsole("<color=yellow>No missing files found.</color>");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=#8B0000>>Fix missing files cancelled.</color>");
            }
            catch (Exception ex)
            {
                PostToMainThread(() =>
                {
                    SVNLogBridge.LogToOutput($"<color=#FFAA00>FixMissing Error:</color> {ex.Message}");
                    SVNLogBridge.LogErrorToOutput($"[SVN] FixMissing: {ex}");
                });
            }
            finally
            {
                IsProcessing = false;

                if (localCts != null)
                {
                    Interlocked.CompareExchange(ref _cts, null, localCts);
                    _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
                }

                if (hasLock)
                {
                    try { _operationLock.Release(); }
                    catch (SemaphoreFullException) { }
                    catch (ObjectDisposedException) { }
                }

                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        // === NOWE: potwierdzenie (pola klasy)
        private float _lastFixMissingClickTime = -10f;

        private bool ConfirmFixMissing(float currentTime)
        {
            const float ConfirmationWindow = 5f;
            const float MinDoubleClickDelay = 0.30f;

            float elapsed = currentTime - _lastFixMissingClickTime;

            if (elapsed > ConfirmationWindow || _lastFixMissingClickTime < 0f)
            {
                _lastFixMissingClickTime = currentTime;
                LogToConsole(
                    "<color=#FFAA00><b>[Missing Files]</b></color> This will permanently remove missing items from the SVN index!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm.");
                return false;
            }

            if (elapsed < MinDoubleClickDelay)
            {
                _lastFixMissingClickTime = currentTime;
                LogToConsole("<color=#FFAA00><b>[Missing Files]</b></color> Confirmation too fast — press once again.");
                return false;
            }

            _lastFixMissingClickTime = -10f;
            return true;
        }

        public async Task<int> FixMissingLogicAsync(CancellationToken token = default)
        {
            string root = svnManager.WorkingDir;
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var missingFiles = statusDict
                .Where(x => x.Value.status.StartsWith("!", StringComparison.Ordinal))
                .Select(x => x.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missingFiles.Count == 0)
                return 0;

            var cleanMissingFiles = missingFiles
                .Select(p => SvnRunner.SanitizeSvnPath(p.Trim().Replace('\\', '/')))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var sortedMissing = cleanMissingFiles.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

            var filteredMissing = new List<string>(sortedMissing.Count);
            foreach (var path in sortedMissing)
            {
                bool isNested = filteredMissing.Any(parent => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));
                if (!isNested)
                {
                    filteredMissing.Add(path);
                }
            }

            int skippedNested = cleanMissingFiles.Count - filteredMissing.Count;
            LogToConsole($"Found <b>{cleanMissingFiles.Count}</b> missing items." + (skippedNested > 0 ? $" <color=yellow>(Optimized: skipped {skippedNested} nested files to prevent errors)</color>" : ""));
            LogToConsole("Removing from SVN index...");

            int total = filteredMissing.Count;
            int processed = 0;
            int failed = 0;

            for (int i = 0; i < total; i += BatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = filteredMissing.Skip(i).Take(BatchSize).ToList();
                string tempFile = Path.Combine(Path.GetTempPath(), $"svn_missing_{Guid.NewGuid():N}.txt");

                try
                {
                    await Task.Run(() => File.WriteAllLines(tempFile, batch, new UTF8Encoding(false)), token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"delete --force --targets \"{tempFile}\"", root, false, token).ConfigureAwait(false);
                    processed += batch.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogToConsole($"<color=yellow>Batch failed ({ex.Message}). Retrying files individually...</color>");

                    foreach (var singleFile in batch)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"delete --force \"{singleFile}\"", root, false, token).ConfigureAwait(false);
                            processed++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception singleEx)
                        {
                            failed++;
                            LogToConsole($"<color=#FFAA00>Failed to remove:</color> {singleFile} - {singleEx.Message}");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }

                LogToConsole($"  Progress: {processed}/{total} files removed.", false);
            }

            if (failed > 0)
                LogToConsole($"<color=#FFAA00>Warning:</color> {failed} file(s) could not be removed due to errors.");

            return processed;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            Cancel();

            var localCts = Interlocked.Exchange(ref _cts, null);
            if (localCts != null)
            {
                // === FIX D2: delayed dispose CTS + opóźniony dispose semafora —
                // operacja potrzebuje chwili od Cancel do dotarcia do finally,
                // a Release na zlikwidowanym semaforze to ODE (ratowany catchem,
                // ale po co).
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    try { localCts.Dispose(); } catch { }
                    try { _operationLock.Dispose(); } catch { }
                });
            }
            else
            {
                try { _operationLock.Dispose(); } catch { }
            }

            GC.SuppressFinalize(this);
        }
    }
}