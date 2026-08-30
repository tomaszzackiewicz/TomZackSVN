using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
                LogToConsole("<color=orange><b>[Missing Files]</b> Cancel requested...</color>");
            }
            catch (ObjectDisposedException) { }
        }

        public async void FixMissingFiles()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
                return;

            bool hasLock = false;
            CancellationTokenSource localCts = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock) return;

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

                    PostToMainThread(() =>
                    {
                        if (statusModule.GetCurrentData().Count == 0)
                        {
                            if (svnUI.TreeDisplay != null)
                                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
                            if (svnUI.CommitTreeDisplay != null)
                                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);
                        }
                    });
                }

                if (removedCount > 0)
                    LogToConsole($"<color=green><b>SUCCESS!</b></color> Removed {removedCount} missing file(s) from SVN index.");
                else
                    LogToConsole("<color=yellow>No missing files found.</color>");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange>Fix missing files cancelled.</color>");
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
                    // === FIX D1: pole zdejmowane pod CompareExchange (jak było — to
                    // poprawnie odcina Cancel), a dispose ODRACZONY — token może być
                    // jeszcze zarejestrowany w umierającym Task.Delay/SvnRunner.
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
                .Select(p => SvnRunner.NormalizeRepositoryPath(p.Trim().Replace('\\', '/')))
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