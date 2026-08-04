using System;
using System.Collections.Generic;
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
                SVNLogBridge.LogToOutput($"<color=#FFAA00>FixMissing Error:</color> {ex.Message}");
                SVNLogBridge.LogErrorToOutput($"[SVN] FixMissing: {ex}");
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
            SVNLogBridge.LogLine($"Found <b>{cleanMissingFiles.Count}</b> missing items." + (skippedNested > 0 ? $" <color=yellow>(Optimized: skipped {skippedNested} nested files to prevent errors)</color>" : ""));
            SVNLogBridge.LogLine("Removing from SVN index...");

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
                    File.WriteAllLines(tempFile, batch, new UTF8Encoding(false));
                    await SvnRunner.RunAsync($"delete --force --targets \"{tempFile}\"", root, false, token);
                    processed += batch.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=yellow>Batch failed ({ex.Message}). Retrying files individually...</color>");

                    foreach (var singleFile in batch)
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"delete --force \"{singleFile}\"", root, false, token);
                            processed++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }

                SVNLogBridge.LogLine($"  Progress: {processed}/{total} files removed.", false);
            }

            if (failed > 0)
                SVNLogBridge.LogLine($"<color=#FFAA00>Warning:</color> {failed} file(s) could not be removed due to errors.");

            return processed;
        }
    }
}