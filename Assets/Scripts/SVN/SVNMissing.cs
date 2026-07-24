using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNMissing : SVNBase
    {
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int BatchSize = 25;

        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        public async void FixMissingFiles()
        {
            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock) return;

                if (IsProcessing) return;
                IsProcessing = true;

                // Reset tokena, aby mieć świeży mechanizm anulowania
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                SVNLogBridge.LogLine("<b>[Missing Files]</b> Scanning for items removed from disk...");

                await svnManager.CancelBackgroundTasksAsync();
                int removedCount = await FixMissingLogicAsync(token);

                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearCurrentData();

                    // Bezpieczne czyszczenie UI – wywoływane po zakończeniu asynchronicznej logiki
                    svnUI.SvnTreeView?.ClearView();
                    svnUI.SVNCommitTreeDisplay?.ClearView();

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

                    svnManager.ExpandedPaths.Clear();
                    svnManager.ExpandedPaths.Add("");

                    SVNLogBridge.LogLine("<color=#4FC3F7>Refreshing SVN status...</color>");
                    await statusModule.ExecuteRefreshWithAutoExpand(force: true);

                    if (statusModule.GetCurrentData().Count == 0)
                    {
                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
                        if (svnUI.CommitTreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);
                    }
                }

                if (removedCount > 0)
                    SVNLogBridge.LogLine($"<color=green><b>SUCCESS!</b></color> Removed {removedCount} missing file(s) from SVN index.");
                else
                    SVNLogBridge.LogLine("<color=yellow>No missing files found.</color>");
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Fix missing files cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>FixMissing Error:</color> {ex.Message}");
                SVNLogBridge.LogError($"[SVN] FixMissing: {ex}");
            }
            finally
            {
                IsProcessing = false;
                if (hasLock) _operationLock.Release();
                _cts?.Dispose();
                _cts = null;
            }
        }

        public async Task<int> FixMissingLogicAsync(CancellationToken token = default)
        {
            string root = svnManager.WorkingDir;
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
            token.ThrowIfCancellationRequested();

            var missingFiles = statusDict
                .Where(x => x.Value.status.Contains("!"))
                .Select(x => x.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Sortuj od najgłębszych ścieżek do rodziców, aby uniknąć błędów
                // "parent is not known to exist" podczas usuwania dzieci.
                .OrderByDescending(x => x.Length)
                .ToList();

            if (missingFiles.Count == 0)
                return 0;

            SVNLogBridge.LogLine($"Found <b>{missingFiles.Count}</b> missing files. Removing from SVN index...");

            int total = missingFiles.Count;
            int processed = 0;
            int failed = 0;

            for (int i = 0; i < total; i += BatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = missingFiles.Skip(i).Take(BatchSize).ToList();
                string tempFile = Path.Combine(Path.GetTempPath(), $"svn_missing_{Guid.NewGuid():N}.txt");

                try
                {
                    // Zapisujemy bez BOM, aby uniknąć problemów z pierwszą ścieżką
                    File.WriteAllLines(tempFile, batch, new UTF8Encoding(false));

                    // Używamy SvnRunner, aby zachować globalny lock, konfigurację SSH i retry.
                    await SvnRunner.RunAsync($"delete --force --targets \"{tempFile}\"", root, false, token);
                    processed += batch.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed += batch.Count;
                    SVNLogBridge.LogError($"[SVN] Batch delete error: {ex.Message}");
                    // Nie przerywamy całej operacji – próbujemy kontynuować z następną partią
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }

                // Logujemy postęp nawet przy częściowych błędach
                SVNLogBridge.LogLine($"  Progress: {processed}/{total} files removed.");
            }

            if (failed > 0)
                SVNLogBridge.LogLine($"<color=#FFAA00>Warning:</color> {failed} file(s) could not be removed due to errors.");

            return processed;
        }
    }
}